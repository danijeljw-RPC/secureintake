using System.Runtime.InteropServices;

namespace SecureIntake.Infrastructure.Secrets;

/// <summary>
/// Resolves secret references (doc 19): <c>env:NAME</c> or <c>file:/absolute/path</c>. Providers are
/// registered at compile time; there is no plugin loading. Vault/OpenBao and cloud stores come in v1.1/v1.2.
/// </summary>
public sealed class SecretResolver(bool allowLiteral)
{
    public static bool IsReference(string? value) =>
        value is not null && (value.StartsWith("env:", StringComparison.Ordinal) || value.StartsWith("file:", StringComparison.Ordinal));

    /// <summary>Returns the secret bytes. Literal values are only allowed under the Development profile.</summary>
    public byte[] Resolve(string setting, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new SecretResolutionException(setting, "is not set");
        }
        if (value.StartsWith("env:", StringComparison.Ordinal))
        {
            var v = Environment.GetEnvironmentVariable(value[4..]);
            return string.IsNullOrEmpty(v) ? throw new SecretResolutionException(setting, $"environment variable {value[4..]} is empty") : System.Text.Encoding.UTF8.GetBytes(v);
        }
        if (value.StartsWith("file:", StringComparison.Ordinal))
        {
            var path = value[5..];
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            {
                throw new SecretResolutionException(setting, "file reference must be an existing absolute path");
            }
            EnsureProtected(setting, path);
            var bytes = File.ReadAllBytes(path);
            // Strip one trailing newline, which editors and `echo` add.
            var len = bytes.Length;
            if (len > 0 && bytes[len - 1] == '\n') len--;
            if (len > 0 && bytes[len - 1] == '\r') len--;
            return bytes[..len];
        }
        return allowLiteral
            ? System.Text.Encoding.UTF8.GetBytes(value)
            : throw new SecretResolutionException(setting, "must be an env: or file: reference outside the Development profile");
    }

    /// <summary>On Unix, refuses secret files readable or writable by group or others.</summary>
    public static void EnsureProtected(string setting, string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode loose = UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & loose) != 0)
        {
            throw new SecretResolutionException(setting, $"file {path} must not be writable by group or readable by others (chmod 600 or 640)");
        }
    }
}

public sealed class SecretResolutionException : Exception
{
    public SecretResolutionException(string setting, string problem) : base($"Secret '{setting}' {problem}.") { }
    public SecretResolutionException() { }
    public SecretResolutionException(string message) : base(message) { }
    public SecretResolutionException(string message, Exception inner) : base(message, inner) { }
}
