using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;

namespace SPBackup.Server;

public static partial class ServerHost
{
    private static void MapSettingsTransfer(WebApplication app)
    {
        var management = app.MapGroup("/management/settings-transfer").RequireAuthorization(ServerAuthentication.Administrator);
        management.MapGet("/export", async (ServerSettingsTransfer transfer, CancellationToken token) =>
            Results.File(await transfer.ExportAsync(token), "application/json", "SP-Backup-server-settings.json"));
        management.MapPost("/import", async (ServerSettingsTransfer transfer, HttpContext context, IAntiforgery anti) =>
        {
            var back = context.Request.PathBase + "/settings-transfer?result=";
            if (!context.Request.HasFormContentType) return Results.Redirect(back + "invalid");
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = ServerSettingsTransfer.MaximumBytes + 16384;
            await anti.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form["confirm"] != "true" || form.Files.Count != 1 || form.Files.GetFile("settings") is not { } file)
                return Results.Redirect(back + "invalid");
            try
            {
                await transfer.ImportAsync(ServerAuthentication.UserId(context.User), DownloadStamp(context.User), file, context.RequestAborted);
                return Results.Redirect(back + "imported");
            }
            catch (ServerRequestException error) when (error.Status is 400 or 409 or 413)
            {
                return Results.Redirect(back + (error.Code == "portable_settings_target_not_empty" ? "occupied" : "invalid"));
            }
        }).RequireRateLimiting("maintenance");
    }
}
