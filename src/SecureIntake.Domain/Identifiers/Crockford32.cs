namespace SecureIntake.Domain.Identifiers;

/// <summary>
/// Crockford base32 (0-9, A-Z without I, L, O, U). Decoding is case-insensitive and maps
/// I/L to 1 and O to 0, as the Crockford spec allows. Hyphens are not accepted here.
/// </summary>
public static class Crockford32
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static readonly sbyte[] DecodeMap = BuildDecodeMap();

    private static sbyte[] BuildDecodeMap()
    {
        var map = new sbyte[128];
        Array.Fill(map, (sbyte)-1);
        for (var i = 0; i < Alphabet.Length; i++)
        {
            map[Alphabet[i]] = (sbyte)i;
            map[char.ToLowerInvariant(Alphabet[i])] = (sbyte)i;
        }
        map['I'] = map['i'] = map['L'] = map['l'] = 1;
        map['O'] = map['o'] = 0;
        return map;
    }

    /// <summary>Returns the 5-bit value of <paramref name="c"/>, or -1 if it isn't a Crockford symbol.</summary>
    public static int DecodeSymbol(char c) => c < 128 ? DecodeMap[c] : -1;

    /// <summary>Encodes a 128-bit value as exactly 26 characters (the top 2 bits of the first symbol are zero).</summary>
    public static string Encode128(UInt128 value)
    {
        Span<char> buffer = stackalloc char[26];
        for (var i = 25; i >= 0; i--)
        {
            buffer[i] = Alphabet[(int)(value & 0x1F)];
            value >>= 5;
        }
        return new string(buffer);
    }

    public static bool TryDecode128(ReadOnlySpan<char> text, out UInt128 value)
    {
        value = 0;
        if (text.Length != 26)
        {
            return false;
        }
        // 26 symbols carry 130 bits, so the first symbol may only use its low 3 bits.
        var first = DecodeSymbol(text[0]);
        if (first is < 0 or > 7)
        {
            return false;
        }
        value = (UInt128)first;
        for (var i = 1; i < 26; i++)
        {
            var v = DecodeSymbol(text[i]);
            if (v < 0)
            {
                value = 0;
                return false;
            }
            value = (value << 5) | (UInt128)v;
        }
        return true;
    }
}
