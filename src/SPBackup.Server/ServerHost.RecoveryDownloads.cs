using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;

namespace SPBackup.Server;

public static partial class ServerHost
{
    internal static Guid DownloadStamp(ClaimsPrincipal user) => Guid.TryParse(user.FindFirstValue("stamp"), out var stamp)
        ? stamp : throw new ServerRequestException(401, "invalid_session");

    private static void MapRecoveryDownloads(WebApplication app)
    {
        var human = app.MapGroup("/management/recovery-downloads").RequireAuthorization(ServerAuthentication.PanelUser);
        human.MapPost("", async (HttpContext context, IAntiforgery antiforgery, RecoveryDownloads downloads) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!Guid.TryParse(form["agent"], out var agent) || !Guid.TryParse(form["job"], out var job)
                || !Guid.TryParse(form["run"], out var run) || !int.TryParse(form["destination"], out var destination))
                throw new ServerRequestException(400, "invalid_recovery_point");
            var id = await downloads.CreateAsync(ServerAuthentication.UserId(context.User), DownloadStamp(context.User),
                agent, job, run, destination, context.RequestAborted);
            return Results.Redirect(context.Request.PathBase + $"/operator/downloads/{id:D}");
        });
        human.MapPost("/{id:guid}/cancel", async (Guid id, HttpContext context, IAntiforgery antiforgery, RecoveryDownloads downloads) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await downloads.CancelAsync(ServerAuthentication.UserId(context.User), DownloadStamp(context.User), id, context.RequestAborted);
            return Results.Redirect(context.Request.PathBase + (context.User.IsInRole(ServerAuthentication.Administrator) ? "/" : "/operator"));
        });
        human.MapGet("/{id:guid}/file", (Guid id, HttpContext context, RecoveryDownloads downloads) =>
            downloads.SendAsync(ServerAuthentication.UserId(context.User), DownloadStamp(context.User), id, context));

        app.MapPost("/management/recovery-downloads/{id:guid}/admin-cancel", async (Guid id, HttpContext context,
            IAntiforgery antiforgery, RecoveryDownloads downloads) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await downloads.CancelByAdministratorAsync(ServerAuthentication.UserId(context.User), DownloadStamp(context.User), id, context.RequestAborted);
            return Results.Redirect(context.Request.PathBase + "/recovery-downloads");
        }).RequireAuthorization(ServerAuthentication.Administrator);

        var agents = app.MapGroup("/api/v1/agents/{agentId:guid}/recovery-downloads").RequireAuthorization("Agent");
        agents.AddEndpointFilter((context, next) =>
        {
            if (ServerAuthentication.UserId(context.HttpContext.User) != Guid.Parse(context.HttpContext.Request.RouteValues["agentId"]!.ToString()!))
                throw new ServerRequestException(403, "agent_mismatch");
            return next(context);
        });
        agents.MapGet("", async (Guid agentId, HttpContext context, RecoveryDownloads downloads) =>
        {
            var offer = await downloads.PollAsync(agentId, ServerAuthentication.AgentGeneration(context.User), context.RequestAborted);
            return offer is null ? Results.NoContent() : Results.Json(offer);
        });
        agents.MapGet("/{id:guid}", async (Guid agentId, Guid id, HttpContext context, RecoveryDownloads downloads) =>
        {
            await downloads.RequireAgentAccessAsync(agentId, ServerAuthentication.AgentGeneration(context.User), id, context.RequestAborted);
            return Results.NoContent();
        });
        agents.MapPost("/{id:guid}/failed", async (Guid agentId, Guid id, HttpContext context, RecoveryDownloads downloads) =>
        {
            await downloads.FailAsync(agentId, ServerAuthentication.AgentGeneration(context.User), id, context.RequestAborted);
            return Results.NoContent();
        });
        agents.MapPut("/{id:guid}/file", async (Guid agentId, Guid id, HttpContext context, RecoveryDownloads downloads) =>
        {
            var length = context.Request.ContentLength;
            if (length is null or < 1 or > RecoveryDownloads.MaximumBytes) throw new ServerRequestException(413, "invalid_recovery_length");
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
                bodySize.MaxRequestBodySize = length.Value;
            await downloads.UploadAsync(agentId, ServerAuthentication.AgentGeneration(context.User), id, length.Value,
                context.Request.Headers["X-Recovery-Sha256"].ToString(), context.Request.Body, context.RequestAborted);
            return Results.NoContent();
        });
    }
}
