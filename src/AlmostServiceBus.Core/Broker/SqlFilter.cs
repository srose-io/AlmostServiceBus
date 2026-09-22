using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AlmostServiceBus.Core.Broker;

/// <summary>
/// Raised when a SQL filter expression cannot be parsed. The management API turns this into a
/// 400 at rule creation, which is what Azure Service Bus does; a rule that cannot be parsed
/// must never be installed, because a filter that silently matches everything turns a filtered
/// subscription into a firehose.
/// </summary>
public sealed class SqlFilterParseException : Exception
{
    public SqlFilterParseException(string message) : base(message)
    {
    }
}

/// <summary>
/// A parsed Azure Service Bus SQL filter expression.
/// </summary>
/// <remarks>
/// <para>
/// The grammar follows Azure's documented SQL filter syntax: <c>=</c>, <c>&lt;&gt;</c>,
/// <c>!=</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&gt;=</c>, <c>IN</c>, <c>NOT IN</c>,
/// <c>LIKE … [ESCAPE …]</c>, <c>NOT LIKE</c>, <c>IS [NOT] NULL</c>, <c>EXISTS(p)</c>,
/// <c>AND</c>, <c>OR</c>, <c>NOT</c>, parentheses, arithmetic (<c>+ - * / %</c>), string,
/// numeric and boolean literals, and the <c>sys.</c> and <c>user.</c> property scopes. An
/// unscoped name is a user property, as Azure documents.
/// </para>
/// <para>
/// Evaluation is three-valued. A comparison that touches a missing property or a null value is
/// UNKNOWN rather than false; <c>AND</c>, <c>OR</c> and <c>NOT</c> follow SQL's truth tables;
/// and <see cref="Matches"/> is true only when the whole expression evaluates to TRUE.
/// Property names and keywords are case-insensitive; string <em>values</em> are compared
/// ordinally, as Azure does.
/// </para>
/// </remarks>
public sealed class SqlFilterExpression
{
    private readonly Node _root;

    private SqlFilterExpression(string text, Node root)
    {
        Text = text;
        _root = root;
    }

    /// <summary>The expression this was parsed from.</summary>
    public string Text { get; }

    /// <summary>Parses an expression, throwing <see cref="SqlFilterParseException"/> if it is not valid.</summary>
    public static SqlFilterExpression Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var tokens = SqlFilterLexer.Tokenize(expression);
        var parser = new SqlFilterParser(tokens);
        var root = parser.ParseExpression();
        parser.ExpectEnd();
        return new SqlFilterExpression(expression, root);
    }

    /// <summary>Parses an expression, returning false and the reason instead of throwing.</summary>
    public static bool TryParse(string expression, out SqlFilterExpression? parsed, out string? error)
    {
        try
        {
            parsed = Parse(expression);
            error = null;
            return true;
        }
        catch (SqlFilterParseException ex)
        {
            parsed = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Evaluates the expression against a message. Only TRUE is a match: UNKNOWN (a comparison
    /// involving a missing property or a null) and FALSE both fail.
    /// </summary>
    public bool Matches(BrokeredMessage message) => Evaluate(message) == true;

    /// <summary>Evaluates to TRUE, FALSE or UNKNOWN (<see langword="null"/>).</summary>
    public bool? Evaluate(BrokeredMessage message) => Values.AsBool(_root.Eval(message));

    // ── Value helpers ────────────────────────────────────────────────────────

    internal static class Values
    {
        /// <summary>A non-boolean result is not a predicate, so it is UNKNOWN rather than a match.</summary>
        public static bool? AsBool(object? value) => value as bool?;

        public static bool TryDecimal(object? value, out decimal result)
        {
            switch (value)
            {
                case byte b: result = b; return true;
                case sbyte b: result = b; return true;
                case short s: result = s; return true;
                case ushort s: result = s; return true;
                case int i: result = i; return true;
                case uint i: result = i; return true;
                case long l: result = l; return true;
                case ulong l: result = l; return true;
                case decimal d: result = d; return true;
                default: result = 0; return false;
            }
        }

        public static bool TryDouble(object? value, out double result)
        {
            if (TryDecimal(value, out var dec))
            {
                result = (double)dec;
                return true;
            }

            switch (value)
            {
                case float f: result = f; return true;
                case double d: result = d; return true;
                default: result = 0; return false;
            }
        }

        public static bool IsNumber(object? value) => TryDouble(value, out _);

        /// <summary>
        /// Orders two values. Returns false when they are not comparable — a missing property, a
        /// null, or two different types — which the caller turns into UNKNOWN.
        /// </summary>
        public static bool TryCompare(object? left, object? right, out int comparison)
        {
            comparison = 0;
            if (left is null || right is null) return false;

            if (TryDecimal(left, out var ld) && TryDecimal(right, out var rd))
            {
                comparison = ld.CompareTo(rd);
                return true;
            }

            if (TryDouble(left, out var lf) && TryDouble(right, out var rf))
            {
                comparison = lf.CompareTo(rf);
                return true;
            }

            if (left is string ls && right is string rs)
            {
                comparison = string.CompareOrdinal(ls, rs);
                return true;
            }

            if (left is bool lb && right is bool rb)
            {
                comparison = lb.CompareTo(rb);
                return true;
            }

            if (left is DateTimeOffset lo && right is DateTimeOffset ro)
            {
                comparison = lo.CompareTo(ro);
                return true;
            }

            return false;
        }

        public static bool? Equal(object? left, object? right) =>
            TryCompare(left, right, out var c) ? c == 0 : null;
    }

    // ── AST ──────────────────────────────────────────────────────────────────

    internal abstract class Node
    {
        public abstract object? Eval(BrokeredMessage message);
    }

    internal sealed class LiteralNode(object? value) : Node
    {
        public object? Value { get; } = value;

        public override object? Eval(BrokeredMessage message) => Value;
    }

    internal enum Scope
    {
        User,
        System
    }

    internal sealed class PropertyNode(Scope scope, string name) : Node
    {
        public Scope Scope { get; } = scope;
        public string Name { get; } = name;

        public override object? Eval(BrokeredMessage message) => Resolve(message, Scope, Name);

        public bool Exists(BrokeredMessage message) => Resolve(message, Scope, Name) is not null;

        private static object? Resolve(BrokeredMessage message, Scope scope, string name)
        {
            if (scope == Scope.System)
                return ResolveSystem(message, name);

            return TryGetUserProperty(message, name, out var value) ? value : null;
        }

        private static object? ResolveSystem(BrokeredMessage message, string name) =>
            name.ToLowerInvariant() switch
            {
                // Azure exposes the AMQP `subject` as `sys.Label` for compatibility with the
                // pre-2021 SDK, and as `sys.Subject` for the current one. Both names, one value.
                "label" or "subject" => message.Subject,
                "correlationid" => message.CorrelationId,
                "messageid" => message.MessageId,
                "sessionid" => message.SessionId,
                "contenttype" => message.ContentType,
                "to" => message.To,
                "replyto" => message.ReplyTo,
                "replytosessionid" => message.ReplyToSessionId,
                "partitionkey" => message.PartitionKey,
                "enqueuedtimeutc" => message.EnqueuedTimeUtc,
                "scheduledenqueuetimeutc" => message.ScheduledEnqueueTimeUtc,
                "deadlettersource" => message.DeadLetterSource,
                "deliverycount" => message.DeliveryCount,
                "sequencenumber" => message.SequenceNumber,
                // Azure raises a FilterException for an unknown system property; we make it
                // UNKNOWN, so a rule naming one never matches rather than breaking every
                // delivery. See the note in the fork's README.
                _ => null
            };

        private static bool TryGetUserProperty(BrokeredMessage message, string name, out object? value)
        {
            if (message.ApplicationProperties.TryGetValue(name, out value))
                return true;

            foreach (var (key, candidate) in message.ApplicationProperties)
            {
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate;
                    return true;
                }
            }

            value = null;
            return false;
        }
    }

    internal sealed class ExistsNode(Node inner) : Node
    {
        public override object? Eval(BrokeredMessage message) =>
            inner is PropertyNode property
                ? property.Exists(message)
                : inner.Eval(message) is not null;
    }

    internal sealed class IsNullNode(Node inner, bool negate) : Node
    {
        public override object? Eval(BrokeredMessage message)
        {
            // IS NULL is two-valued even in a three-valued world: it asks about the value, not
            // about a comparison, so it never yields UNKNOWN.
            var isNull = inner.Eval(message) is null;
            return negate ? !isNull : isNull;
        }
    }

    internal sealed class NotNode(Node inner) : Node
    {
        public override object? Eval(BrokeredMessage message)
        {
            var value = Values.AsBool(inner.Eval(message));
            return value is null ? null : !value.Value;
        }
    }

    internal sealed class AndNode(Node left, Node right) : Node
    {
        public override object? Eval(BrokeredMessage message)
        {
            var l = Values.AsBool(left.Eval(message));
            if (l == false) return false;
            var r = Values.AsBool(right.Eval(message));
            if (r == false) return false;
            if (l is null || r is null) return null;
            return true;
        }
    }

    internal sealed class OrNode(Node left, Node right) : Node
    {
        public override object? Eval(BrokeredMessage message)
        {
            var l = Values.AsBool(left.Eval(message));
            if (l == true) return true;
            var r = Values.AsBool(right.Eval(message));
            if (r == true) return true;
            if (l is null || r is null) return null;
            return false;
        }
    }

    internal enum ComparisonOperator
    {
        Equal,
        NotEqual,
        LessThan,
        LessThanOrEqual,
        GreaterThan,
        GreaterThanOrEqual
    }

    internal sealed class ComparisonNode(ComparisonOperator op, Node left, Node right) : Node
    {
        public override object? Eval(BrokeredMessage message)
        {
            var l = left.Eval(message);
            var r = right.Eval(message);
            if (!Values.TryCompare(l, r, out var c))
                return null;

            return op switch
            {
                ComparisonOperator.Equal => c == 0,
                ComparisonOperator.NotEqual => c != 0,
                ComparisonOperator.LessThan => c < 0,
                ComparisonOperator.LessThanOrEqual => c <= 0,
                ComparisonOperator.GreaterThan => c > 0,
                ComparisonOperator.GreaterThanOrEqual => c >= 0,
                _ => null
            };
        }
    }

    internal sealed class InNode(Node left, IReadOnlyList<Node> items, bool negate) : Node
    {
        public override object? Eval(BrokeredMessage message)
        {
            var value = left.Eval(message);
            if (value is null) return null;

            var sawUnknown = false;
            foreach (var item in items)
            {
                var equal = Values.Equal(value, item.Eval(message));
                if (equal is null) { sawUnknown = true; continue; }
                if (equal.Value) return !negate;
            }

            // SQL: `x IN (a, b)` with no match but an incomparable member is UNKNOWN, not false.
            if (sawUnknown) return null;
            return negate;
        }
    }

    internal sealed class LikeNode(Node left, string pattern, char? escape, bool negate) : Node
    {
        private readonly Regex _regex = new(
            TranslatePattern(pattern, escape),
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        public override object? Eval(BrokeredMessage message)
        {
            if (left.Eval(message) is not string value) return null;
            var matched = _regex.IsMatch(value);
            return negate ? !matched : matched;
        }

        /// <summary>
        /// Translates a SQL LIKE pattern into a regex: <c>%</c> is any run of characters,
        /// <c>_</c> is exactly one, and an ESCAPE character makes the next character literal.
        /// </summary>
        internal static string TranslatePattern(string pattern, char? escape)
        {
            var builder = new StringBuilder("^");
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (escape is not null && c == escape.Value)
                {
                    if (i + 1 >= pattern.Length)
                        throw new SqlFilterParseException(
                            $"LIKE pattern '{pattern}' ends with the escape character '{escape}'.");
                    builder.Append(Regex.Escape(pattern[++i].ToString()));
                    continue;
                }

                switch (c)
                {
                    case '%': builder.Append(".*"); break;
                    case '_': builder.Append('.'); break;
                    default: builder.Append(Regex.Escape(c.ToString())); break;
                }
            }

            return builder.Append('$').ToString();
        }
    }

    internal enum ArithmeticOperator
    {
        Add,
        Subtract,
        Multiply,
        Divide,
        Modulo
    }

    internal sealed class ArithmeticNode(ArithmeticOperator op, Node left, Node right) : Node
    {
        public override object? Eval(BrokeredMessage message)
        {
            var l = left.Eval(message);
            var r = right.Eval(message);
            if (l is null || r is null) return null;

            if (Values.TryDecimal(l, out var ld) && Values.TryDecimal(r, out var rd))
                return Apply(ld, rd);

            if (Values.TryDouble(l, out var lf) && Values.TryDouble(r, out var rf))
                return Apply(lf, rf);

            return null;
        }

        private object? Apply(decimal l, decimal r) => op switch
        {
            ArithmeticOperator.Add => l + r,
            ArithmeticOperator.Subtract => l - r,
            ArithmeticOperator.Multiply => l * r,
            ArithmeticOperator.Divide => r == 0 ? null : l / r,
            ArithmeticOperator.Modulo => r == 0 ? null : l % r,
            _ => null
        };

        private object? Apply(double l, double r) => op switch
        {
            ArithmeticOperator.Add => l + r,
            ArithmeticOperator.Subtract => l - r,
            ArithmeticOperator.Multiply => l * r,
            ArithmeticOperator.Divide => r == 0 ? null : l / r,
            ArithmeticOperator.Modulo => r == 0 ? null : l % r,
            _ => null
        };
    }

    internal sealed class NegateNode(Node inner) : Node
    {
        public override object? Eval(BrokeredMessage message)
        {
            var value = inner.Eval(message);
            if (value is null) return null;
            if (Values.TryDecimal(value, out var d)) return -d;
            if (Values.TryDouble(value, out var f)) return -f;
            return null;
        }
    }
}

// ── Lexer ────────────────────────────────────────────────────────────────────

internal enum SqlTokenKind
{
    Identifier,
    String,
    Number,
    Operator,
    OpenParen,
    CloseParen,
    Comma,
    End
}

internal readonly record struct SqlToken(SqlTokenKind Kind, string Text, object? Value, int Position);

internal static class SqlFilterLexer
{
    public static IReadOnlyList<SqlToken> Tokenize(string expression)
    {
        var tokens = new List<SqlToken>();
        var i = 0;

        while (i < expression.Length)
        {
            var c = expression[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '\'')
            {
                var start = i++;
                var builder = new StringBuilder();
                var closed = false;
                while (i < expression.Length)
                {
                    if (expression[i] == '\'')
                    {
                        // '' inside a literal is one quote, as in SQL.
                        if (i + 1 < expression.Length && expression[i + 1] == '\'')
                        {
                            builder.Append('\'');
                            i += 2;
                            continue;
                        }

                        i++;
                        closed = true;
                        break;
                    }

                    builder.Append(expression[i++]);
                }

                if (!closed)
                    throw new SqlFilterParseException($"Unterminated string literal at position {start}.");

                tokens.Add(new SqlToken(SqlTokenKind.String, builder.ToString(), builder.ToString(), start));
                continue;
            }

            if (char.IsAsciiDigit(c) ||
                (c == '.' && i + 1 < expression.Length && char.IsAsciiDigit(expression[i + 1])))
            {
                var start = i;
                var isFloating = false;
                while (i < expression.Length && (char.IsAsciiDigit(expression[i]) || expression[i] == '.'))
                {
                    if (expression[i] == '.') isFloating = true;
                    i++;
                }

                if (i < expression.Length && (expression[i] == 'e' || expression[i] == 'E'))
                {
                    isFloating = true;
                    i++;
                    if (i < expression.Length && (expression[i] == '+' || expression[i] == '-')) i++;
                    if (i >= expression.Length || !char.IsAsciiDigit(expression[i]))
                        throw new SqlFilterParseException($"Malformed numeric literal at position {start}.");
                    while (i < expression.Length && char.IsAsciiDigit(expression[i])) i++;
                }

                var text = expression[start..i];
                object value;
                if (isFloating)
                {
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                        throw new SqlFilterParseException($"Malformed numeric literal '{text}' at position {start}.");
                    value = d;
                }
                else if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                {
                    value = l;
                }
                else if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var m))
                {
                    value = m;
                }
                else
                {
                    throw new SqlFilterParseException($"Malformed numeric literal '{text}' at position {start}.");
                }

                tokens.Add(new SqlToken(SqlTokenKind.Number, text, value, start));
                continue;
            }

            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                var start = i;
                while (i < expression.Length &&
                       (char.IsLetterOrDigit(expression[i]) || expression[i] is '_' or '$' or '.'))
                {
                    i++;
                }

                var text = expression[start..i];
                tokens.Add(new SqlToken(SqlTokenKind.Identifier, text, null, start));
                continue;
            }

            switch (c)
            {
                case '(':
                    tokens.Add(new SqlToken(SqlTokenKind.OpenParen, "(", null, i++));
                    continue;
                case ')':
                    tokens.Add(new SqlToken(SqlTokenKind.CloseParen, ")", null, i++));
                    continue;
                case ',':
                    tokens.Add(new SqlToken(SqlTokenKind.Comma, ",", null, i++));
                    continue;
            }

            var two = i + 1 < expression.Length ? expression.Substring(i, 2) : null;
            if (two is "<>" or "!=" or "<=" or ">=")
            {
                tokens.Add(new SqlToken(SqlTokenKind.Operator, two, null, i));
                i += 2;
                continue;
            }

            if (c is '=' or '<' or '>' or '+' or '-' or '*' or '/' or '%')
            {
                tokens.Add(new SqlToken(SqlTokenKind.Operator, c.ToString(), null, i++));
                continue;
            }

            throw new SqlFilterParseException($"Unexpected character '{c}' at position {i}.");
        }

        tokens.Add(new SqlToken(SqlTokenKind.End, string.Empty, null, expression.Length));
        return tokens;
    }
}

// ── Parser ───────────────────────────────────────────────────────────────────

internal sealed class SqlFilterParser(IReadOnlyList<SqlToken> tokens)
{
    private int _position;

    private SqlToken Current => tokens[_position];

    private SqlToken Advance() => tokens[_position++];

    private bool IsKeyword(string keyword) =>
        Current.Kind == SqlTokenKind.Identifier &&
        string.Equals(Current.Text, keyword, StringComparison.OrdinalIgnoreCase);

    private bool TakeKeyword(string keyword)
    {
        if (!IsKeyword(keyword)) return false;
        _position++;
        return true;
    }

    private void ExpectKeyword(string keyword)
    {
        if (!TakeKeyword(keyword))
            throw new SqlFilterParseException($"Expected '{keyword}' at position {Current.Position}.");
    }

    public void ExpectEnd()
    {
        if (Current.Kind != SqlTokenKind.End)
            throw new SqlFilterParseException(
                $"Unexpected '{Current.Text}' at position {Current.Position}.");
    }

    public SqlFilterExpression.Node ParseExpression() => ParseOr();

    private SqlFilterExpression.Node ParseOr()
    {
        var left = ParseAnd();
        while (TakeKeyword("OR"))
            left = new SqlFilterExpression.OrNode(left, ParseAnd());
        return left;
    }

    private SqlFilterExpression.Node ParseAnd()
    {
        var left = ParseNot();
        while (TakeKeyword("AND"))
            left = new SqlFilterExpression.AndNode(left, ParseNot());
        return left;
    }

    private SqlFilterExpression.Node ParseNot()
    {
        // NOT binds looser than a comparison: `NOT a = b` is `NOT (a = b)`.
        if (TakeKeyword("NOT"))
            return new SqlFilterExpression.NotNode(ParseNot());
        return ParseRelational();
    }

    private SqlFilterExpression.Node ParseRelational()
    {
        var left = ParseAdditive();

        while (true)
        {
            if (IsKeyword("IS"))
            {
                _position++;
                var negate = TakeKeyword("NOT");
                ExpectKeyword("NULL");
                left = new SqlFilterExpression.IsNullNode(left, negate);
                continue;
            }

            if (IsKeyword("NOT"))
            {
                // `x NOT IN (…)` / `x NOT LIKE '…'` — a trailing NOT with anything else after it
                // is a parse error rather than a silent match.
                var save = _position;
                _position++;
                if (TakeKeyword("IN")) { left = ParseInTail(left, negate: true); continue; }
                if (TakeKeyword("LIKE")) { left = ParseLikeTail(left, negate: true); continue; }
                _position = save;
                break;
            }

            if (TakeKeyword("IN")) { left = ParseInTail(left, negate: false); continue; }
            if (TakeKeyword("LIKE")) { left = ParseLikeTail(left, negate: false); continue; }

            if (Current.Kind == SqlTokenKind.Operator && Current.Text is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=")
            {
                var op = Advance().Text switch
                {
                    "=" => SqlFilterExpression.ComparisonOperator.Equal,
                    "<>" or "!=" => SqlFilterExpression.ComparisonOperator.NotEqual,
                    "<" => SqlFilterExpression.ComparisonOperator.LessThan,
                    "<=" => SqlFilterExpression.ComparisonOperator.LessThanOrEqual,
                    ">" => SqlFilterExpression.ComparisonOperator.GreaterThan,
                    _ => SqlFilterExpression.ComparisonOperator.GreaterThanOrEqual
                };
                left = new SqlFilterExpression.ComparisonNode(op, left, ParseAdditive());
                continue;
            }

            break;
        }

        return left;
    }

    private SqlFilterExpression.Node ParseInTail(SqlFilterExpression.Node left, bool negate)
    {
        Expect(SqlTokenKind.OpenParen, "(");
        var items = new List<SqlFilterExpression.Node>();
        if (Current.Kind == SqlTokenKind.CloseParen)
            throw new SqlFilterParseException($"IN list is empty at position {Current.Position}.");

        do
        {
            items.Add(ParseAdditive());
        }
        while (TakeComma());

        Expect(SqlTokenKind.CloseParen, ")");
        return new SqlFilterExpression.InNode(left, items, negate);
    }

    private SqlFilterExpression.Node ParseLikeTail(SqlFilterExpression.Node left, bool negate)
    {
        if (Current.Kind != SqlTokenKind.String)
            throw new SqlFilterParseException(
                $"LIKE expects a string pattern at position {Current.Position}.");
        var pattern = (string)Advance().Value!;

        char? escape = null;
        if (TakeKeyword("ESCAPE"))
        {
            if (Current.Kind != SqlTokenKind.String)
                throw new SqlFilterParseException(
                    $"ESCAPE expects a single-character string at position {Current.Position}.");
            var escapeText = (string)Advance().Value!;
            if (escapeText.Length != 1)
                throw new SqlFilterParseException(
                    $"ESCAPE expects exactly one character, got '{escapeText}'.");
            escape = escapeText[0];
        }

        return new SqlFilterExpression.LikeNode(left, pattern, escape, negate);
    }

    private SqlFilterExpression.Node ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Current.Kind == SqlTokenKind.Operator && Current.Text is "+" or "-")
        {
            var op = Advance().Text == "+"
                ? SqlFilterExpression.ArithmeticOperator.Add
                : SqlFilterExpression.ArithmeticOperator.Subtract;
            left = new SqlFilterExpression.ArithmeticNode(op, left, ParseMultiplicative());
        }

        return left;
    }

    private SqlFilterExpression.Node ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Current.Kind == SqlTokenKind.Operator && Current.Text is "*" or "/" or "%")
        {
            var op = Advance().Text switch
            {
                "*" => SqlFilterExpression.ArithmeticOperator.Multiply,
                "/" => SqlFilterExpression.ArithmeticOperator.Divide,
                _ => SqlFilterExpression.ArithmeticOperator.Modulo
            };
            left = new SqlFilterExpression.ArithmeticNode(op, left, ParseUnary());
        }

        return left;
    }

    private SqlFilterExpression.Node ParseUnary()
    {
        if (Current.Kind == SqlTokenKind.Operator && Current.Text is "+" or "-")
        {
            var negate = Advance().Text == "-";
            var operand = ParseUnary();
            return negate ? new SqlFilterExpression.NegateNode(operand) : operand;
        }

        return ParsePrimary();
    }

    private SqlFilterExpression.Node ParsePrimary()
    {
        var token = Current;

        switch (token.Kind)
        {
            case SqlTokenKind.Number:
            case SqlTokenKind.String:
                _position++;
                return new SqlFilterExpression.LiteralNode(token.Value);

            case SqlTokenKind.OpenParen:
                _position++;
                var inner = ParseOr();
                Expect(SqlTokenKind.CloseParen, ")");
                return inner;

            case SqlTokenKind.Identifier:
                if (string.Equals(token.Text, "TRUE", StringComparison.OrdinalIgnoreCase))
                {
                    _position++;
                    return new SqlFilterExpression.LiteralNode(true);
                }

                if (string.Equals(token.Text, "FALSE", StringComparison.OrdinalIgnoreCase))
                {
                    _position++;
                    return new SqlFilterExpression.LiteralNode(false);
                }

                if (string.Equals(token.Text, "NULL", StringComparison.OrdinalIgnoreCase))
                {
                    _position++;
                    return new SqlFilterExpression.LiteralNode(null);
                }

                if (string.Equals(token.Text, "EXISTS", StringComparison.OrdinalIgnoreCase))
                {
                    _position++;
                    Expect(SqlTokenKind.OpenParen, "(");
                    var operand = ParseExpression();
                    Expect(SqlTokenKind.CloseParen, ")");
                    return new SqlFilterExpression.ExistsNode(operand);
                }

                if (IsReservedWord(token.Text))
                    throw new SqlFilterParseException(
                        $"Unexpected keyword '{token.Text}' at position {token.Position}.");

                _position++;
                return MakeProperty(token);

            default:
                throw new SqlFilterParseException(
                    token.Kind == SqlTokenKind.End
                        ? "Unexpected end of expression."
                        : $"Unexpected '{token.Text}' at position {token.Position}.");
        }
    }

    private static bool IsReservedWord(string text) =>
        text.ToUpperInvariant() is "AND" or "OR" or "NOT" or "IN" or "LIKE" or "IS" or "ESCAPE";

    private static SqlFilterExpression.Node MakeProperty(SqlToken token)
    {
        var text = token.Text;

        if (text.StartsWith("sys.", StringComparison.OrdinalIgnoreCase))
            return new SqlFilterExpression.PropertyNode(SqlFilterExpression.Scope.System, text[4..]);

        if (text.StartsWith("user.", StringComparison.OrdinalIgnoreCase))
            return new SqlFilterExpression.PropertyNode(SqlFilterExpression.Scope.User, text[5..]);

        if (text.Contains('.'))
            throw new SqlFilterParseException(
                $"Unknown property scope in '{text}' at position {token.Position}; expected 'sys.' or 'user.'.");

        // Azure: an unscoped name is a user property.
        return new SqlFilterExpression.PropertyNode(SqlFilterExpression.Scope.User, text);
    }

    private bool TakeComma()
    {
        if (Current.Kind != SqlTokenKind.Comma) return false;
        _position++;
        return true;
    }

    private void Expect(SqlTokenKind kind, string text)
    {
        if (Current.Kind != kind)
            throw new SqlFilterParseException($"Expected '{text}' at position {Current.Position}.");
        _position++;
    }
}
