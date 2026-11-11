using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;

namespace SPBackup.Server;
public static partial class ServerHost
{
    private static readonly JsonSerializerOptions CommandJson = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true };
    private static async Task<T> ReadCommandAsync<T>(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new ServerRequestException(415, "json_required");
        using var bytes = new MemoryStream();
        var buffer = new byte[4096]; int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
        {
            if (bytes.Length + read > 8192) throw new ServerRequestException(413, "command_too_large");
            bytes.Write(buffer, 0, read);
        }
        try { return JsonSerializer.Deserialize<T>(bytes.ToArray(), CommandJson) ?? throw new JsonException(); }
        catch (JsonException) { throw new ServerRequestException(400, "invalid_remote_command"); }
    }
    private static void MapRemoteRuns(WebApplication app)
    {
        app.MapGet("/api/v1/management/problems", async (int? page, string? kind, string? search, Guid? folder, bool? root,
            ServerStore store, ServerOptions options, CancellationToken token) => Results.Json(await store.ProblemsAsync(page ?? 0,
                kind, search, folder, root ?? false, options.OfflineAfterSeconds, token))).RequireAuthorization(ServerAuthentication.Administrator);
        var admin = app.MapGroup("/api/v1/management/agents/{id:guid}/commands").RequireAuthorization(ServerAuthentication.Administrator);
        admin.MapGet("", async (Guid id, ServerStore store, CancellationToken token) => Results.Json(await store.RemoteRunsAsync(id, token)));
        admin.MapPost("", async (Guid id, HttpContext context, ServerStore store, IAntiforgery anti) =>
        {
            await anti.ValidateRequestAsync(context);
            if (!Guid.TryParse(context.User.FindFirstValue("stamp"), out var stamp)) throw new ServerRequestException(401, "invalid_session");
            return Results.Json(await store.CreateRemoteRunAsync(ServerAuthentication.UserId(context.User), stamp, id,
                await ReadCommandAsync<RemoteRunRequest>(context), context.RequestAborted), statusCode: 202);
        });
        var agent = app.MapGroup("/api/v1/agents/{agentId:guid}/commands").RequireAuthorization("Agent");
        agent.AddEndpointFilter(async (context, next) =>
        {
            if (ServerAuthentication.UserId(context.HttpContext.User) != Guid.Parse(context.HttpContext.Request.RouteValues["agentId"]!.ToString()!))
                throw new ServerRequestException(403, "agent_mismatch");
            if (!ConfigurationCompatibility.ReadHeader(context.HttpContext).Contains(AgentCapabilities.RemoteRunsV1))
                throw new ServerRequestException(409, "remote_runs_unsupported");
            return await next(context);
        });
        agent.MapGet("", async (Guid agentId, HttpContext context, ServerStore store) =>
            Results.Json(await store.PollRemoteRunsAsync(agentId, ServerAuthentication.AgentGeneration(context.User), context.RequestAborted)));
        agent.MapPost("/receipt", async (Guid agentId, HttpContext context, ServerStore store) =>
        {
            await store.SaveRemoteRunReceiptAsync(agentId, ServerAuthentication.AgentGeneration(context.User),
                await ReadCommandAsync<RemoteRunReceipt>(context), context.RequestAborted);
            return Results.NoContent();
        });
    }
}
