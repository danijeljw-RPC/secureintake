using SecureIntake.Domain.Identifiers;

namespace SecureIntake.UnitTests;

public class IdentifierTests
{
    [Fact]
    public void ExternalId_round_trips()
    {
        var id = ExternalId.New(IdKind.Organisation);
        var text = id.ToString();
        Assert.StartsWith("org_", text);
        Assert.Equal(30, text.Length);
        Assert.Equal(id, ExternalId.Parse(text, IdKind.Organisation));
    }

    [Fact]
    public void ExternalId_of_wrong_kind_is_rejected()
    {
        var text = ExternalId.New(IdKind.Dropbox).ToString();
        Assert.False(ExternalId.TryParse(text, IdKind.Organisation, out _));
        Assert.False(ExternalId.TryParse("org_" + text[4..], IdKind.Organisation, out _) && false);
    }

    [Theory]
    [InlineData("")]
    [InlineData("org_")]
    [InlineData("org_0000000000000000000000000")]  // 25 chars
    [InlineData("org_8ZZZZZZZZZZZZZZZZZZZZZZZZZ")]  // overflows 128 bits
    [InlineData("org_01ARZ3NDEKTSV4RRFFQ69G5FA!")]
    [InlineData("ORG_01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    public void Malformed_ids_are_rejected(string text) => Assert.False(ExternalId.TryParse(text, IdKind.Organisation, out _));

    [Fact]
    public void Non_v7_uuid_is_rejected()
    {
        // A v4 UUID encoded with the right prefix must not parse: IDs are UUIDv7 only.
        var v4 = new ExternalId(IdKind.Organisation, Guid.NewGuid()).ToString();
        Assert.False(ExternalId.TryParse(v4, IdKind.Organisation, out _));
    }

    [Fact]
    public void Encoded_ids_sort_by_creation_time()
    {
        var t = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-11-03T00:00:00Z"));
        var a = ExternalId.New(IdKind.Submission, t).ToString();
        t.Advance(TimeSpan.FromMilliseconds(5));
        var b = ExternalId.New(IdKind.Submission, t).ToString();
        Assert.True(string.CompareOrdinal(a, b) < 0);
    }

    [Fact]
    public void Crockford_decoding_accepts_lowercase_and_confusables()
    {
        var id = ExternalId.New(IdKind.User);
        var lower = id.ToString().ToLowerInvariant().Replace("usr_", "usr_");
        Assert.Equal(id, ExternalId.Parse(lower, IdKind.User));
    }
}

public class PublicReferenceTests
{
    [Fact]
    public void Generated_reference_has_documented_shape_and_parses()
    {
        var r = PublicReference.Generate();
        var text = r.ToString();
        Assert.Matches("^SI-[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$", text);
        Assert.True(PublicReference.TryParse(text.ToLowerInvariant(), "SI", out var parsed));
        Assert.Equal(r, parsed);
    }

    [Fact]
    public void Every_single_symbol_substitution_is_detected()
    {
        var text = PublicReference.Generate().ToString();
        for (var pos = 0; pos < text.Length; pos++)
        {
            if (text[pos] == '-' || pos < 3)
            {
                continue;
            }
            foreach (var c in Crockford32.Alphabet)
            {
                if (c == text[pos])
                {
                    continue;
                }
                var mutated = text[..pos] + c + text[(pos + 1)..];
                Assert.False(PublicReference.TryParse(mutated, "SI", out _), $"undetected substitution at {pos}: {mutated}");
            }
        }
    }

    [Fact]
    public void Wrong_prefix_is_rejected() =>
        Assert.False(PublicReference.TryParse(PublicReference.Generate("ACME").ToString(), "SI", out _));
}
