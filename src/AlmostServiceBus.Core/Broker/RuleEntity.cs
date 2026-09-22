
namespace AlmostServiceBus.Core.Broker;

/// <summary>
/// The filter type applied by a <see cref="RuleEntity"/>.
/// </summary>
public enum FilterType
{
    TrueFilter,
    FalseFilter,
    SqlFilter,
    CorrelationFilter
}

/// <summary>
/// A subscription rule that determines whether a published message should be
/// delivered to the owning subscription.
/// </summary>
public sealed class RuleEntity
{
    public string Name { get; set; } = string.Empty;

    public FilterType FilterType { get; set; } = FilterType.TrueFilter;

    /// <summary>SQL filter expression, used when <see cref="FilterType"/> is <see cref="FilterType.SqlFilter"/>.</summary>
    public string? SqlExpression { get; set; }

    /// <summary>Correlation ID filter value, used when <see cref="FilterType"/> is <see cref="FilterType.CorrelationFilter"/>.</summary>
    public string? CorrelationId { get; set; }

    // ── Additional correlation filter properties ─────────────────────────────

    public string? Subject { get; set; }

    public string? To { get; set; }

    public string? ReplyTo { get; set; }

    public string? SessionId { get; set; }

    public string? ContentType { get; set; }

    /// <summary>Custom properties to match against <see cref="BrokeredMessage.ApplicationProperties"/>.</summary>
    public Dictionary<string, object>? CorrelationFilterProperties { get; set; }

    /// <summary>Optional SQL action expression executed when the rule matches.</summary>
    public string? ActionExpression { get; set; }

    /// <summary>
    /// Evaluates whether this rule matches the given message.
    /// </summary>
    public bool Matches(BrokeredMessage message)
    {
        return FilterType switch
        {
            FilterType.TrueFilter => true,
            FilterType.FalseFilter => false,
            FilterType.CorrelationFilter => MatchesCorrelationFilter(message),
            FilterType.SqlFilter => MatchesSqlFilter(message),
            _ => true
        };
    }

    private bool MatchesCorrelationFilter(BrokeredMessage message)
    {
        if (CorrelationId is not null && !string.Equals(CorrelationId, message.CorrelationId, StringComparison.Ordinal))
            return false;
        if (Subject is not null && !string.Equals(Subject, message.Subject, StringComparison.Ordinal))
            return false;
        if (To is not null && !string.Equals(To, message.To, StringComparison.Ordinal))
            return false;
        if (ReplyTo is not null && !string.Equals(ReplyTo, message.ReplyTo, StringComparison.Ordinal))
            return false;
        if (SessionId is not null && !string.Equals(SessionId, message.SessionId, StringComparison.Ordinal))
            return false;
        if (ContentType is not null && !string.Equals(ContentType, message.ContentType, StringComparison.Ordinal))
            return false;

        // Match custom properties
        if (CorrelationFilterProperties is not null)
        {
            foreach (var (key, value) in CorrelationFilterProperties)
            {
                if (!message.ApplicationProperties.TryGetValue(key, out var msgValue))
                    return false;
                if (!Equals(value, msgValue))
                    return false;
            }
        }

        return true;
    }

    // ── SQL filter evaluation ────────────────────────────────────────────────
    //
    // The expression is parsed once and cached against the text it was parsed from, so a rule
    // whose SqlExpression is replaced (the management API does that on every reconcile) picks
    // the new one up without a stale cache.

    private sealed record CompiledFilter(string Source, SqlFilterExpression? Expression, string? Error);

    private CompiledFilter? _compiled;

    /// <summary>
    /// Parses the SQL expression, throwing <see cref="SqlFilterParseException"/> when it is not
    /// valid. Call this at rule creation: a rule that cannot be parsed must be rejected rather
    /// than installed, because an unparsable filter that matched everything would turn a
    /// filtered subscription into a firehose.
    /// </summary>
    public void ValidateSqlFilter()
    {
        if (FilterType != FilterType.SqlFilter || string.IsNullOrWhiteSpace(SqlExpression))
            return;

        var compiled = Compile(SqlExpression);
        if (compiled.Error is not null)
            throw new SqlFilterParseException(compiled.Error);
    }

    /// <summary>
    /// Returns false and the reason when the SQL expression is not valid, instead of throwing.
    /// </summary>
    public bool TryValidateSqlFilter(out string? error)
    {
        error = null;
        if (FilterType != FilterType.SqlFilter || string.IsNullOrWhiteSpace(SqlExpression))
            return true;

        error = Compile(SqlExpression).Error;
        return error is null;
    }

    private CompiledFilter Compile(string expression)
    {
        var cached = _compiled;
        if (cached is not null && string.Equals(cached.Source, expression, StringComparison.Ordinal))
            return cached;

        CompiledFilter compiled;
        if (SqlFilterExpression.TryParse(expression, out var parsed, out var error))
            compiled = new CompiledFilter(expression, parsed, null);
        else
            compiled = new CompiledFilter(expression, null, error);

        // A lost race only costs one extra parse of the same text.
        _compiled = compiled;
        return compiled;
    }

    private bool MatchesSqlFilter(BrokeredMessage message)
    {
        // An absent expression is the SDK's `1=1`: the rule is a pass-through.
        if (string.IsNullOrWhiteSpace(SqlExpression))
            return true;

        var compiled = Compile(SqlExpression);

        // An expression that did not parse never matches. The management API rejects those at
        // creation, so reaching here means a rule was built in process with a bad expression.
        return compiled.Expression?.Matches(message) ?? false;
    }
}
