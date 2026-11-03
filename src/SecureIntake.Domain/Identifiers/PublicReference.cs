using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace SecureIntake.Domain.Identifiers;

/// <summary>
/// Public submission reference (F9): <c>{PREFIX}-XXXXX-XXXXX</c>. Nine random Crockford symbols
/// (45 bits) plus one check symbol. A reference is a label, never a capability.
/// </summary>
/// <remarks>
/// Check symbol: <c>sum(w_i * v_i) mod 32</c> with odd weights 1, 3, 5, …, 17. Odd weights are coprime
/// with 32, so every single-symbol substitution is detected. Most adjacent transpositions are detected;
/// the exception is a pair whose values differ by 16. To be pinned in spec S10.
/// </remarks>
public readonly record struct PublicReference
{
    public const string DefaultPrefix = "SI";
    private const int RandomSymbols = 9;

    public string Prefix { get; }
    public string Body { get; }

    private PublicReference(string prefix, string body)
    {
        Prefix = prefix;
        Body = body;
    }

    public static PublicReference Generate(string prefix = DefaultPrefix)
    {
        ValidatePrefix(prefix);
        Span<char> body = stackalloc char[RandomSymbols + 1];
        for (var i = 0; i < RandomSymbols; i++)
        {
            body[i] = Crockford32.Alphabet[RandomNumberGenerator.GetInt32(32)];
        }
        body[RandomSymbols] = Crockford32.Alphabet[CheckValue(body[..RandomSymbols])];
        return new PublicReference(prefix, new string(body));
    }

    public override string ToString() => $"{Prefix}-{Body[..5]}-{Body[5..]}";

    public static bool TryParse(string? text, string expectedPrefix, [NotNullWhen(true)] out PublicReference? reference)
    {
        reference = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var parts = text.Trim().Split('-');
        if (parts.Length != 3 || !string.Equals(parts[0], expectedPrefix, StringComparison.OrdinalIgnoreCase)
            || parts[1].Length != 5 || parts[2].Length != 5)
        {
            return false;
        }
        Span<char> normalised = stackalloc char[RandomSymbols + 1];
        var joined = parts[1] + parts[2];
        for (var i = 0; i < joined.Length; i++)
        {
            var v = Crockford32.DecodeSymbol(joined[i]);
            if (v < 0)
            {
                return false;
            }
            normalised[i] = Crockford32.Alphabet[v];
        }
        if (Crockford32.Alphabet[CheckValue(normalised[..RandomSymbols])] != normalised[RandomSymbols])
        {
            return false;
        }
        reference = new PublicReference(expectedPrefix.ToUpperInvariant(), new string(normalised));
        return true;
    }

    private static int CheckValue(ReadOnlySpan<char> symbols)
    {
        var sum = 0;
        for (var i = 0; i < symbols.Length; i++)
        {
            sum += ((2 * i) + 1) * Crockford32.DecodeSymbol(symbols[i]);
        }
        return sum % 32;
    }

    private static void ValidatePrefix(string prefix)
    {
        if (prefix.Length is < 1 or > 6 || !prefix.All(c => c is >= 'A' and <= 'Z'))
        {
            throw new ArgumentException("Reference prefix must be 1 to 6 uppercase ASCII letters.", nameof(prefix));
        }
    }
}
