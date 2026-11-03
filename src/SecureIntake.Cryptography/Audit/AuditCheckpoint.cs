using System.Buffers.Text;
using System.Globalization;
using System.Text;
using SecureIntake.Cryptography.Signing;

namespace SecureIntake.Cryptography.Audit;

/// <summary>
/// A signed statement that the chain for <see cref="Scope"/> had <see cref="Hash"/> at <see cref="Seq"/> (S8 draft).
/// The signed payload is the JCS form of
/// <c>{"format":"secure-intake-audit-checkpoint@1","hash":…,"keyId":…,"recoveryEpoch":…,"scope":…,"seq":…,"time":…}</c>
/// with <c>hash</c> as unpadded base64url and <c>time</c> as Unix seconds.
/// </summary>
public sealed record AuditCheckpoint(string Scope, long Seq, byte[] Hash, long RecoveryEpoch, long Time, string KeyId)
{
    public const string Format = "secure-intake-audit-checkpoint@1";

    public byte[] SigningPayload()
    {
        // Built by hand in canonical (sorted) order; every value is a string or a safe integer.
        var sb = new StringBuilder();
        sb.Append("{\"format\":\"").Append(Format)
          .Append("\",\"hash\":\"").Append(Base64Url.EncodeToString(Hash))
          .Append("\",\"keyId\":\"").Append(KeyId)
          .Append("\",\"recoveryEpoch\":").Append(RecoveryEpoch.ToString(CultureInfo.InvariantCulture))
          .Append(",\"scope\":\"").Append(Scope)
          .Append("\",\"seq\":").Append(Seq.ToString(CultureInfo.InvariantCulture))
          .Append(",\"time\":").Append(Time.ToString(CultureInfo.InvariantCulture))
          .Append('}');
        return Canonical.Jcs.Canonicalize(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    public byte[] Sign(Es256Signer signer) => signer.Sign(SigningPayload());

    public bool Verify(ReadOnlySpan<byte> signature, Es256Verifier verifier) => verifier.Verify(SigningPayload(), signature);
}
