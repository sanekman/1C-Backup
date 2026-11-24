using Microsoft.AspNetCore.Antiforgery;

namespace SPBackup.Server;

public static partial class ServerHost
{
    private static void MapOperators(WebApplication app)
    {
        app.MapPost("/management/operators", async (HttpContext context, IAntiforgery antiforgery, ServerStore store) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var grants = new List<OperatorGrant>();
            foreach (var value in form["grant"])
            {
                var parts = value?.Split(':');
                if (parts is not { Length: 2 } || !Guid.TryParseExact(parts[0], "D", out var agent)
                    || !Guid.TryParseExact(parts[1], "D", out var job))
                    return Results.BadRequest("Некорректное назначение.");
                grants.Add(new(agent, job));
            }
            var downloads = form["download"].ToHashSet(StringComparer.Ordinal);
            var allowed = grants.Select(grant => $"{grant.AgentId:D}:{grant.JobId:D}").ToHashSet(StringComparer.Ordinal);
            if (!downloads.IsSubsetOf(allowed)) return Results.BadRequest("Для скачивания включите мониторинг того же задания.");
            grants = grants.Select(grant => grant with
                { Download = downloads.Contains($"{grant.AgentId:D}:{grant.JobId:D}") }).ToList();
            Guid? id = null, stamp = null;
            var selected = form["id"].ToString();
            if (selected.Length > 0)
            {
                if (!Guid.TryParseExact(selected, "D", out var user) || !Guid.TryParseExact(form["stamp"].ToString(), "D", out var expected))
                    return Results.BadRequest("Обновите страницу оператора.");
                id = user;
                stamp = expected;
            }
            var password = form["password"].ToString();
            var saved = await store.SaveOperatorAsync(ServerAuthentication.UserId(context.User), id, stamp,
                form["name"].ToString(), password.Length == 0 ? null : password, form["enabled"] == "true", grants, context.RequestAborted);
            return Results.Redirect(context.Request.PathBase + $"/operators?edit={saved:D}");
        }).RequireAuthorization(ServerAuthentication.Administrator);
    }
}
