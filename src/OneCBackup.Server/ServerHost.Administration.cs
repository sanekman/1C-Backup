using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace OneCBackup.Server;

public static partial class ServerHost
{
    private static void MapAdministration(WebApplication app)
    {
        app.MapPost("/management/password", async (HttpContext context, IAntiforgery anti, ServerStore store, ServerLog log) =>
        {
            await anti.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (new[] { "currentPassword", "newPassword", "confirmation" }.Any(key => form[key].Count != 1))
                return Results.Redirect(context.Request.PathBase + "/account?error=invalid");
            if (!Guid.TryParse(context.User.FindFirstValue("stamp"), out var stamp)) throw new ServerRequestException(401, "invalid_session");
            var result = await store.ChangePasswordAsync(ServerAuthentication.UserId(context.User), stamp,
                form["currentPassword"].ToString(), form["newPassword"].ToString(), form["confirmation"].ToString(), context.RequestAborted);
            if (result is PasswordChangeResult.Changed or PasswordChangeResult.StaleSession)
            {
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                if (result == PasswordChangeResult.Changed) log.Write(ServerEvent.AdministratorPasswordChanged);
                return Results.Redirect(context.Request.PathBase + (result == PasswordChangeResult.Changed ? "/login?changed=1" : "/login"));
            }
            log.Write(ServerEvent.PasswordChangeFailed);
            var code = result switch { PasswordChangeResult.IncorrectCurrent => "current", PasswordChangeResult.Mismatch => "mismatch", PasswordChangeResult.SamePassword => "same", _ => "weak" };
            return Results.Redirect(context.Request.PathBase + "/account?error=" + code);
        }).RequireAuthorization(ServerAuthentication.Administrator).RequireRateLimiting("maintenance");

        app.MapPost("/api/v1/management/diagnostics", async (HttpContext context, IAntiforgery anti, ServerDiagnostics diagnostics, ServerLog log) =>
        {
            await anti.ValidateRequestAsync(context);
            var bytes = await diagnostics.CreateAsync(context.RequestAborted);
            context.Response.Headers.CacheControl = "no-store";
            log.Write(ServerEvent.DiagnosticsDownloaded);
            return Results.File(bytes, "application/zip", "1C-Backup-Server-diagnostics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".zip");
        }).RequireAuthorization(ServerAuthentication.Administrator).RequireRateLimiting("maintenance");

        app.MapGet("/api/v1/management/audit", async (int? page, ServerStore store, CancellationToken token) =>
            Results.Json(await store.AuditPageAsync(page ?? 0, token))).RequireAuthorization(ServerAuthentication.Administrator);
    }
}
