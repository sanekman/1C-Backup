using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;

namespace SPBackup.Server;

public static partial class ServerHost
{
    private static void MapConfiguration(WebApplication app)
    {
        var routes = app.MapGroup("/api/v1/management/agents/{id:guid}/configuration")
            .RequireAuthorization(ServerAuthentication.Administrator);
        routes.MapGet("", async (Guid id, HttpContext context, ServerStore store, CancellationToken token) =>
        {
            var document = await store.ReadFileConfigurationAsync(id, token);
            context.Response.Headers.ETag = FileConfigurationContract.ETag(document.Revision);
            return Results.Json(document, FileConfigurationContract.Json);
        });
        routes.MapPut("", async (Guid id, HttpContext context, IAntiforgery anti, ServerStore store) =>
        {
            await anti.ValidateRequestAsync(context);
            var expected = FileConfigurationContract.ExpectedRevision(context);
            if (!Guid.TryParse(context.User.FindFirstValue("stamp"), out var stamp)) throw new ServerRequestException(401, "invalid_session");
            var request = await FileConfigurationContract.ReadAsync(context);
            var document = await store.PublishFileConfigurationAsync(ServerAuthentication.UserId(context.User), stamp, id,
                expected, request, context.RequestAborted);
            context.Response.Headers.ETag = FileConfigurationContract.ETag(document.Revision);
            return Results.Json(document, FileConfigurationContract.Json);
        });
    }
}
