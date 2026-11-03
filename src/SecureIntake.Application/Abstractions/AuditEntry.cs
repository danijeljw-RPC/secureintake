namespace SecureIntake.Application.Abstractions;

/// <summary>
/// One audit event to append in the same transaction as the change it records (F11).
/// <see cref="Data"/> values must be strings without lone surrogates, <see cref="int"/>/<see cref="long"/> within
/// ±(2^53 − 1), booleans, <see cref="Guid"/>s or null (SI-JCS-1, ADR-0003). They must never hold secrets,
/// tokens, OTPs, private keys, DEKs or E2EE plaintext.
/// </summary>
public sealed record AuditEntry(string Event, string Actor, string? Target, IReadOnlyDictionary<string, object?> Data)
{
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    public static AuditEntry Of(string evt, string actor, string? target, params (string Key, object? Value)[] data)
    {
        var entry = new AuditEntry(evt, actor, target, data.ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal));
        entry.EnsureValid();
        return entry;
    }

    /// <summary>Rejects unsupported values before anything is persisted. Writers call this before any SQL.</summary>
    public void EnsureValid()
    {
        Check("event", Event);
        Check("actor", Actor);
        if (Target is not null)
        {
            Check("target", Target);
        }
        foreach (var (key, value) in Data)
        {
            Check("data key", key);
            switch (value)
            {
                case null or bool or Guid:
                    break;
                case string s:
                    Check(key, s);
                    break;
                case int:
                    break;
                case long l when l is <= MaxSafeInteger and >= -MaxSafeInteger:
                    break;
                default:
                    throw new ArgumentException($"Audit data '{key}' has an unsupported type or value ({value.GetType().Name}).");
            }
        }
    }

    private static void Check(string what, string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(s[i]))
            {
                throw new ArgumentException($"Audit {what} contains a lone surrogate.");
            }
        }
    }
}

public sealed record OutboxEvent(string Type, int Version, Guid? OrganisationId, string PayloadJson);
