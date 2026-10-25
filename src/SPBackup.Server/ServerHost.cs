using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;
using SPBackup.Server.Components;

namespace SPBackup.Server;

public static partial class ServerHost
{
    public static WebApplication Build(string[] args)
    {
        if (!args.Contains("--contentRoot") && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_CONTENTROOT")))
            args = [.. args, "--contentRoot", Path.GetDirectoryName(typeof(ServerHost).Assembly.Location)!];
        // Trust is configured explicitly below; never enable the framework's trust-all forwarding switch.
        args = [.. args, "--ForwardedHeaders_Enabled", "false"];
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ApplicationName = typeof(ServerHost).Assembly.FullName });
        var extraConfiguration = builder.Configuration["Server:ConfigFile"] ?? Environment.GetEnvironmentVariable("ONECBACKUP_SERVER_CONFIG_FILE");
        if (extraConfiguration is not null)
        {
            if (!Path.IsPathFullyQualified(extraConfiguration)) throw new ServerRequestException(400, "absolute_configuration_path_required");
            builder.Configuration.AddJsonFile(extraConfiguration, optional: false, reloadOnChange: false).AddEnvironmentVariables().AddCommandLine(args);
        }
        var settings = builder.Configuration.GetSection("Server").Get<ServerOptions>() ?? new();
        if (!settings.HasValidIdentity() || !Path.IsPathFullyQualified(settings.DataDirectory) || settings.OfflineAfterSeconds is < 30 or > 86400
            || settings.PathBase is not null && (settings.PathBase.Length < 2 || !settings.PathBase.StartsWith('/') || settings.PathBase.EndsWith('/')
                || settings.PathBase.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '/' and not '-' and not '_')))
            throw new ServerRequestException(400, "invalid_server_options");
        var connectionString = settings.ConnectionStringFile is { } file ? ReadSecret(file) : settings.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ServerRequestException(400, "database_configuration_required");
        var database = new NpgsqlConnectionStringBuilder(connectionString);
        if (settings.DatabasePasswordFile is { } databasePassword) database.Password = ReadSecret(databasePassword);
        if (database.IncludeErrorDetail || database.LogParameters) throw new ServerRequestException(400, "unsafe_database_logging");
        database.Timeout = Math.Min(database.Timeout, 10); database.CommandTimeout = 15;
        builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "https://localhost:7443");
        if (settings.HttpsCertificatePath is { } certificate)
        {
            var password = settings.HttpsCertificatePasswordFile is { } passwordFile ? ReadSecret(passwordFile) : null;
            var cert = X509CertificateLoader.LoadPkcs12FromFile(certificate, password);
            builder.Services.AddSingleton<X509Certificate2>(_ => cert);
            builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https => https.ServerCertificate = cert));
        }
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024 * 1024);
        builder.Host.UseWindowsService(options => options.ServiceName = "OneCBackupServer").UseSystemd();
        builder.Logging.ClearProviders(); // Application logs use typed events; no raw framework/database exception strings.
        var keys = Path.Combine(settings.DataDirectory, "keys");
        Directory.CreateDirectory(keys);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(database.ConnectionString));
        builder.Services.AddSingleton<ServerStore>(); builder.Services.AddSingleton<ServerLog>();
        builder.Services.AddSingleton<ServerDiagnostics>();
        var protection = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)).SetApplicationName("OneCBackup.Server");
        if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
        ServerAuthentication.Add(builder, settings);
        builder.Services.AddRazorComponents();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddOpenApi();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
            options.KnownIPNetworks.Clear(); options.KnownProxies.Clear();
            foreach (var proxy in settings.TrustedProxies) options.KnownProxies.Add(IPAddress.Parse(proxy));
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("maintenance", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local",
                _ => new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local",
                _ => new() { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("enrollment", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "local",
                _ => new() { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        var app = builder.Build();
        if (settings.HttpsCertificatePath is not null) _ = app.Services.GetRequiredService<X509Certificate2>();
        if (settings.TrustedProxies.Length > 0) app.UseForwardedHeaders();
        if (settings.PathBase is not null) app.UsePathBase(settings.PathBase);
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method) && (context.Request.Path == "/" || context.Request.Path == "/system" || context.Request.Path.StartsWithSegments("/agents")))
                context.Response.Headers["Refresh"] = "30";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self'; form-action 'self'";
            var localDevelopment = app.Environment.IsDevelopment() && settings.AllowInsecureLoopback
                && context.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote);
            if (!context.Request.IsHttps && !localDevelopment && !context.Request.Path.StartsWithSegments("/health"))
            { await Problem(context, 426, "https_required"); return; }
            try { await next(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception ex)
            {
                app.Services.GetRequiredService<ServerLog>().Write(ServerEvent.RequestFailed, ex, requestId: context.TraceIdentifier);
                if (context.Response.HasStarted) { context.Abort(); return; }
                context.Response.Clear();
                var status = ex is ServerRequestException error ? error.Status : ex is AntiforgeryValidationException or BadHttpRequestException ? 400 : 503;
                await Problem(context, status, ex is ServerRequestException known ? known.Code : status == 400 ? "invalid_request" : "service_unavailable");
            }
        });
        app.UseStaticFiles(); app.UseRouting(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery();
        app.MapGet("/health/live", () => Results.Text("live"));
        app.MapGet("/health/ready", async (ServerStore store, CancellationToken token) => await store.ReadyAsync(token) ? Results.Text("ready") : Results.StatusCode(503));
        app.MapOpenApi().RequireAuthorization(ServerAuthentication.Administrator);
        MapSessions(app); MapApi(app); MapAdministration(app);
        app.MapRazorComponents<App>();
        return app;
    }
    public static string ReadSecret(string path)
    {
        if (!Path.IsPathFullyQualified(path) || new FileInfo(path).Length > 8192) throw new ServerRequestException(400, "invalid_secret_file");
        return File.ReadAllText(path).TrimEnd('\r', '\n');
    }
    private static Task Problem(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status; context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(new ServerProblem(status, "Запрос не выполнен", code, context.TraceIdentifier), context.RequestAborted);
    }
    private static void MapSessions(WebApplication app)
    {
        app.MapPost("/session/login", async (HttpContext context, IAntiforgery anti, ServerStore store, ServerLog log) =>
        {
            await anti.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var name = form["name"].ToString(); var password = form["password"].ToString();
            if (name.Length is < 1 or > 80 || password.Length is < 1 or > 256) throw new ServerRequestException(400, "invalid_login");
            var user = await store.LoginAsync(name, password, context.RequestAborted);
            if (user is null) { log.Write(ServerEvent.LoginFailed); return Results.Redirect(context.Request.PathBase + "/login?failed=1"); }
            await store.RecordAdministratorLoginAsync(user.Id, context.RequestAborted);
            var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, user.Id.ToString("D")), new(ClaimTypes.Name, user.Name),
                new(ClaimTypes.Role, ServerAuthentication.Administrator), new("stamp", user.SecurityStamp.ToString("D"))], CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            log.Write(ServerEvent.LoginSucceeded);
            return Results.Redirect(context.Request.PathBase + "/");
        }).RequireRateLimiting("login");
        app.MapPost("/session/logout", async (HttpContext context, IAntiforgery anti) =>
        {
            await anti.ValidateRequestAsync(context);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect(context.Request.PathBase + "/login");
        }).RequireAuthorization(ServerAuthentication.Administrator);
        app.MapPost("/management/agents/{id:guid}/revoke", async (Guid id, HttpContext context, IAntiforgery anti, ServerStore store, ServerLog log) =>
        {
            await anti.ValidateRequestAsync(context);
            await store.RevokeAsync(ServerAuthentication.UserId(context.User), id, context.RequestAborted);
            log.Write(ServerEvent.AgentRevoked, agentId: id);
            return Results.Redirect(context.Request.PathBase + "/agents/" + id);
        }).RequireAuthorization(ServerAuthentication.Administrator);
    }
    private static void MapApi(WebApplication app)
    {
        app.MapPost("/api/v1/enrollment", async (EnrollmentRequest request, ServerStore store, ServerLog log, CancellationToken token) =>
        {
            var result = await store.EnrollAsync(request, token); log.Write(ServerEvent.AgentEnrolled, agentId: result.AgentId);
            return Results.Json(result, statusCode: 201);
        }).RequireRateLimiting("enrollment");
        var agents = app.MapGroup("/api/v1/agents/{agentId:guid}").RequireAuthorization("Agent");
        agents.AddEndpointFilter(async (context, next) =>
        {
            var pathId = Guid.Parse(context.HttpContext.Request.RouteValues["agentId"]!.ToString()!);
            if (ServerAuthentication.UserId(context.HttpContext.User) != pathId) throw new ServerRequestException(403, "agent_mismatch");
            return await next(context);
        });
        agents.MapGet("/configuration", async (Guid agentId, HttpContext context, ServerStore store, CancellationToken token) =>
        {
            var configuration = await store.ConfigurationAsync(agentId, token);
            var etag = '"' + configuration.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + '"';
            context.Response.Headers.ETag = etag;
            if (context.Request.GetTypedHeaders().IfNoneMatch?.Any(value => value.Tag.Value == etag || value.Tag.Value == "*") == true) return Results.StatusCode(304);
            return Results.Content(configuration.Document, "application/json");
        });
        agents.MapPost("/heartbeat", async (Guid agentId, HeartbeatRequest request, ServerStore store, CancellationToken token) =>
        { await store.HeartbeatAsync(agentId, request, token); return Results.NoContent(); });
        agents.MapPut("/monitoring", async (Guid agentId, Monitoring.MonitoringSnapshot request, ServerStore store, CancellationToken token) =>
        { await store.SaveMonitoringAsync(agentId, request, token); return Results.NoContent(); });
        agents.MapPost("/runs", async (Guid agentId, Monitoring.MonitoredRun request, ServerStore store, CancellationToken token) =>
        { await store.SaveRunAsync(agentId, request, token); return Results.NoContent(); });
        var management = app.MapGroup("/api/v1/management").RequireAuthorization(ServerAuthentication.Administrator);
        management.MapGet("/session", (HttpContext context, IAntiforgery anti) => Results.Json(new { name = context.User.Identity!.Name, csrfToken = anti.GetAndStoreTokens(context).RequestToken }));
        management.MapGet("/agents/{id:guid}/monitoring", async (Guid id, ServerStore store, CancellationToken token) => Results.Json(await store.MonitoringAsync(id,token)));
        management.MapGet("/agents/{id:guid}/runs", async (Guid id, int? page, ServerStore store, CancellationToken token) => Results.Json(await store.RunsAsync(id,page ?? 0,token)));
        management.MapGet("/agents", async (int? page, ServerStore store, CancellationToken token) => Results.Json(await store.AgentsAsync(page ?? 0, token)));
        management.MapGet("/agents/{id:guid}", async (Guid id, ServerStore store, CancellationToken token) => await store.AgentAsync(id, token) is { } agent ? Results.Json(agent) : Results.NotFound());
        management.MapPost("/invitations", async (InviteRequest request, HttpContext context, IAntiforgery anti, ServerStore store, ServerLog log) =>
        {
            await anti.ValidateRequestAsync(context);
            var invitation = await store.InviteAsync(ServerAuthentication.UserId(context.User), request, context.RequestAborted);
            log.Write(ServerEvent.InvitationCreated); return Results.Json(invitation, statusCode: 201);
        });
        management.MapPost("/agents/{id:guid}/revoke", async (Guid id, HttpContext context, IAntiforgery anti, ServerStore store, ServerLog log) =>
        {
            await anti.ValidateRequestAsync(context); await store.RevokeAsync(ServerAuthentication.UserId(context.User), id, context.RequestAborted);
            log.Write(ServerEvent.AgentRevoked, agentId: id); return Results.NoContent();
        });
    }
}
