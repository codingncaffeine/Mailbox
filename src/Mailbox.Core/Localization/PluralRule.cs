using System.Globalization;

namespace Mailbox.Core.Localization;

/// <summary>
/// A language's rule for which plural form a number takes.
/// </summary>
/// <remarks>
/// <b>Why this is not "one or many".</b> English has two forms and puts every number but one in
/// the second, which makes it the worst possible model to build on: Japanese has one form,
/// Polish has three and picks between them on the last two digits, Arabic has six. A client that
/// asks translators for a singular and a plural has already made their language wrong, and the
/// usual result is that they write the plural twice and the interface reads badly forever.
/// <para>
/// So the rule comes from the translation rather than from here. Every <c>.po</c> file carries a
/// <c>Plural-Forms</c> header — <c>nplurals=3; plural=(n==1 ? 0 : n%10&gt;=2 &amp;&amp; n%10&lt;=4
/// ? 1 : 2);</c> — which is a small C expression over one variable, and this reads and evaluates
/// it. That is the whole of what gettext does here, it is what every translator's tool already
/// writes, and it means a language nobody anticipated works without a change to this code.
/// </para>
/// <para>
/// The expression is arithmetic over one non-negative number and cannot reach anything: no names
/// but <c>n</c>, no calls, no assignment. Anything it cannot parse falls back to English's rule
/// rather than throwing, because a malformed header in somebody's translation should cost the
/// plural agreement of that language and nothing else.
/// </para>
/// </remarks>
public sealed class PluralRule
{
    /// <summary>English's own: two forms, and one of them is only for exactly one.</summary>
    public static PluralRule English { get; } = new(2, new Binary("!=", new Variable(), new Number(1)));

    /// <summary>How many forms this language has, which is how many strings a translator writes.</summary>
    public int Forms { get; }

    private readonly Node _expression;

    private PluralRule(int forms, Node expression)
    {
        Forms = forms;
        _expression = expression;
    }

    /// <summary>
    /// Reads a <c>Plural-Forms</c> header, or English's rule when it says nothing usable.
    /// </summary>
    public static PluralRule Read(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return English;

        var forms = 0;
        string? expression = null;

        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0) continue;

            var name = part[..equals].Trim();
            var value = part[(equals + 1)..].Trim();

            if (string.Equals(name, "nplurals", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(value, CultureInfo.InvariantCulture, out forms);
            }
            else if (string.Equals(name, "plural", StringComparison.OrdinalIgnoreCase))
            {
                expression = value;
            }
        }

        if (forms < 1 || expression is null) return English;

        var parsed = Parse(expression);
        return parsed is null ? English : new PluralRule(forms, parsed);
    }

    /// <summary>
    /// Which form a count takes: an index from zero, always inside the language's own range.
    /// </summary>
    /// <remarks>
    /// Clamped rather than trusted. A header claiming three forms with an expression that can
    /// answer four would otherwise index past the translations a translator actually wrote.
    /// The number is taken as its magnitude: "−1 messages" is not a thing anybody writes, and a
    /// negative count reaching a modulo would pick a form at random.
    /// </remarks>
    public int Form(long count)
    {
        var answer = _expression.Value(Math.Abs(count));

        // C's own coercion, which is what these expressions are written in: a comparison yields
        // 0 or 1, and a language with two forms uses them as the indices directly.
        return answer < 0 ? 0 : answer >= Forms ? Forms - 1 : (int)answer;
    }

    // ---- The expression ------------------------------------------------------------------------

    private abstract class Node
    {
        public abstract long Value(long n);
    }

    private sealed class Number(long value) : Node
    {
        public override long Value(long n) => value;
    }

    private sealed class Variable : Node
    {
        public override long Value(long n) => n;
    }

    private sealed class Unary(string op, Node inner) : Node
    {
        public override long Value(long n) => op == "!" ? (inner.Value(n) == 0 ? 1 : 0) : inner.Value(n);
    }

    private sealed class Binary(string op, Node left, Node right) : Node
    {
        public override long Value(long n)
        {
            // Short-circuit, as C does: the right side of an && whose left is false is never
            // evaluated, which matters because a division by zero could live there.
            if (op == "&&") return left.Value(n) != 0 && right.Value(n) != 0 ? 1 : 0;
            if (op == "||") return left.Value(n) != 0 || right.Value(n) != 0 ? 1 : 0;

            var a = left.Value(n);
            var b = right.Value(n);

            return op switch
            {
                "%" => b == 0 ? 0 : a % b,
                "==" => a == b ? 1 : 0,
                "!=" => a != b ? 1 : 0,
                "<" => a < b ? 1 : 0,
                ">" => a > b ? 1 : 0,
                "<=" => a <= b ? 1 : 0,
                ">=" => a >= b ? 1 : 0,
                _ => 0,
            };
        }
    }

    private sealed class Conditional(Node test, Node whenTrue, Node whenFalse) : Node
    {
        public override long Value(long n) => test.Value(n) != 0 ? whenTrue.Value(n) : whenFalse.Value(n);
    }

    /// <summary>
    /// Recursive descent over the operators gettext's headers actually use, weakest first.
    /// </summary>
    /// <remarks>
    /// Null for anything it does not understand, rather than an exception: the caller's answer to
    /// a header it cannot read is English's rule, and that is a better outcome than a translation
    /// file taking the application down.
    /// </remarks>
    private static Node? Parse(string text)
    {
        var at = 0;
        var parsed = Ternary(text, ref at);
        Skip(text, ref at);

        // Trailing anything means this was not the expression it looked like.
        return parsed is not null && at >= text.Length ? parsed : null;
    }

    private static Node? Ternary(string text, ref int at)
    {
        var test = Or(text, ref at);
        if (test is null) return null;

        Skip(text, ref at);
        if (at >= text.Length || text[at] != '?') return test;

        at++;
        var whenTrue = Ternary(text, ref at);
        if (whenTrue is null) return null;

        Skip(text, ref at);
        if (at >= text.Length || text[at] != ':') return null;

        at++;
        var whenFalse = Ternary(text, ref at);
        return whenFalse is null ? null : new Conditional(test, whenTrue, whenFalse);
    }

    private static Node? Or(string text, ref int at) => Chain(text, ref at, ["||"], And);

    private static Node? And(string text, ref int at) => Chain(text, ref at, ["&&"], Equality);

    private static Node? Equality(string text, ref int at) => Chain(text, ref at, ["==", "!="], Relational);

    private static Node? Relational(string text, ref int at)
        // The two-character operators are tried first: reading ">" out of ">=" would leave an "="
        // behind and fail the whole expression.
        => Chain(text, ref at, ["<=", ">=", "<", ">"], Modulo);

    private static Node? Modulo(string text, ref int at) => Chain(text, ref at, ["%"], Primary);

    private delegate Node? Level(string text, ref int at);

    private static Node? Chain(string text, ref int at, string[] operators, Level next)
    {
        var left = next(text, ref at);
        if (left is null) return null;

        while (true)
        {
            Skip(text, ref at);

            // Copied out of the ref parameter, which a lambda may not close over.
            var here = at;
            var found = operators.FirstOrDefault(op => Ahead(text, here, op));
            if (found is null) return left;

            at += found.Length;
            var right = next(text, ref at);
            if (right is null) return null;
            left = new Binary(found, left, right);
        }
    }

    private static Node? Primary(string text, ref int at)
    {
        Skip(text, ref at);
        if (at >= text.Length) return null;

        if (text[at] == '!')
        {
            at++;
            var inner = Primary(text, ref at);
            return inner is null ? null : new Unary("!", inner);
        }

        if (text[at] == '(')
        {
            at++;
            var inner = Ternary(text, ref at);
            Skip(text, ref at);
            if (inner is null || at >= text.Length || text[at] != ')') return null;
            at++;
            return inner;
        }

        if (text[at] == 'n')
        {
            at++;
            return new Variable();
        }

        if (char.IsAsciiDigit(text[at]))
        {
            var start = at;
            while (at < text.Length && char.IsAsciiDigit(text[at])) at++;
            return long.TryParse(text[start..at], CultureInfo.InvariantCulture, out var value)
                ? new Number(value)
                : null;
        }

        return null;
    }

    private static bool Ahead(string text, int at, string op)
        => at + op.Length <= text.Length && text.AsSpan(at, op.Length).SequenceEqual(op);

    private static void Skip(string text, ref int at)
    {
        while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
    }
}
