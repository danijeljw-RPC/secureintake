using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SecureIntake.Cryptography.Native;

/// <summary>
/// Minimal libsodium bindings (ADR-0002): source-generated <c>LibraryImport</c>, AOT-safe, official
/// <c>libsodium</c> NuGet binaries. Bind only what a spec needs.
/// </summary>
internal static partial class Sodium
{
    private const string Lib = "libsodium";
    private static readonly Lazy<string?> InitError = new(Initialise);

    public static void EnsureInitialised()
    {
        if (InitError.Value is { } error)
        {
            throw new CryptographicException(error);
        }
    }

    /// <summary>Null when libsodium loaded and initialised; otherwise a safe description of why not.</summary>
    public static string? AvailabilityError => InitError.Value;

    private static string? Initialise()
    {
        try
        {
            return sodium_init() >= 0 ? null : "libsodium failed to initialise.";
        }
        catch (DllNotFoundException)
        {
            return "libsodium native library not found for this platform.";
        }
        catch (EntryPointNotFoundException)
        {
            return "libsodium native library is incompatible.";
        }
    }

    [LibraryImport(Lib)]
    private static partial int sodium_init();

    [LibraryImport(Lib)]
    internal static partial int crypto_scalarmult_curve25519(Span<byte> q, ReadOnlySpan<byte> n, ReadOnlySpan<byte> p);

    [LibraryImport(Lib)]
    internal static partial int crypto_scalarmult_curve25519_base(Span<byte> q, ReadOnlySpan<byte> n);
}
