using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SecureIntake.Application.Abstractions;
using SecureIntake.Application.Access;
using SecureIntake.Application.Intake;
using SecureIntake.Application.Organisations;
using SecureIntake.Contracts.Operations;
using SecureIntake.Contracts.Organisations;
using SecureIntake.Domain.Common;
using SecureIntake.Domain.Identifiers;
using SecureIntake.Domain.Intake;
using SecureIntake.Domain.Organisations;

namespace SecureIntake.Api;

public sealed record HostInfo(string Version, string Profile, string Role, IReadOnlyList<string> Warnings);

public static class Endpoints
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        // Infrastructure probes: unauthenticated and minimal, status only (ADR-0004). Liveness never depends on
        // external services (doc 21). Reasons for "not_ready" are only on /health/status and in the logs.
        app.MapGet("/health/live", () => TypedResults.Ok(new HealthResponse("live")));

        app.MapGet("/health/ready", async (IHealthReporter health, CancellationToken ct) =>
            await health.IsReadyAsync(ct).ConfigureAwait(false)
                ? Results.Ok(new HealthResponse("ready"))
                : Results.Json(new HealthResponse("not_ready"), ApiJsonContext.Default.HealthResponse, statusCode: 503));

        app.MapGet("/health/status", async (HttpContext ctx, IHealthReporter health, Authorizer authz, HostInfo info, CancellationToken ct) =>
        {
            Require(authz, ctx.RequirePrincipal(), "platform.dashboard.view");
            var components = await health.ComponentsAsync(ct).ConfigureAwait(false);
            var overall = components.Any(c => c.Status == "unhealthy") ? "unhealthy"
                : components.Any(c => c.Status == "degraded") ? "degraded" : "healthy";
            return TypedResults.Ok(new HealthStatusResponse(overall, info.Version, info.Profile, info.Role,
                components.ToDictionary(c => c.Name, c => c.Status),
                [.. components.Where(c => c.Reason is not null).Select(c => c.Reason!)], info.Warnings));
        });
        return app;
    }

    public static IEndpointRouteBuilder MapPlatformOrganisations(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/platform/organisations");

        g.MapGet("/", async (HttpContext ctx, OrganisationService svc, int? limit, CancellationToken ct) =>
        {
            var list = await svc.ListAsync(ctx.RequirePrincipal(), limit ?? 100, ct).ConfigureAwait(false);
            return TypedResults.Ok(new OrganisationListResponse([.. list.Select(ToResponse)]));
        });

        g.MapPost("/", async (HttpContext ctx, OrganisationService svc, CreateOrganisationRequest body, CancellationToken ct) =>
        {
            var principal = ctx.RequirePrincipal();
            var method = principal.Kind == PrincipalKind.ServiceAccount ? ProvisioningMethod.Api : ProvisioningMethod.Manual;
            var org = await svc.CreateAsync(principal, body.DisplayName, body.Slug, method, ct).ConfigureAwait(false);
            var response = ToResponse(org);
            return TypedResults.Created($"/api/platform/organisations/{response.Id}", response);
        });

        g.MapGet("/{id}", async (HttpContext ctx, OrganisationService svc, string id, CancellationToken ct) =>
        {
            var org = await svc.GetAsync(ctx.RequirePrincipal(), ParseOrg(id), ct).ConfigureAwait(false)
                ?? throw new DomainException("organisation.not_found", "Organisation not found.");
            return TypedResults.Ok(ToResponse(org));
        });

        g.MapPost("/{id}/approve", async (HttpContext ctx, OrganisationService svc, string id, CancellationToken ct) =>
            TypedResults.Ok(ToResponse(await svc.ApproveAsync(ctx.RequirePrincipal(), ParseOrg(id), ct).ConfigureAwait(false))));

        g.MapPost("/{id}/activate", async (HttpContext ctx, OrganisationService svc, string id, CancellationToken ct) =>
            TypedResults.Ok(ToResponse(await svc.ActivateAsync(ctx.RequirePrincipal(), ParseOrg(id), ct).ConfigureAwait(false))));

        g.MapPost("/{id}/suspend", async (HttpContext ctx, OrganisationService svc, string id, SuspendOrganisationRequest body, CancellationToken ct) =>
            TypedResults.Ok(ToResponse(await svc.SuspendAsync(ctx.RequirePrincipal(), ParseOrg(id), body.Reason, ct).ConfigureAwait(false))));

        g.MapPost("/{id}/reactivate", async (HttpContext ctx, OrganisationService svc, string id, CancellationToken ct) =>
            TypedResults.Ok(ToResponse(await svc.ReactivateAsync(ctx.RequirePrincipal(), ParseOrg(id), ct).ConfigureAwait(false))));

        return app;
    }

    public static IEndpointRouteBuilder MapOrganisationDropboxes(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/organisations/{orgId}/dropboxes");

        g.MapGet("/", async (HttpContext ctx, DropboxService svc, string orgId, int? limit, CancellationToken ct) =>
        {
            var list = await svc.ListAsync(ctx.RequirePrincipal(), ParseOrg(orgId), limit ?? 100, ct).ConfigureAwait(false);
            return TypedResults.Ok(new DropboxListResponse([.. list.Select(ToResponse)]));
        });

        g.MapPost("/", async (HttpContext ctx, DropboxService svc, string orgId, CreateDropboxRequest body, CancellationToken ct) =>
        {
            var dropbox = await svc.CreateAsync(ctx.RequirePrincipal(), ParseOrg(orgId), body.Name, ct).ConfigureAwait(false);
            var response = ToResponse(dropbox);
            return TypedResults.Created($"/api/organisations/{orgId}/dropboxes/{response.Id}", response);
        });

        g.MapGet("/{dropboxId}", async (HttpContext ctx, DropboxService svc, string orgId, string dropboxId, CancellationToken ct) =>
        {
            if (!ExternalId.TryParse(dropboxId, IdKind.Dropbox, out var dbx))
            {
                throw new DomainException("dropbox.not_found", "Dropbox not found.");
            }
            var dropbox = await svc.GetAsync(ctx.RequirePrincipal(), ParseOrg(orgId), dbx.Value.Value, ct).ConfigureAwait(false)
                ?? throw new DomainException("dropbox.not_found", "Dropbox not found.");
            return TypedResults.Ok(ToResponse(dropbox));
        });
        return app;
    }

    /// <summary>A malformed or wrong-kind ID is a 404, never a coercion (F5).</summary>
    private static Guid ParseOrg(string id) =>
        ExternalId.TryParse(id, IdKind.Organisation, out var parsed) ? parsed.Value.Value
            : throw new DomainException("organisation.not_found", "Organisation not found.");

    private static void Require(Authorizer authz, Principal principal, string permission)
    {
        var result = authz.Evaluate(principal, permission);
        if (!result.IsAllowed)
        {
            throw new AuthorizationDeniedException(result);
        }
    }

    private static OrganisationResponse ToResponse(Organisation o) => new(
        new ExternalId(IdKind.Organisation, o.Id).ToString(), o.DisplayName, o.Slug, Snake(o.Status.ToString()),
        Snake(o.Method.ToString()), o.Version, o.CreatedAt);

    private static DropboxResponse ToResponse(Dropbox d) => new(
        new ExternalId(IdKind.Dropbox, d.Id).ToString(), new ExternalId(IdKind.Organisation, d.OrganisationId).ToString(),
        d.Name, Snake(d.Status.ToString()), d.Version);

    private static string Snake(string pascal) =>
        string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
