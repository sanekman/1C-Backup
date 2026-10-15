using OneCBackup.Remote;

try
{
    if (args.Length == 3 && args[0] == "initialize") { RemoteConfiguration.Initialize(args[1], args[2]); Console.WriteLine("Pairing initialized. Keep configuration files private."); return 0; }
    if (args.Length == 2 && args[0] == "hub")
    {
        var options = await RemoteConfiguration.ReadAsync<HubOptions>(args[1]);
        if (!options.Enabled) throw new InvalidDataException("Hub is disabled");
        await using var hub = RemoteHub.Build(options); await hub.RunAsync(); return 0;
    }
    if (args.Length == 2 && args[0] == "gateway")
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; shutdown.Cancel(); });
        try { await new StorageGateway(await RemoteConfiguration.ReadAsync<GatewayOptions>(args[1])).RunAsync(shutdown.Token); }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        return 0;
    }
    if (args.Length >= 2 && args[0] is "probe" or "put" or "get" or "stat" or "delete")
    {
        var client = new RemoteStorageClient(await RemoteConfiguration.ReadAsync<ClientPairing>(args[1]));
        if (args[0] == "probe" && args.Length == 2) { await client.ProbeAsync(); Console.WriteLine("gateway_ready"); return 0; }
        if (args[0] == "put" && args.Length == 5)
        { var result = await client.PutAsync(Guid.Parse(args[2]), Guid.Parse(args[3]), args[4]); Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result)); return 0; }
        if (args[0] == "stat" && args.Length == 4)
        { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(await client.StatAsync(Guid.Parse(args[2]), Guid.Parse(args[3])))); return 0; }
        if (args[0] == "get" && args.Length == 7)
        { await using var output = new FileStream(args[6], FileMode.CreateNew, FileAccess.Write, FileShare.None); await client.GetAsync(Guid.Parse(args[2]), Guid.Parse(args[3]), long.Parse(args[4]), args[5], output); Console.WriteLine("object_downloaded"); return 0; }
        if (args[0] == "delete" && args.Length == 6)
        { await client.DeleteAsync(Guid.Parse(args[2]), Guid.Parse(args[3]), long.Parse(args[4]), args[5]); Console.WriteLine("object_deleted"); return 0; }
        throw new ArgumentException("Invalid client command arguments");
    }
    Console.Error.WriteLine("Commands: initialize DIRECTORY wss://DOMAIN | gateway CONFIG | hub CONFIG"); return 2;
}
catch (Exception error) { Console.Error.WriteLine("bridge_failed:" + RemoteConfiguration.ErrorCode(error)); return 1; }
