using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using SPBackup.Core;

/// <summary>Fresh SCRAM cluster; the service cannot read the owner's private fixture password file.</summary>
internal sealed class PostgreSqlFixture : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "OneCBackup-service-pg-" + Guid.NewGuid().ToString("N"));
    private readonly string bin = Environment.GetEnvironmentVariable("PGBIN") ?? throw new InvalidOperationException("PGBIN is required for service authentication verification.");
    private readonly string user = "ocbpg_" + Guid.NewGuid().ToString("N")[..12];
    public string Password { get; } = "ServicePG:\\" + Guid.NewGuid().ToString("N");
    private int port;
    private bool initialized;
    private BackupHold? backupHold;
    private string Tool(string name)
    {
        var path = Path.Combine(bin, name + ".exe");
        if (!File.Exists(path)) throw new FileNotFoundException("Required PostgreSQL fixture tool is missing: " + name);
        return path;
    }
    private Dictionary<string, string?> Authentication => new()
    {
        ["PGPASSFILE"] = Path.Combine(root, "pgpass.conf"), ["PGPASSWORD"] = null, ["PGSERVICE"] = null,
        ["PGSERVICEFILE"] = null, ["PGHOSTADDR"] = null, ["PGSSLMODE"] = "verify-full", ["PGSSLROOTCERT"] = RootCertificatePath, ["PGCLIENTENCODING"] = "UTF8"
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
    private Task<string> Sql(string database, string sql) => Run("psql", ["--no-psqlrc", "--no-password", "--host=127.0.0.1", "--port=" + port,
        "--username=" + user, "--dbname=" + database, "--set=ON_ERROR_STOP=1", "--tuples-only", "--no-align", "--command=" + sql]);
    public async Task<IAsyncDisposable> HoldBackupAsync()
    {
        if (backupHold is not null) throw new InvalidOperationException("A PostgreSQL backup barrier is already active.");
        var application = "ocb_backup_barrier_" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Tool("psql")) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--no-psqlrc", "--no-password", "--host=127.0.0.1", "--port=" + port,
            "--username=" + user, "--dbname=service_source", "--set=ON_ERROR_STOP=1", "--quiet" }) start.ArgumentList.Add(arg);
        foreach (var pair in Authentication)
            if (pair.Value is null) start.Environment.Remove(pair.Key); else start.Environment[pair.Key] = pair.Value;
        start.Environment["PGAPPNAME"] = application;
        start.Environment["PGCONNECT_TIMEOUT"] = "10";
        var process = Process.Start(start) ?? throw new IOException("PostgreSQL backup barrier did not start.");
        var hold = new BackupHold(process, application, Password);
        backupHold = hold;
        try
        {
            await process.StandardInput.WriteLineAsync("BEGIN; LOCK TABLE fixture IN ACCESS EXCLUSIVE MODE;")
                .WaitAsync(TimeSpan.FromSeconds(10));
            await process.StandardInput.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (await Sql("service_source", "SELECT EXISTS (SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid "
                + $"WHERE a.application_name='{application}' AND l.relation='fixture'::regclass "
                + "AND l.mode='AccessExclusiveLock' AND l.granted);") != "t")
            {
                if (process.HasExited) throw new IOException("PostgreSQL backup barrier exited: " + await hold.ErrorAsync());
                if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("PostgreSQL backup barrier did not acquire its table lock.");
                await Task.Delay(100);
            }
            return hold;
        }
        catch
        {
            await hold.DisposeAsync();
            backupHold = null;
            throw;
        }
    }
    public async Task<int> WaitForBlockedDumpAsync()
    {
        var hold = backupHold ?? throw new InvalidOperationException("The PostgreSQL backup barrier is not active.");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (true)
        {
            var result = await Sql("service_source", "SELECT a.pid FROM pg_stat_activity a WHERE a.datname='service_source' "
                + $"AND a.usename='{user}' AND a.application_name='pg_dump' AND a.wait_event_type='Lock' "
                + "AND EXISTS (SELECT 1 FROM pg_stat_activity blocker "
                + $"WHERE blocker.application_name='{hold.Application}' AND blocker.pid=ANY(pg_blocking_pids(a.pid))) LIMIT 1;");
            if (int.TryParse(result, out var pid) && pid > 0) return pid;
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The actual service pg_dump did not wait on the fixture table lock.");
            await Task.Delay(100);
        }
    }
    public async Task<bool> IsDumpBackendPresentAsync(int pid) => await Sql("service_source",
        $"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid={pid} AND datname='service_source' "
        + $"AND usename='{user}' AND application_name='pg_dump');") == "t";

    private sealed class BackupHold(Process process, string application, string password) : IAsyncDisposable
    {
        private readonly Task<string> output = process.StandardOutput.ReadToEndAsync();
        private readonly Task<string> errors = process.StandardError.ReadToEndAsync();
        private bool disposed;
        public string Application => application;
        public async Task<string> ErrorAsync() => (await errors).Replace(password, "[redacted]");
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        await process.StandardInput.WriteLineAsync("ROLLBACK;\n\\q").WaitAsync(TimeSpan.FromSeconds(5));
                        await process.StandardInput.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        process.StandardInput.Close();
                    }
                    catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException) { }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (TimeoutException)
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
                await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { process.Dispose(); }
        }
    }
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
        await PostgreSqlTlsFixture.CreateAsync(root, Path.Combine(root, "cluster"));
        await File.AppendAllTextAsync(Path.Combine(root, "cluster", "postgresql.conf"), "\nssl=on\nssl_cert_file='server.crt'\nssl_key_file='server.key'\n");
        await File.WriteAllTextAsync(Path.Combine(root, "cluster", "pg_hba.conf"), "hostnossl all all 127.0.0.1/32 reject\nhostssl all all 127.0.0.1/32 scram-sha-256\n");
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
    public string RootCertificatePath => Path.Combine(root, "ca.pem");
    public string Username => user;
    public string DumpExecutablePath => Tool("pg_dump");
    public async Task AssertRestoresAsync(string dump)
    {
        var restored = "service_restored_" + Guid.NewGuid().ToString("N");
        await Sql("postgres", "CREATE DATABASE " + restored + ";");
        await Run("pg_restore", ["--no-password", "--exit-on-error", "--host=127.0.0.1", "--port=" + port,
            "--username=" + user, "--dbname=" + restored, dump]);
        if (await Sql(restored, "SELECT value FROM fixture WHERE id=1;") != "from service")
            throw new InvalidDataException("The service PostgreSQL backup did not restore expected data.");
    }
    public async ValueTask DisposeAsync()
    {
        if (backupHold is not null) await backupHold.DisposeAsync();
        if (initialized && File.Exists(Path.Combine(root, "cluster", "postmaster.pid")))
            await Run("pg_ctl", ["stop", "-D", Path.Combine(root, "cluster"), "-w", "-t", "60", "-m", "fast"], capture: false);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
