using Microsoft.AspNetCore.Antiforgery;

namespace SPBackup.Server;

public static partial class ServerHost
{
    private static void MapServerUpdates(WebApplication app)
    {
        var api = app.MapGroup("/api/v1/management/server-update").RequireAuthorization(ServerAuthentication.Administrator);
        api.MapGet("", async (HttpContext context, ServerUpdates updates) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(await updates.ReadAsync(context.RequestAborted));
        });
        app.MapPost("/management/server-update/{action}", async (string action, HttpContext context, IAntiforgery anti, ServerUpdates updates) =>
        {
            await anti.ValidateRequestAsync(context);
            if (!context.Request.HasFormContentType) return Results.StatusCode(415);
            if (action == "check") await updates.CheckAsync(context.RequestAborted);
            else if (action == "start")
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                await updates.StartAsync(form["version"].ToString(), context.RequestAborted);
            }
            else return Results.NotFound();
            return Results.Redirect(context.Request.PathBase + "/settings");
        }).RequireAuthorization(ServerAuthentication.Administrator);
    }
}
