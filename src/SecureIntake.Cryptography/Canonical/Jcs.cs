using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SecureIntake.Cryptography.Canonical;

/// <summary>
/// <b>SI-JCS-1</b>: the Secure Intake profile of the JSON Canonicalization Scheme (RFC 8785).
/// Normative text: <c>docs/specs/SI-JCS-1-canonical-json.md</c> (ADR-0003). It is a strict subset of RFC 8785:
/// every accepted input canonicalises to the same bytes RFC 8785 would produce, but inputs outside the
/// profile are rejected rather than normalised.
/// <list type="bullet">
/// <item>Numbers: the original token must match <c>-?(0|[1-9][0-9]*)</c>, must not be <c>-0</c>, and must lie in
/// ±(2^53 − 1). Fractions and exponent notation are rejected, even when the value is an exact integer.</item>
/// <item>Duplicate property names, lone surrogates, a byte-order mark, invalid UTF-8 and nesting deeper than
/// 64 are rejected.</item>
/// <item>Properties sort by UTF-16 code units; strings escape only <c>"</c>, <c>\</c> and U+0000…U+001F.</item>
/// </list>
/// Numbers are never converted through floating point: the canonical form is the validated token itself.
/// </summary>
public static class Jcs
{
    public const int MaxDepth = 64;
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    public static byte[] Canonicalize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length >= 3 && utf8Json[0] == 0xEF && utf8Json[1] == 0xBB && utf8Json[2] == 0xBF)
        {
            throw new FormatException("A byte-order mark is not allowed.");
        }
        JsonDocument doc;
        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = MaxDepth,
            });
            doc = JsonDocument.ParseValue(ref reader);
            if (reader.BytesConsumed != utf8Json.Length && !OnlyWhitespace(utf8Json[(int)reader.BytesConsumed..]))
            {
                doc.Dispose();
                throw new FormatException("Trailing content after the JSON value.");
            }
        }
        catch (JsonException ex)
        {
            throw new FormatException("Input is not valid JSON or not valid UTF-8.", ex);
        }
        using (doc)
        {
            var sb = new StringBuilder();
            try
            {
                Write(doc.RootElement, sb);
            }
            catch (InvalidOperationException ex)
            {
                // System.Text.Json throws this when an escaped lone surrogate can't be transcoded.
                throw new FormatException("String contains an invalid UTF-16 sequence.", ex);
            }
            return Encoding.UTF8.GetBytes(sb.ToString());
        }
    }

    private static bool OnlyWhitespace(ReadOnlySpan<byte> rest)
    {
        foreach (var b in rest)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
            {
                return false;
            }
        }
        return true;
    }

    private static void Write(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var props = new List<(string Name, JsonElement Value)>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject())
                {
                    if (!seen.Add(p.Name))
                    {
                        throw new FormatException($"Duplicate property '{p.Name}'.");
                    }
                    props.Add((p.Name, p.Value));
                }
                props.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                sb.Append('{');
                for (var i = 0; i < props.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }
                    WriteString(props[i].Name, sb);
                    sb.Append(':');
                    Write(props[i].Value, sb);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var first = true;
                foreach (var item in e.EnumerateArray())
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    Write(item, sb);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(e.GetString()!, sb);
                break;
            case JsonValueKind.Number:
                sb.Append(ValidateNumberToken(e.GetRawText()));
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
                sb.Append("null");
                break;
            default:
                throw new FormatException($"Unsupported JSON value kind {e.ValueKind}.");
        }
    }

    /// <summary>Validates a number token against the profile and returns it unchanged (it is already canonical).</summary>
    public static string ValidateNumberToken(string token)
    {
        var digits = token.StartsWith('-') ? token.AsSpan(1) : token.AsSpan();
        if (digits.Length == 0 || digits.Length > 16 || (digits.Length > 1 && digits[0] == '0'))
        {
            throw new FormatException($"Number '{token}' is outside the SI-JCS-1 profile.");
        }
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                throw new FormatException($"Number '{token}' is not a plain integer (no fraction or exponent allowed).");
            }
        }
        if (token == "-0")
        {
            throw new FormatException("Negative zero is not allowed.");
        }
        var value = long.Parse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (value is > MaxSafeInteger or < -MaxSafeInteger)
        {
            throw new FormatException($"Number '{token}' is outside ±(2^53 − 1).");
        }
        return token;
    }

    /// <summary>True when <paramref name="value"/> may appear in SI-JCS-1 JSON as a number.</summary>
    public static bool IsSafeInteger(long value) => value is <= MaxSafeInteger and >= -MaxSafeInteger;

    /// <summary>True when <paramref name="s"/> has no lone surrogates and so encodes as valid UTF-8.</summary>
    public static bool IsWellFormed(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(s[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        if (!IsWellFormed(s))
        {
            throw new FormatException("Lone surrogate in string.");
        }
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
