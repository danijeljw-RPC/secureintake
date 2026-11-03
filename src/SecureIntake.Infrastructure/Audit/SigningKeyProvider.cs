using SecureIntake.Cryptography.Signing;
using SecureIntake.Infrastructure.Secrets;

namespace SecureIntake.Infrastructure.Audit;

/// <summary>
/// Loads the deployment's audit checkpoint key (key domain separate from receipts and evidence, doc 26 §7).
/// The secret is a base64 PKCS#8 P-256 private key. Under Development only, a missing key is generated
/// into the data directory with mode 0600. KMS/HSM providers arrive in v1.2.
/// </summary>
public static class SigningKeyProvider
{
    public static Es256Signer LoadAuditCheckpointKey(SecretResolver secrets, string? reference, string dataDirectory, bool allowGenerate)
    {
        if (!string.IsNullOrEmpty(reference))
        {
            var raw = secrets.Resolve("Signing:AuditCheckpointKey", reference);
            return Create(Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(raw).Trim()));
        }
        if (!allowGenerate)
        {
            throw new SecretResolutionException("Signing:AuditCheckpointKey", "is required outside the Development profile");
        }
        var path = Path.Combine(dataDirectory, "keys", "audit-checkpoint.p8.b64");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var generated = Es256Signer.Generate("pending");
            File.WriteAllText(path, Convert.ToBase64String(generated.ExportPkcs8()));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        SecretResolver.EnsureProtected("Signing:AuditCheckpointKey", path);
        return Create(Convert.FromBase64String(File.ReadAllText(path).Trim()));
    }

    private static Es256Signer Create(byte[] pkcs8)
    {
        using var probe = Es256Signer.FromPkcs8(pkcs8, "probe");
        var fp = Es256Verifier.Fingerprint(probe.ExportSubjectPublicKeyInfo());
        return Es256Signer.FromPkcs8(pkcs8, "audck_" + Convert.ToHexStringLower(fp)[..16]);
    }
}
