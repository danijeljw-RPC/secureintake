using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SecureIntake.Application.Abstractions;
using SecureIntake.Application.Access;
using SecureIntake.Contracts;
using SecureIntake.Contracts.Organisations;
using SecureIntake.Domain.Common;

namespace SecureIntake.Api;

public static partial class ApiPipeline
{
    private const string PrincipalKey = "SecureIntake.Principal";

    /// <summary>Security headers, version negotiation, error mapping and bearer authentication, in that order.</summary>
    public static IApplicationBuilder UseSecureIntakeApi(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            h["X-Frame-Options"] = "DENY";
            h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            h["Cache-Control"] = "no-store";
            h[ApiVersion.HeaderName] = ApiVersion.Current;

            if (ctx.Request.Headers.TryGetValue(ApiVersion.HeaderName, out var requested)
                && !ApiVersion.Supported.Contains(requested.ToString()))
            {
                await WriteProblem(ctx, 400, "api.unsupported_version", "Unsupported API version",
                    $"Supported versions: {string.Join(", ", ApiVersion.Supported)}.").ConfigureAwait(false);
                return;
            }
            try
            {
                await next(ctx).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ctx.Response.HasStarted)
            {
                await MapException(ctx, ex).ConfigureAwait(false);
            }
        });

        app.Use(async (ctx, next) =>
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                var authenticator = ctx.RequestServices.GetRequiredService<IBearerAuthenticator>();
                var principal = await authenticator.AuthenticateAsync(auth[7..].Trim(), ctx.RequestAborted).ConfigureAwait(false);
                if (principal is null)
                {
                    ctx.Response.Headers.WWWAuthenticate = "Bearer";
                    await WriteProblem(ctx, 401, "auth.invalid_token", "Unauthorised", null).ConfigureAwait(false);
                    return;
                }
                ctx.Items[PrincipalKey] = principal;
            }
            await next(ctx).ConfigureAwait(false);
        });
        return app;
    }

    /// <summary>The authenticated principal. Throws so the error mapper returns 401 when there isn't one.</summary>
    public static Principal RequirePrincipal(this HttpContext ctx) =>
        ctx.Items.TryGetValue(PrincipalKey, out var p) && p is Principal principal ? principal : throw new UnauthenticatedException();

    public static Principal? OptionalPrincipal(this HttpContext ctx) =>
        ctx.Items.TryGetValue(PrincipalKey, out var p) ? p as Principal : null;

    private static Task MapException(HttpContext ctx, Exception ex)
    {
        switch (ex)
        {
            case UnauthenticatedException:
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                return WriteProblem(ctx, 401, "auth.required", "Unauthorised", null);
            case AuthorizationDeniedException denied:
                var code = denied.Result.Outcome switch
                {
                    AuthorizationOutcome.ElevationRequired => "access.elevation_required",
                    AuthorizationOutcome.ApprovalRequired => "access.approval_required",
                    _ => "access.denied",
                };
                return WriteProblem(ctx, 403, code, "Forbidden", $"Permission: {denied.Result.Permission}");
            case DomainException d when d.Code.EndsWith(".not_found", StringComparison.Ordinal):
                return WriteProblem(ctx, 404, d.Code, "Not found", null);
            case DomainException d when d.Code.Contains(".invalid_", StringComparison.Ordinal) && !d.Code.EndsWith("invalid_transition", StringComparison.Ordinal):
                return WriteProblem(ctx, 422, d.Code, "Validation failed", d.Message);
            case DomainException d:
                return WriteProblem(ctx, 409, d.Code, "Conflict", d.Message);
            case ConcurrencyException:
                return WriteProblem(ctx, 409, "concurrency.conflict", "Conflict", "The resource changed; reload and retry.");
            case TenantFencedException:
                ctx.Response.Headers.RetryAfter = "30";
                return WriteProblem(ctx, 503, "tenant.fenced", "Temporarily unavailable", null);
            case BadHttpRequestException or JsonException:
                return WriteProblem(ctx, 400, "request.malformed", "Bad request", null);
            default:
                var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SecureIntake.Api");
                LogUnhandled(log, ex.GetType().Name, ctx.TraceIdentifier, ex);
                return WriteProblem(ctx, 500, "server.error", "Internal error", $"Trace: {ctx.TraceIdentifier}");
        }
    }

    public static Task WriteProblem(HttpContext ctx, int status, string code, string title, string? detail)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        var problem = new ProblemResponse($"https://secureintake.invalid/problems/{code}", title, status, code, detail);
        return JsonSerializer.SerializeAsync(ctx.Response.Body, problem, ApiJsonContext.Default.ProblemResponse, ctx.RequestAborted);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled {ExceptionType} for trace {TraceId}")]
    private static partial void LogUnhandled(ILogger logger, string exceptionType, string traceId, Exception ex);
}

public sealed class UnauthenticatedException : Exception
{
    public UnauthenticatedException() : base("Authentication required.") { }
    public UnauthenticatedException(string message) : base(message) { }
    public UnauthenticatedException(string message, Exception inner) : base(message, inner) { }
}
