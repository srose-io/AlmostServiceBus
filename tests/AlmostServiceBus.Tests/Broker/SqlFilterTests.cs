using AlmostServiceBus.Core.Broker;

namespace AlmostServiceBus.Tests.Broker;

/// <summary>
/// The SQL filter evaluator, against Azure's documented grammar and semantics.
/// </summary>
/// <remarks>
/// The <see cref="S6Table"/> cases are the measurement that motivated this evaluator: every
/// expression there was measured against the previous regex ladder and passed messages it had no
/// business passing. The messages and the expectations are the ones the probe sends.
/// </remarks>
public class SqlFilterTests
{
    private static BrokeredMessage Message(string? subject = null, Dictionary<string, object>? properties = null)
    {
        var message = new BrokeredMessage
        {
            Body = System.Text.Encoding.UTF8.GetBytes("body"),
            Subject = subject
        };

        if (properties is not null)
            foreach (var (key, value) in properties)
                message.ApplicationProperties[key] = value;

        return message;
    }

    private static bool Matches(string expression, BrokeredMessage message) =>
        new RuleEntity { Name = "r", FilterType = FilterType.SqlFilter, SqlExpression = expression }
            .Matches(message);

    private static bool? Evaluate(string expression, BrokeredMessage message) =>
        SqlFilterExpression.Parse(expression).Evaluate(message);

    // ══════════════════════════════════════════════════════════════════════════
    // The S6 table: the four probe messages, and which of them each rule passes.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>A: a tenant message with everything set.</summary>
    private static BrokeredMessage MessageA() => Message("Created", new()
    {
        ["TenantId"] = "loom-full",
        ["EntityType"] = "dbo_Student",
        ["PrivateKeys"] = "x_loom-full_1",
        ["messageType"] = "Other"
    });

    /// <summary>B: the same tenant, nothing else.</summary>
    private static BrokeredMessage MessageB() => Message("Updated", new()
    {
        ["TenantId"] = "loom-full"
    });

    /// <summary>C: another tenant.</summary>
    private static BrokeredMessage MessageC() => Message("Created", new()
    {
        ["TenantId"] = "loom-other",
        ["EntityType"] = "dbo_Student",
        ["messageType"] = "MergeJobStatus",
        ["TenantType"] = "eds"
    });

    /// <summary>D: no properties and no subject at all.</summary>
    private static BrokeredMessage MessageD() => Message();

    public static TheoryData<string, string, string> S6Table => new()
    {
        // name, expression, the messages it must pass (and only those)
        {
            "in-and-in",
            "user.TenantId IN ('loom-full','LOOM-FULL') AND user.EntityType IN ('dbo_Student','dbo_X')",
            "A"
        },
        {
            "eq-and-eq",
            "user.TenantId = 'loom-full' AND user.EntityType = 'dbo_Student'",
            "A"
        },
        {
            "in-and-eq",
            "user.TenantId IN ('loom-full','LOOM-FULL') AND user.EntityType = 'dbo_Student'",
            "A"
        },
        {
            "paren-in-and-in",
            "(user.TenantId IN ('loom-full','LOOM-FULL')) AND (user.EntityType IN ('dbo_Student','dbo_X'))",
            "A"
        },
        {
            "in-only",
            "user.TenantId IN ('loom-full','LOOM-FULL')",
            "AB"
        },
        {
            "label-and-in",
            "sys.Label = 'Created' AND user.TenantId IN ('loom-full','LOOM-FULL')",
            "A"
        },
        {
            "in-and-label",
            "user.TenantId IN ('loom-full','LOOM-FULL') AND sys.Label = 'Created'",
            "A"
        },
        {
            "like",
            "user.PrivateKeys LIKE '%loom-full_%'",
            "A"
        },
        {
            // S6 predicted "two messages" (A and B). Azure documents `[NOT] IN` as UNKNOWN when
            // the left operand is unknown, and B has no `messageType` at all, so only A passes.
            // See SqlFilter_NotIn_MissingProperty_IsUnknown below.
            "not-in",
            "user.messageType NOT IN ('MergeJobStatus','EdsJobStatus')",
            "A"
        },
        {
            "isnull-or",
            "user.TenantType IS NULL OR user.tenantType <> 'eds'",
            "ABD"
        },
    };

    [Theory]
    [MemberData(nameof(S6Table))]
    public void S6_FilterTable_PassesExactlyTheExpectedMessages(string name, string expression, string expected)
    {
        var messages = new (char Name, BrokeredMessage Message)[]
        {
            ('A', MessageA()), ('B', MessageB()), ('C', MessageC()), ('D', MessageD())
        };

        var passed = string.Concat(messages.Where(m => Matches(expression, m.Message)).Select(m => m.Name));

        Assert.Equal(expected, passed);
        Assert.False(string.IsNullOrEmpty(name));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Every rule shape the InPlace CLI's Broker.cs generates.
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TenantRule_MatchesEitherCasing_AndNoOtherTenant()
    {
        const string expression = "user.TenantId IN ('loom-full','LOOM-FULL') AND user.EntityType IN ('dbo_Student','dbo_Agency')";

        Assert.True(Matches(expression, Message(properties: new() { ["TenantId"] = "loom-full", ["EntityType"] = "dbo_Student" })));
        Assert.True(Matches(expression, Message(properties: new() { ["TenantId"] = "LOOM-FULL", ["EntityType"] = "dbo_Agency" })));
        Assert.False(Matches(expression, Message(properties: new() { ["TenantId"] = "loom-other", ["EntityType"] = "dbo_Student" })));
        Assert.False(Matches(expression, Message(properties: new() { ["TenantId"] = "loom-full", ["EntityType"] = "dbo_Other" })));
        // A tenant with the wrong case that is not in the list is a different tenant.
        Assert.False(Matches(expression, Message(properties: new() { ["TenantId"] = "Loom-Full", ["EntityType"] = "dbo_Student" })));
    }

    [Fact]
    public void LabelRule_ReadsSubject_UnderEitherSysName()
    {
        const string expression = "sys.Label = 'Created' AND user.TenantId IN ('loom-full','LOOM-FULL')";

        Assert.True(Matches(expression, Message("Created", new() { ["TenantId"] = "loom-full" })));
        Assert.False(Matches(expression, Message("Updated", new() { ["TenantId"] = "loom-full" })));
        Assert.False(Matches(expression, Message(null, new() { ["TenantId"] = "loom-full" })));

        // sys.Subject is the current SDK's name for the same value.
        Assert.True(Matches("sys.Subject = 'Created'", Message("Created")));
        Assert.True(Matches("SYS.LABEL = 'Created'", Message("Created")));
    }

    [Fact]
    public void LikeRule_WildcardAndUnderscore()
    {
        const string expression = "user.PrivateKeys LIKE '%loom-full_%'";

        Assert.True(Matches(expression, Message(properties: new() { ["PrivateKeys"] = "x_loom-full_1" })));
        Assert.True(Matches(expression, Message(properties: new() { ["PrivateKeys"] = "loom-full-1" })));
        // `_` needs exactly one character after "loom-full".
        Assert.False(Matches(expression, Message(properties: new() { ["PrivateKeys"] = "x_loom-full" })));
        Assert.False(Matches(expression, Message(properties: new() { ["PrivateKeys"] = "loom-other_1" })));
    }

    [Fact]
    public void NotInRule_PassesAnythingNotListed()
    {
        const string expression = "user.messageType NOT IN ('MergeJobStatus','EdsJobStatus')";

        Assert.True(Matches(expression, Message(properties: new() { ["messageType"] = "Other" })));
        Assert.False(Matches(expression, Message(properties: new() { ["messageType"] = "MergeJobStatus" })));
        Assert.False(Matches(expression, Message(properties: new() { ["messageType"] = "EdsJobStatus" })));
    }

    [Fact]
    public void IsNullOrRule_IsCaseInsensitiveInPropertyNames()
    {
        const string expression = "user.TenantType IS NULL OR user.tenantType <> 'eds'";

        Assert.True(Matches(expression, Message()));
        Assert.True(Matches(expression, Message(properties: new() { ["TenantType"] = "standard" })));
        Assert.False(Matches(expression, Message(properties: new() { ["TenantType"] = "eds" })));
        // The rule spells the property both ways; both must reach the same value.
        Assert.False(Matches(expression, Message(properties: new() { ["tenanttype"] = "eds" })));
    }

    [Fact]
    public void NumericInRule_MatchesIntegerProperties()
    {
        const string expression = "user.InFlowActionType IN (1,2,3)";

        Assert.True(Matches(expression, Message(properties: new() { ["InFlowActionType"] = 1 })));
        Assert.True(Matches(expression, Message(properties: new() { ["InFlowActionType"] = 3L })));
        Assert.False(Matches(expression, Message(properties: new() { ["InFlowActionType"] = 4 })));
        Assert.False(Matches(expression, Message()));
    }

    [Fact]
    public void MessageTypeOrActionTypeRule()
    {
        const string expression = "user.MessageType = 'a' OR user.InSightActionType IS NOT NULL";

        Assert.True(Matches(expression, Message(properties: new() { ["MessageType"] = "a" })));
        Assert.True(Matches(expression, Message(properties: new() { ["InSightActionType"] = 7 })));
        // MessageType is unknown, but IS NOT NULL is two-valued and true, so OR is true.
        Assert.True(Matches(expression, Message(properties: new() { ["InSightActionType"] = "x" })));
        Assert.False(Matches(expression, Message(properties: new() { ["MessageType"] = "b" })));
        Assert.False(Matches(expression, Message()));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Three-valued logic
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Comparison_MissingProperty_IsUnknown()
    {
        Assert.Null(Evaluate("user.missing = 'x'", Message()));
        Assert.Null(Evaluate("user.missing <> 'x'", Message()));
        Assert.Null(Evaluate("user.missing > 3", Message()));
        Assert.Null(Evaluate("user.missing LIKE 'x%'", Message()));
        Assert.Null(Evaluate("user.missing NOT LIKE 'x%'", Message()));
        Assert.Null(Evaluate("user.missing IN ('x')", Message()));
        Assert.Null(Evaluate("user.missing NOT IN ('x')", Message()));
    }

    [Fact]
    public void SqlFilter_NotIn_MissingProperty_IsUnknown()
    {
        // Azure: "Unknown evaluation in [NOT] IN: if the left operand is evaluated as unknown,
        // then the result is unknown." NOT IN does not flip unknown to true, so a message with
        // no such property is not delivered.
        var message = Message(properties: new() { ["other"] = "x" });

        Assert.Null(Evaluate("user.messageType NOT IN ('MergeJobStatus')", message));
        Assert.False(Matches("user.messageType NOT IN ('MergeJobStatus')", message));
    }

    [Theory]
    // left, right, AND, OR  — Azure's published truth tables, with U written as null.
    [InlineData("1=1", "1=1", true, true)]
    [InlineData("1=1", "1=0", false, true)]
    [InlineData("1=1", "user.x=1", null, true)]
    [InlineData("1=0", "1=1", false, true)]
    [InlineData("1=0", "1=0", false, false)]
    [InlineData("1=0", "user.x=1", false, null)]
    [InlineData("user.x=1", "1=1", null, true)]
    [InlineData("user.x=1", "1=0", false, null)]
    [InlineData("user.x=1", "user.x=1", null, null)]
    public void LogicalOperators_FollowSqlTruthTables(string left, string right, bool? and, bool? or)
    {
        var message = Message();

        Assert.Equal(and, Evaluate($"({left}) AND ({right})", message));
        Assert.Equal(or, Evaluate($"({left}) OR ({right})", message));
    }

    [Fact]
    public void Not_PropagatesUnknown()
    {
        Assert.False(Evaluate("NOT 1=1", Message()));
        Assert.True(Evaluate("NOT 1=0", Message()));
        Assert.Null(Evaluate("NOT user.missing = 'x'", Message()));
    }

    [Fact]
    public void IsNull_IsTwoValued()
    {
        Assert.True(Evaluate("user.missing IS NULL", Message()));
        Assert.False(Evaluate("user.missing IS NOT NULL", Message()));
        Assert.True(Evaluate("user.x IS NOT NULL", Message(properties: new() { ["x"] = "v" })));
        Assert.True(Evaluate("sys.Label IS NULL", Message()));
        Assert.True(Evaluate("sys.Label IS NOT NULL", Message("Created")));
    }

    [Fact]
    public void UnknownIsNotAMatch()
    {
        // The whole point: only TRUE delivers.
        Assert.Null(Evaluate("user.missing = 'x'", Message()));
        Assert.False(Matches("user.missing = 'x'", Message()));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Grammar
    // ══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("1=1", true)]
    [InlineData("1 = 1", true)]
    [InlineData("1=0", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("2 > 1", true)]
    [InlineData("2 >= 2", true)]
    [InlineData("1 < 2", true)]
    [InlineData("1 <= 0", false)]
    [InlineData("1 <> 2", true)]
    [InlineData("1 != 1", false)]
    [InlineData("'a' = 'a'", true)]
    [InlineData("'a' = 'A'", false)]
    [InlineData("'it''s' = 'it''s'", true)]
    [InlineData("1 + 2 = 3", true)]
    [InlineData("2 * 3 - 1 = 5", true)]
    [InlineData("7 % 3 = 1", true)]
    [InlineData("6 / 3 = 2", true)]
    [InlineData("-3 + 4 = 1", true)]
    [InlineData("1.5 + 1.5 = 3", true)]
    [InlineData("(1 = 1) AND (2 = 2)", true)]
    [InlineData("1 = 1 OR 1 = 0", true)]
    [InlineData("NOT (1 = 0)", true)]
    [InlineData("NOT 1 = 0", true)]
    [InlineData("1 IN (1, 2, 3)", true)]
    [InlineData("4 NOT IN (1, 2, 3)", true)]
    [InlineData("'b' IN ('a', 'b')", true)]
    public void Literals_AndOperators(string expression, bool expected)
    {
        Assert.Equal(expected, Matches(expression, Message()));
    }

    [Fact]
    public void Precedence_AndBindsTighterThanOr()
    {
        // false AND false OR true  →  (false AND false) OR true  →  true
        Assert.True(Matches("1 = 0 AND 1 = 0 OR 1 = 1", Message()));
        // true OR true AND false  →  true OR (true AND false)  →  true
        Assert.True(Matches("1 = 1 OR 1 = 1 AND 1 = 0", Message()));
        // (true OR true) AND false → false
        Assert.False(Matches("(1 = 1 OR 1 = 1) AND 1 = 0", Message()));
    }

    [Fact]
    public void Precedence_ArithmeticBeforeComparison()
    {
        Assert.True(Matches("1 + 2 * 3 = 7", Message()));
        Assert.True(Matches("(1 + 2) * 3 = 9", Message()));
    }

    [Fact]
    public void Exists_IsTwoValued()
    {
        Assert.True(Matches("EXISTS(user.x)", Message(properties: new() { ["x"] = "v" })));
        Assert.False(Matches("EXISTS(user.x)", Message()));
        Assert.True(Matches("NOT EXISTS(user.x)", Message()));
        Assert.True(Matches("EXISTS(x)", Message(properties: new() { ["x"] = "v" })));
        Assert.True(Matches("exists(sys.Label)", Message("Created")));
        Assert.False(Matches("EXISTS(sys.Label)", Message()));
    }

    [Fact]
    public void PropertyNames_AreCaseInsensitive_ValuesAreNot()
    {
        var message = Message(properties: new() { ["TenantId"] = "loom-full" });

        Assert.True(Matches("user.tenantid = 'loom-full'", message));
        Assert.True(Matches("USER.TENANTID = 'loom-full'", message));
        Assert.True(Matches("tenantid = 'loom-full'", message));
        Assert.False(Matches("user.TenantId = 'LOOM-FULL'", message));
    }

    [Fact]
    public void Keywords_AreCaseInsensitive()
    {
        var message = Message(properties: new() { ["x"] = "a" });

        Assert.True(Matches("user.x in ('a')", message));
        Assert.True(Matches("user.x like 'a'", message));
        Assert.True(Matches("user.x is not null and 1 = 1", message));
        Assert.True(Matches("user.y is null or user.x = 'a'", message));
        Assert.True(Matches("user.x not in ('b')", message));
    }

    [Fact]
    public void Like_SupportsEscape()
    {
        var literal = Message(properties: new() { ["x"] = "ABC%" });
        var wild = Message(properties: new() { ["x"] = "ABCDEF" });

        Assert.True(Matches(@"user.x LIKE 'ABC\%' ESCAPE '\'", literal));
        Assert.False(Matches(@"user.x LIKE 'ABC\%' ESCAPE '\'", wild));
        Assert.True(Matches("user.x LIKE 'ABC%'", wild));
    }

    [Fact]
    public void Like_TreatsRegexMetacharactersLiterally()
    {
        Assert.True(Matches("user.x LIKE 'a.c'", Message(properties: new() { ["x"] = "a.c" })));
        Assert.False(Matches("user.x LIKE 'a.c'", Message(properties: new() { ["x"] = "abc" })));
        Assert.True(Matches("user.x LIKE 'a_c'", Message(properties: new() { ["x"] = "abc" })));
    }

    [Fact]
    public void Numbers_CompareAcrossClrNumericTypes()
    {
        Assert.True(Matches("user.n = 27", Message(properties: new() { ["n"] = (byte)27 })));
        Assert.True(Matches("user.n = 27", Message(properties: new() { ["n"] = (short)27 })));
        Assert.True(Matches("user.n = 27", Message(properties: new() { ["n"] = 27L })));
        Assert.True(Matches("user.n = 27", Message(properties: new() { ["n"] = 27.0 })));
        Assert.True(Matches("user.n = 27.0", Message(properties: new() { ["n"] = 27 })));
        Assert.True(Matches("user.n > 26", Message(properties: new() { ["n"] = 27 })));
    }

    [Fact]
    public void Booleans_Compare()
    {
        Assert.True(Matches("user.flag = true", Message(properties: new() { ["flag"] = true })));
        Assert.False(Matches("user.flag = true", Message(properties: new() { ["flag"] = false })));
        Assert.True(Matches("user.flag", Message(properties: new() { ["flag"] = true })));
    }

    [Fact]
    public void NonBooleanResult_IsNotAMatch()
    {
        // `user.x` alone is a string, not a predicate.
        Assert.False(Matches("user.x", Message(properties: new() { ["x"] = "v" })));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Parse errors
    // ══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("user.TenantId IN")]
    [InlineData("user.TenantId IN (")]
    [InlineData("user.TenantId IN ()")]
    [InlineData("user.TenantId = ")]
    [InlineData("user.TenantId = 'unterminated")]
    [InlineData("AND user.TenantId = 'x'")]
    [InlineData("user.TenantId 'x'")]
    [InlineData("user.TenantId LIKE 5")]
    [InlineData("user.TenantId IS NOT 'x'")]
    [InlineData("(user.TenantId = 'x'")]
    [InlineData("user.TenantId = 'x')")]
    [InlineData("user.x & 1")]
    [InlineData("scope.other.TenantId = 'x'")]
    [InlineData("user.x LIKE 'a' ESCAPE 'ab'")]
    public void ParseErrors_AreRejected(string expression)
    {
        Assert.Throws<SqlFilterParseException>(() => SqlFilterExpression.Parse(expression));
        Assert.False(SqlFilterExpression.TryParse(expression, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void UnparsableFilter_NeverMatches()
    {
        // Belt and braces: the management API rejects these at creation, but a rule built in
        // process with a bad expression must fail closed, not open.
        var rule = new RuleEntity
        {
            Name = "r",
            FilterType = FilterType.SqlFilter,
            SqlExpression = "this is (not sql"
        };

        Assert.False(rule.Matches(Message()));
        Assert.False(rule.Matches(Message(properties: new() { ["anything"] = "at all" })));
        Assert.False(rule.TryValidateSqlFilter(out var error));
        Assert.NotNull(error);
        Assert.Throws<SqlFilterParseException>(rule.ValidateSqlFilter);
    }

    [Fact]
    public void ValidFilter_Validates()
    {
        var rule = new RuleEntity
        {
            Name = "r",
            FilterType = FilterType.SqlFilter,
            SqlExpression = "user.TenantId IN ('a','B')"
        };

        Assert.True(rule.TryValidateSqlFilter(out var error));
        Assert.Null(error);
        rule.ValidateSqlFilter();
    }

    [Fact]
    public void ChangingTheExpression_RecompilesIt()
    {
        var message = Message(properties: new() { ["x"] = "a" });
        var rule = new RuleEntity { Name = "r", FilterType = FilterType.SqlFilter, SqlExpression = "user.x = 'a'" };

        Assert.True(rule.Matches(message));

        rule.SqlExpression = "user.x = 'b'";
        Assert.False(rule.Matches(message));

        rule.SqlExpression = "user.x = 'a'";
        Assert.True(rule.Matches(message));
    }

    [Fact]
    public void NonSqlFilterTypes_AreUntouched()
    {
        Assert.True(new RuleEntity { Name = "r", FilterType = FilterType.TrueFilter }.Matches(Message()));
        Assert.False(new RuleEntity { Name = "r", FilterType = FilterType.FalseFilter }.Matches(Message()));

        // A SqlRuleAction rides along on a rule whose filter still decides delivery.
        var withAction = new RuleEntity
        {
            Name = "r",
            FilterType = FilterType.SqlFilter,
            SqlExpression = "user.x = 'a'",
            ActionExpression = "SET sys.Label = 'handled'"
        };
        Assert.True(withAction.Matches(Message(properties: new() { ["x"] = "a" })));
        Assert.False(withAction.Matches(Message(properties: new() { ["x"] = "b" })));
        Assert.True(withAction.TryValidateSqlFilter(out _));
    }

    [Fact]
    public void EmptyExpression_IsAPassThrough()
    {
        Assert.True(new RuleEntity { Name = "r", FilterType = FilterType.SqlFilter, SqlExpression = null }.Matches(Message()));
        Assert.True(new RuleEntity { Name = "r", FilterType = FilterType.SqlFilter, SqlExpression = "   " }.Matches(Message()));
    }
}
