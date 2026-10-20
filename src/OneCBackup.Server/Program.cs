using OneCBackup.Server;

Console.OutputEncoding = System.Text.Encoding.UTF8;

WebApplication? app = null;
try
{
    var initialize = args.Contains("--initialize"); var migrate = args.Contains("--migrate"); var provision = args.Contains("--provision");
    app = ServerHost.Build(args.Where(arg => arg is not "--initialize" and not "--migrate" and not "--provision").ToArray());
    var store = app.Services.GetRequiredService<ServerStore>();
    if (initialize || migrate || provision)
    {
        await store.MigrateAsync();
        if (initialize || provision && !await store.ReadyAsync())
        {
            var settings = app.Services.GetRequiredService<ServerOptions>();
            if (settings.BootstrapUser is null || settings.BootstrapPasswordFile is null) throw new ServerRequestException(400, "bootstrap_configuration_required");
            await store.BootstrapAsync(settings.BootstrapUser, ServerHost.ReadSecret(settings.BootstrapPasswordFile));
        }
        Console.WriteLine(initialize ? "Сервер и администратор инициализированы." : "Схема сервера обновлена.");
        return 0;
    }
    if (!await store.ReadyAsync()) throw new ServerRequestException(503, "initialize_database_first");
    var log = app.Services.GetRequiredService<ServerLog>();
    app.Lifetime.ApplicationStarted.Register(() => log.Write(ServerEvent.Started));
    app.Lifetime.ApplicationStopped.Register(() => log.Write(ServerEvent.Stopped));
    await app.RunAsync(); return 0;
}
catch (Exception ex)
{
    app?.Services.GetService<ServerLog>()?.Write(ServerEvent.StartupFailed, ex);
    Console.Error.WriteLine("Сервер не запущен. Код: " + (ex is ServerRequestException known ? known.Code : ex.GetType().Name));
    Console.Error.WriteLine(string.Join("\n", ServerLog.Frames(ex)));
    return 1;
}
finally { if (app is not null) await app.DisposeAsync(); }
