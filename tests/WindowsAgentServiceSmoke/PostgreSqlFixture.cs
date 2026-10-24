using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using OneCBackup.Core;

/// <summary>Fresh SCRAM cluster; the service cannot read the owner's private fixture password file.</summary>
internal sealed class PostgreSqlFixture : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-service-pg-" + Guid.NewGuid().ToString("N"));
    private readonly string bin = Environment.GetEnvironmentVariable("PGBIN") ?? throw new InvalidOperationException("PGBIN is required for service authentication verification.");
    private readonly string user = "ocbpg_" + Guid.NewGuid().ToString("N")[..12];
    public string Password { get; } = "ServicePG:\\" + Guid.NewGuid().ToString("N");
    private int port;
    private bool initialized;
    private string Tool(string name)
    {
        var path = Path.Combine(bin, name + ".exe");
        if (!File.Exists(path)) throw new FileNotFoundException("Required PostgreSQL fixture tool is missing: " + name);
        return path;
    }
    private Dictionary<string, string?> Authentication => new()
    {
        ["PGPASSFILE"] = Path.Combine(root, "pgpass.conf"), ["PGPASSWORD"] = null, ["PGSERVICE"] = null,
        ["PGSERVICEFILE"] = null, ["PGHOSTADDR"] = null, ["PGCLIENTENCODING"] = "UTF8"
    };
    private async Task<string> Run(string name, IEnumerable<string> args, bool capture = true)
    {
        var start = new ProcessStartInfo(Tool(name)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = capture, RedirectStandardError = capture };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var pair in Authentication)
            if (pair.Value is null) start.Environment.Remove(pair.Key); else start.Environment[pair.Key] = pair.Value;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var process = Process.Start(start) ?? throw new IOException("PostgreSQL fixture did not start.");
        var output = capture ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        var errors = capture ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } throw; }
        var text = await output; var error = await errors;
        if (process.ExitCode != 0) throw new IOException(name + " fixture failed: " + error.Replace(Password, "[redacted]"));
        return text.Trim();
    }
    private Task<string> Sql(string database, string sql) => Run("psql", ["--no-password", "--host=127.0.0.1", "--port=" + port,
        "--username=" + user, "--dbname=" + database, "--set=ON_ERROR_STOP=1", "--tuples-only", "--no-align", "--command=" + sql]);
    public async Task StartAsync()
    {
        var sid = WindowsIdentity.GetCurrent().User!;
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false); acl.SetOwner(sid);
        foreach (var account in new[] { sid, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).Create(acl);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var passwordFile = Path.Combine(root, "init-password"); await File.WriteAllTextAsync(passwordFile, Password);
        await File.WriteAllTextAsync(Path.Combine(root, "pgpass.conf"), $"127.0.0.1:{port}:*:{user}:"
            + Password.Replace("\\", "\\\\").Replace(":", "\\:") + "\n");
        await Run("initdb", ["--pgdata=" + Path.Combine(root, "cluster"), "--username=" + user, "--auth=scram-sha-256",
            "--pwfile=" + passwordFile, "--encoding=UTF8", "--locale=C"]);
        initialized = true; File.Delete(passwordFile);
        await Run("pg_ctl", ["start", "-D", Path.Combine(root, "cluster"), "-w", "-t", "60", "-l", Path.Combine(root, "postgres.log"),
            "-o", $"-h 127.0.0.1 -p {port}"], capture: false);
        await Sql("postgres", "CREATE DATABASE service_source;");
        await Sql("service_source", "CREATE TABLE fixture (id integer PRIMARY KEY, value text); INSERT INTO fixture VALUES (1,'from service');");
    }
    public BackupJob Job(string destination, string key, string archiveKey) => new()
    {
        Name = "PostgreSQL service password", Kind = DatabaseKind.PostgreSql, Source = "service_source", Enabled = false,
        ScheduleKind = ScheduleKind.Manual, DestinationDirectory = destination, ArchiveCopies = true, ArchivePasswordKey = archiveKey,
        PostgreSql = new() { Host = "127.0.0.1", Port = port, Username = user, CredentialKey = key,
            PgDumpPath = Tool("pg_dump"), PgRestorePath = Tool("pg_restore"), TimeoutMinutes = 2 }
    };
    public string Username => user;
    public async Task AssertRestoresAsync(string dump)
    {
        await Sql("postgres", "CREATE DATABASE service_restored;");
        await Run("pg_restore", ["--no-password", "--exit-on-error", "--host=127.0.0.1", "--port=" + port,
            "--username=" + user, "--dbname=service_restored", dump]);
        if (await Sql("service_restored", "SELECT value FROM fixture WHERE id=1;") != "from service")
            throw new InvalidDataException("The service PostgreSQL backup did not restore expected data.");
    }
    public async ValueTask DisposeAsync()
    {
        if (initialized && File.Exists(Path.Combine(root, "cluster", "postmaster.pid")))
            await Run("pg_ctl", ["stop", "-D", Path.Combine(root, "cluster"), "-w", "-t", "60", "-m", "fast"], capture: false);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
