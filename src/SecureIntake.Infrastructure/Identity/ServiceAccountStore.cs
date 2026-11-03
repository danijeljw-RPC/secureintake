using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using SecureIntake.Application.Access;
using SecureIntake.Domain.Identifiers;
using SecureIntake.Infrastructure.Audit;
using SecureIntake.Infrastructure.Data;

namespace SecureIntake.Infrastructure.Identity;

public sealed record ServiceAccountCreated(Guid Id, string Token);

/// <summary>
/// Service accounts (API clients and the platform Provisioning Client). Bearer tokens are
/// <c>sit_</c> + 32 random bytes (base64url). Only <c>HMAC-SHA-256(pepper, token)</c> is stored (S10 draft),
/// so a database leak doesn't leak usable tokens, and lookup is a constant-cost indexed equality.
/// </summary>
public sealed class ServiceAccountStore(Database database, byte[] pepper, TimeProvider time) : Application.Abstractions.IBearerAuthenticator
{
    public const string TokenPrefix = "sit_";
    public const string ProvisioningClientRole = "provisioning_client";

    /// <summary>Platform service accounts can only hold the Provisioning Client role (doc 28). Human roles need human identities.</summary>
    public Task<ServiceAccountCreated> CreatePlatformAsync(string name, IReadOnlyCollection<string> roles, string actor, CancellationToken ct = default)
    {
        if (roles.Count == 0 || roles.Any(r => r != ProvisioningClientRole))
        {
            throw new ArgumentException("Platform service accounts may only hold the provisioning_client role.", nameof(roles));
        }
        return CreateAsync(name, "platform", null, roles, [], actor, ct);
    }

    public Task<ServiceAccountCreated> CreateOrganisationApiClientAsync(Guid organisationId, string name, IReadOnlyCollection<string> permissions,
        Authorizer authorizer, string actor, CancellationToken ct = default)
    {
        foreach (var p in permissions)
        {
            if (!authorizer.ApiClientMayHold(p))
            {
                throw new ArgumentException($"API clients can't hold '{p}' (Standard-level API-client permissions only, ADR-0004).", nameof(permissions));
            }
        }
        return CreateAsync(name, "organisation", organisationId, ["api_client"], permissions, actor, ct);
    }

    private Task<ServiceAccountCreated> CreateAsync(string name, string scope, Guid? org, IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> permissions, string actor, CancellationToken ct)
    {
        var d = database.Dialect;
        var id = Guid.CreateVersion7(time.GetUtcNow());
        var token = TokenPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return database.InTransactionAsync(async tx =>
        {
            await using (var cmd = tx.Command("""
                INSERT INTO service_accounts (id, name, scope, organisation_id, roles, permissions, token_digest, status, created_at)
                VALUES (@id, @name, @scope, @org, @roles, @perms, @digest, 'active', @t)
                """)
                .Param("id", d.Id(id)).Param("name", name).Param("scope", scope).Param("org", d.Id(org))
                .Param("roles", string.Join(' ', roles)).Param("perms", string.Join(' ', permissions))
                .Param("digest", Digest(token)).Param("t", time.GetUtcNow().ToUnixTimeMilliseconds()))
            {
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await AuditRecordWriter.AppendPlatformAsync(tx, d, Application.Abstractions.AuditEntry.Of("service_account.created", actor,
                new ExternalId(IdKind.ServiceAccount, id).ToString(), ("scope", scope), ("roles", string.Join(' ', roles)),
                ("permissions", string.Join(' ', permissions)), ("organisation", org?.ToString("D"))), time).ConfigureAwait(false);
            return new ServiceAccountCreated(id, token);
        }, ct);
    }

    public async Task<Principal?> AuthenticateAsync(string? bearerToken, CancellationToken ct = default)
    {
        var bearer = bearerToken;
        if (bearer is null || !bearer.StartsWith(TokenPrefix, StringComparison.Ordinal) || bearer.Length > 128)
        {
            return null;
        }
        var d = database.Dialect;
        var digest = Digest(bearer);
        return await database.InTransactionAsync(async tx =>
        {
            await using var cmd = tx.Command("SELECT id, scope, organisation_id, roles, permissions, status FROM service_accounts WHERE token_digest = @d")
                .Param("d", digest);
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false) || r.GetString(5) != "active")
            {
                return null;
            }
            var id = d.ReadId(r, 0);
            var scope = r.GetString(1);
            var org = d.ReadNullableId(r, 2);
            var roles = r.GetString(3).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var perms = r.GetString(4).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            var orgRoles = new Dictionary<Guid, IReadOnlySet<string>>();
            if (scope == "organisation" && org is { } o)
            {
                orgRoles[o] = roles;
            }
            return new Principal(id, PrincipalKind.ServiceAccount, new ExternalId(IdKind.ServiceAccount, id).ToString(),
                scope == "platform" ? roles : new HashSet<string>(), orgRoles, perms, new HashSet<string>());
        }, ct).ConfigureAwait(false);
    }

    private byte[] Digest(string token) => HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(token));
}
