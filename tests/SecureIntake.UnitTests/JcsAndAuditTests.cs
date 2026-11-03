using System.Text;
using SecureIntake.Cryptography.Audit;
using SecureIntake.Cryptography.Canonical;
using SecureIntake.Cryptography.Signing;

namespace SecureIntake.UnitTests;

public class JcsTests
{
    private static string C(string json) => Encoding.UTF8.GetString(Jcs.Canonicalize(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Sorts_keys_by_utf16_code_units_and_strips_whitespace() =>
        // RFC 8785 §3.2.3: "\u20ac" (€) sorts after "\r" and before "\ud83d\ude00" (😀) by UTF-16 units.
        Assert.Equal("{\"\\r\":1,\"1\":2,\"\u20ac\":3,\"\ud83d\ude00\":4,\"\ufb33\":5}",
            C("{ \"€\": 3, \"\\r\": 1, \"\ufb33\": 5, \"1\": 2, \"\ud83d\ude00\": 4 }"));

    [Fact]
    public void Escapes_only_what_rfc8785_requires() =>
        Assert.Equal("[\"\\u000f\\n\\\"\\\\é/<>\"]", C("[\"\\u000F\\n\\\"\\\\\\u00e9\\/<>\"]"));

    [Fact]
    public void Literals_and_nesting() =>
        Assert.Equal("{\"a\":[true,false,null,{\"b\":-5}],\"c\":{}}", C("{\"c\":{},\"a\":[true,false,null,{\"b\":-5}]}"));

    [Theory]
    [InlineData("{\"a\":1.5}")]
    [InlineData("{\"a\":1e3}")]
    [InlineData("{\"a\":1.0}")]
    [InlineData("{\"a\":-0}")]
    [InlineData("{\"a\":9007199254740992}")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("\"\\ud800\"")]
    public void Rejects_values_outside_the_profile(string json) => Assert.Throws<FormatException>(() => C(json));
}

public class AuditChainTests
{
    private static List<(byte[], byte[], byte[])> BuildChain(int n)
    {
        var prev = AuditChain.Genesis;
        var list = new List<(byte[], byte[], byte[])>();
        for (var i = 1; i <= n; i++)
        {
            var json = Encoding.UTF8.GetBytes($"{{\"seq\":{i},\"event\":\"e{i}\",\"scope\":\"platform\"}}");
            var hash = AuditChain.Link(prev, json);
            list.Add((json, prev, hash));
            prev = hash;
        }
        return list;
    }

    [Fact]
    public void Valid_chain_verifies() => Assert.Equal(-1, AuditChain.Verify(AuditChain.Genesis, BuildChain(5)));

    [Fact]
    public void Key_order_does_not_change_the_hash() =>
        Assert.Equal(AuditChain.Link(AuditChain.Genesis, "{\"a\":1,\"b\":2}"u8), AuditChain.Link(AuditChain.Genesis, "{\"b\":2, \"a\":1}"u8));

    [Fact]
    public void Edited_record_is_detected_at_its_position()
    {
        var chain = BuildChain(5);
        chain[2] = (Encoding.UTF8.GetBytes("{\"seq\":3,\"event\":\"forged\",\"scope\":\"platform\"}"), chain[2].Item2, chain[2].Item3);
        Assert.Equal(2, AuditChain.Verify(AuditChain.Genesis, chain));
    }

    [Fact]
    public void Deleted_record_is_detected()
    {
        var chain = BuildChain(5);
        chain.RemoveAt(1);
        Assert.Equal(1, AuditChain.Verify(AuditChain.Genesis, chain));
    }

    [Fact]
    public void Checkpoint_signature_verifies_and_binds_every_field()
    {
        using var signer = Es256Signer.Generate("audck_test");
        using var verifier = signer.Verifier();
        var hash = AuditChain.Link(AuditChain.Genesis, "{}"u8);
        var cp = new AuditCheckpoint("platform", 42, hash, 1, 1_793_000_000, signer.KeyId);
        var sig = cp.Sign(signer);
        Assert.Equal(64, sig.Length);
        Assert.True(cp.Verify(sig, verifier));
        Assert.False((cp with { Seq = 43 }).Verify(sig, verifier));
        Assert.False((cp with { RecoveryEpoch = 2 }).Verify(sig, verifier));
        Assert.False((cp with { Scope = "org:x" }).Verify(sig, verifier));
    }

    [Fact]
    public void Es256_key_survives_pkcs8_round_trip()
    {
        using var a = Es256Signer.Generate("k");
        using var b = Es256Signer.FromPkcs8(a.ExportPkcs8(), "k");
        using var v = a.Verifier();
        Assert.True(v.Verify("x"u8, b.Sign("x"u8)));
    }
}
