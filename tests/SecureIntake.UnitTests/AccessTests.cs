using SecureIntake.Application.Access;
using SecureIntake.Contracts.Catalogue;

namespace SecureIntake.UnitTests;

public class PermissionCatalogueTests
{
    private static readonly PermissionCatalogue Cat = PermissionCatalogue.Current;

    [Fact]
    public void Catalogue_loads_with_expected_shape()
    {
        Assert.Equal("secure-intake-permissions@1", Cat.Document.Format);
        Assert.True(Cat.Permissions.Count >= 60);
        Assert.NotNull(Cat.Role("platform", "provisioning_client"));
    }

    [Fact]
    public void No_permission_grants_decryption() =>
        Assert.DoesNotContain(Cat.Permissions.Keys, p => p.Contains("decrypt", StringComparison.Ordinal));

    [Fact]
    public void Every_role_grant_names_a_permission_of_the_same_scope()
    {
        foreach (var role in Cat.Document.Roles)
        {
            foreach (var p in role.Grants.Select(g => g.Permission).Concat(role.Eligible))
            {
                Assert.True(Cat.Permissions.TryGetValue(p, out var def), $"{role.Id} references unknown {p}");
                Assert.Equal(role.Scope, def!.Scope);
            }
        }
    }

    [Fact]
    public void Api_clients_are_never_eligible_for_sensitive_permissions() =>
        Assert.All(Cat.Role("organisation", "api_client")!.Eligible, p => Assert.False(Cat.Permissions[p].IsSensitive));
}

public class AuthorizerTests
{
    private static readonly Authorizer Authz = new(PermissionCatalogue.Current);
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid OtherOrg = Guid.CreateVersion7();

    private static Principal User(string[]? platform = null, string[]? orgRoles = null, string[]? elevations = null) =>
        new(Guid.CreateVersion7(), PrincipalKind.User, "usr_test", (platform ?? []).ToHashSet(),
            orgRoles is null ? new Dictionary<Guid, IReadOnlySet<string>>() : new() { [Org] = orgRoles.ToHashSet() },
            new HashSet<string>(), (elevations ?? []).ToHashSet());

    private static Principal ApiClient(params string[] perms) =>
        new(Guid.CreateVersion7(), PrincipalKind.ServiceAccount, "svc_test", new HashSet<string>(),
            new Dictionary<Guid, IReadOnlySet<string>> { [Org] = new HashSet<string> { "api_client" } }, perms.ToHashSet(), new HashSet<string>());

    [Fact]
    public void Standard_permission_is_allowed_by_base_role() =>
        Assert.True(Authz.Evaluate(User(orgRoles: ["reviewer"]), "org.submissions.review", Org).IsAllowed);

    [Fact]
    public void Membership_in_another_organisation_does_not_count() =>
        Assert.Equal(AuthorizationOutcome.Denied, Authz.Evaluate(User(orgRoles: ["owner"]), "org.dropboxes.view", OtherOrg).Outcome);

    [Fact]
    public void Elevated_permission_requires_jit_then_allows()
    {
        Assert.Equal(AuthorizationOutcome.ElevationRequired, Authz.Evaluate(User(orgRoles: ["admin"]), "org.members.manage", Org).Outcome);
        Assert.True(Authz.Evaluate(User(orgRoles: ["admin"], elevations: ["org.members.manage"]), "org.members.manage", Org).IsAllowed);
    }

    [Fact]
    public void Sensitive_permission_requires_jit_and_approval()
    {
        var owner = User(orgRoles: ["owner"], elevations: ["org.submissions.delete"]);
        Assert.Equal(AuthorizationOutcome.ApprovalRequired, Authz.Evaluate(owner, "org.submissions.delete", Org).Outcome);
        Assert.True(Authz.Evaluate(owner, "org.submissions.delete", Org, hasApproval: true).IsAllowed);
    }

    [Fact]
    public void Api_client_gets_only_its_explicit_subset()
    {
        var client = ApiClient("org.grants.issue_api");
        Assert.True(Authz.Evaluate(client, "org.grants.issue_api", Org).IsAllowed);
        Assert.False(Authz.Evaluate(client, "org.webhooks.manage", Org).IsAllowed);
        Assert.False(Authz.Evaluate(client, "org.submissions.delete", Org).IsAllowed);
    }

    [Fact]
    public void Service_account_with_human_role_stored_gets_nothing_from_it()
    {
        var sa = new Principal(Guid.CreateVersion7(), PrincipalKind.ServiceAccount, "svc_x", new HashSet<string> { "owner" },
            new Dictionary<Guid, IReadOnlySet<string>> { [Org] = new HashSet<string> { "owner" } }, new HashSet<string>(), new HashSet<string>());
        Assert.False(Authz.Evaluate(sa, "platform.organisations.provision").IsAllowed);
        Assert.False(Authz.Evaluate(sa, "org.dropboxes.manage", Org).IsAllowed);
    }

    [Fact]
    public void Provisioning_client_gets_standard_view_but_never_elevated_provisioning()
    {
        var sa = new Principal(Guid.CreateVersion7(), PrincipalKind.ServiceAccount, "svc_p", new HashSet<string> { "provisioning_client" },
            new Dictionary<Guid, IReadOnlySet<string>>(), new HashSet<string>(), new HashSet<string>());
        Assert.True(Authz.Evaluate(sa, "platform.organisations.view").IsAllowed);
        var provision = Authz.Evaluate(sa, "platform.organisations.provision");
        Assert.Equal(AuthorizationOutcome.Denied, provision.Outcome);
        Assert.Equal("elevation_not_available_to_service_accounts", provision.Reason);
        Assert.False(Authz.Evaluate(sa, "platform.organisations.approve").IsAllowed);
    }

    /// <summary>ADR-0004: no stored role, explicit permission, elevation or approval lets a service account past JIT.</summary>
    [Fact]
    public void Service_accounts_never_get_elevated_or_sensitive_permissions()
    {
        var everything = PermissionCatalogue.Current.Permissions.Keys.ToHashSet();
        var sa = new Principal(Guid.CreateVersion7(), PrincipalKind.ServiceAccount, "svc_all",
            new HashSet<string> { "owner", "provisioning_client", "security_admin" },
            new Dictionary<Guid, IReadOnlySet<string>> { [Org] = new HashSet<string> { "owner", "api_client" } },
            everything, everything);
        foreach (var def in PermissionCatalogue.Current.Permissions.Values.Where(d => d.Level != "standard"))
        {
            Assert.False(Authz.Evaluate(sa, def.Name, Org, hasApproval: true).IsAllowed, def.Name);
        }
    }

    [Fact]
    public void Api_client_with_an_elevated_permission_stored_is_still_denied() =>
        Assert.Equal("elevation_not_available_to_service_accounts", Authz.Evaluate(ApiClient("org.webhooks.manage"), "org.webhooks.manage", Org).Reason);

    [Fact]
    public void Catalogue_records_what_adr_0004_blocks_for_service_roles()
    {
        var cat = PermissionCatalogue.Current;
        Assert.Contains("platform.organisations.provision", cat.Role("platform", "provisioning_client")!.BlockedForServiceAccounts!);
        Assert.Contains("org.webhooks.manage", cat.Role("organisation", "api_client")!.BlockedForServiceAccounts!);
        foreach (var role in new[] { cat.Role("platform", "provisioning_client")!, cat.Role("organisation", "api_client")! })
        {
            Assert.All(role.Grants.Select(g => g.Permission).Concat(role.Eligible), p => Assert.Equal("standard", cat.Permissions[p].Level));
        }
        Assert.False(Authz.ApiClientMayHold("org.webhooks.manage"));
        Assert.True(Authz.ApiClientMayHold("org.grants.issue_api"));
    }

    [Fact]
    public void Recovery_procedure_permission_is_never_granted_through_rbac() =>
        Assert.False(Authz.Evaluate(User(platform: ["owner"], elevations: ["platform.emergency.activate"]), "platform.emergency.activate", hasApproval: true).IsAllowed);

    [Fact]
    public void Unknown_permission_is_denied() =>
        Assert.Equal("unknown_permission", Authz.Evaluate(User(platform: ["owner"]), "platform.everything").Reason);
}
