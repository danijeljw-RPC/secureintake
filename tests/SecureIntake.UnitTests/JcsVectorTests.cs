using System.Text;
using System.Text.Json;
using SecureIntake.Cryptography.Canonical;

namespace SecureIntake.UnitTests;

/// <summary>Shared SI-JCS-1 vectors; the TypeScript implementation in frontend/packages/crypto-protocol runs the same file.</summary>
public class JcsVectorTests
{
    private static readonly JsonElement V = JsonDocument.Parse(File.ReadAllBytes(Repo.Path("docs", "specs", "vectors", "si-jcs-1.json"))).RootElement;

    public static TheoryData<string> Valid() => [.. V.GetProperty("valid").EnumerateArray().Select(e => e.GetProperty("name").GetString()!)];

    public static TheoryData<string> Invalid() => [.. V.GetProperty("invalid").EnumerateArray().Select(e => e.GetProperty("name").GetString()!)];

    private static JsonElement Case(string section, string name) =>
        V.GetProperty(section).EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);

    private static byte[] Input(JsonElement c) => c.TryGetProperty("inputHex", out var hex)
        ? Convert.FromHexString(hex.GetString()!)
        : Encoding.UTF8.GetBytes(c.GetProperty("input").GetString()!);

    [Theory]
    [MemberData(nameof(Valid))]
    public void Valid_input_canonicalises_to_exact_bytes(string name)
    {
        var c = Case("valid", name);
        Assert.Equal(c.GetProperty("canonicalHex").GetString(), Convert.ToHexStringLower(Jcs.Canonicalize(Input(c))));
        // Idempotent: canonical output is its own canonical form.
        Assert.Equal(c.GetProperty("canonicalHex").GetString(), Convert.ToHexStringLower(Jcs.Canonicalize(Jcs.Canonicalize(Input(c)))));
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_input_is_rejected(string name) => Assert.Throws<FormatException>(() => Jcs.Canonicalize(Input(Case("invalid", name))));
}
