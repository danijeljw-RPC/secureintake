using System.Diagnostics.CodeAnalysis;

namespace SecureIntake.Domain.Identifiers;

/// <summary>
/// An internal UUIDv7 together with its kind. Renders as <c>{prefix}_{26 Crockford base32}</c>.
/// Parsing an ID of the wrong kind fails; callers turn that into a 404, never a coercion (F5).
/// </summary>
public readonly record struct ExternalId(IdKind Kind, Guid Value)
{
    public static ExternalId New(IdKind kind, TimeProvider? time = null) =>
        new(kind, time is null ? Guid.CreateVersion7() : Guid.CreateVersion7(time.GetUtcNow()));

    public override string ToString() => $"{Kind.Prefix()}_{Crockford32.Encode128(ToUInt128(Value))}";

    public static bool TryParse(string? text, IdKind expected, [NotNullWhen(true)] out ExternalId? id)
    {
        id = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        var prefix = expected.Prefix();
        if (text.Length != prefix.Length + 1 + 26
            || !text.StartsWith(prefix, StringComparison.Ordinal)
            || text[prefix.Length] != '_')
        {
            return false;
        }
        if (!Crockford32.TryDecode128(text.AsSpan(prefix.Length + 1), out var raw))
        {
            return false;
        }
        var guid = FromUInt128(raw);
        if (guid.Version != 7)
        {
            return false;
        }
        id = new ExternalId(expected, guid);
        return true;
    }

    public static ExternalId Parse(string text, IdKind expected) =>
        TryParse(text, expected, out var id) ? id.Value : throw new FormatException($"Not a valid {expected.Prefix()} identifier.");

    // Guid's big-endian byte order matches the RFC 9562 layout, so the encoded form sorts by time.
    private static UInt128 ToUInt128(Guid g)
    {
        Span<byte> b = stackalloc byte[16];
        g.TryWriteBytes(b, bigEndian: true, out _);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(b);
    }

    private static Guid FromUInt128(UInt128 v)
    {
        Span<byte> b = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt128BigEndian(b, v);
        return new Guid(b, bigEndian: true);
    }
}
