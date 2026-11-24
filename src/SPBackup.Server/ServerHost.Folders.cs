using Microsoft.AspNetCore.Antiforgery;

namespace SPBackup.Server;

public static partial class ServerHost
{
    private static void MapFolders(WebApplication app)
    {
        var folders = app.MapGroup("/api/v1/management/folders").RequireAuthorization(ServerAuthentication.Administrator);
        folders.MapGet("", async (ServerStore store, CancellationToken token) => Results.Json(await store.FoldersAsync(token)));
        folders.MapPost("", async (FolderRequest request, HttpContext context, IAntiforgery anti, ServerStore store) =>
        {
            await anti.ValidateRequestAsync(context);
            var id = await store.CreateFolderAsync(ServerAuthentication.UserId(context.User), request, context.RequestAborted);
            return Results.Json(new { id }, statusCode: 201);
        });
        folders.MapPut("/{id:guid}", async (Guid id, FolderRequest request, HttpContext context, IAntiforgery anti, ServerStore store) =>
        {
            await anti.ValidateRequestAsync(context);
            await store.UpdateFolderAsync(ServerAuthentication.UserId(context.User), id, request, context.RequestAborted);
            return Results.NoContent();
        });
        folders.MapDelete("/{id:guid}", async (Guid id, HttpContext context, IAntiforgery anti, ServerStore store) =>
        {
            await anti.ValidateRequestAsync(context);
            await store.DeleteFolderAsync(ServerAuthentication.UserId(context.User), id, context.RequestAborted);
            return Results.NoContent();
        });
        app.MapPut("/api/v1/management/agents/{id:guid}/identity", async (Guid id, AgentIdentityRequest request,
            HttpContext context, IAntiforgery anti, ServerStore store) =>
        {
            await anti.ValidateRequestAsync(context);
            await store.UpdateAgentIdentityAsync(ServerAuthentication.UserId(context.User), id, request, context.RequestAborted);
            return Results.NoContent();
        }).RequireAuthorization(ServerAuthentication.Administrator);
    }
}
