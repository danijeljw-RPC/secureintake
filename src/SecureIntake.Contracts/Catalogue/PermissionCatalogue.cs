using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecureIntake.Contracts.Catalogue;

public sealed record PermissionDefinition(string Name, string Scope, string Level, string? Note)
{
    public bool IsSensitive => Level == "sensitive";
    public bool IsElevated => Level is "elevated" or "sensitive";
}

public sealed record RoleGrant(string Permission, string? Qualifier);

/// <param name="BlockedForServiceAccounts">Doc 28 cells removed by ADR-0004 because they need human JIT elevation.</param>
public sealed record RoleDefinition(string Id, string Scope, IReadOnlyList<RoleGrant> Grants, IReadOnlyList<string> Eligible,
    IReadOnlyList<string>? BlockedForServiceAccounts = null);

public sealed record PermissionCatalogueDocument(
    string Format, string CatalogueVersion, IReadOnlyList<PermissionDefinition> Permissions, IReadOnlyList<RoleDefinition> Roles);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PermissionCatalogueDocument))]
internal sealed partial class CatalogueJsonContext : JsonSerializerContext;

/// <summary>The generated, versioned permission catalogue (doc 28). Loaded once from the embedded <c>permissions.json</c>.</summary>
public sealed class PermissionCatalogue
{
    private static readonly Lazy<PermissionCatalogue> Lazy = new(Load);

    private PermissionCatalogue(PermissionCatalogueDocument doc)
    {
        Document = doc;
        Permissions = doc.Permissions.ToFrozenDictionary(p => p.Name, StringComparer.Ordinal);
        Roles = doc.Roles.ToFrozenDictionary(r => Key(r.Scope, r.Id), StringComparer.Ordinal);
    }

    public static PermissionCatalogue Current => Lazy.Value;

    public PermissionCatalogueDocument Document { get; }
    public FrozenDictionary<string, PermissionDefinition> Permissions { get; }
    public FrozenDictionary<string, RoleDefinition> Roles { get; }

    public RoleDefinition? Role(string scope, string roleId) => Roles.GetValueOrDefault(Key(scope, roleId));

    public bool RoleGrants(string scope, string roleId, string permission) =>
        Role(scope, roleId)?.Grants.Any(g => g.Permission == permission) ?? false;

    private static string Key(string scope, string id) => scope + ":" + id;

    private static PermissionCatalogue Load()
    {
        using var stream = typeof(PermissionCatalogue).Assembly.GetManifestResourceStream("SecureIntake.permissions.json")
            ?? throw new InvalidOperationException("Embedded permission catalogue is missing.");
        var doc = JsonSerializer.Deserialize(stream, CatalogueJsonContext.Default.PermissionCatalogueDocument)
            ?? throw new InvalidOperationException("Permission catalogue is empty.");
        if (doc.Format != "secure-intake-permissions@1")
        {
            throw new InvalidOperationException($"Unsupported permission catalogue format '{doc.Format}'.");
        }
        return new PermissionCatalogue(doc);
    }
}
