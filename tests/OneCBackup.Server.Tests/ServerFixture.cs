using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OneCBackup.Server;
using Xunit;

namespace OneCBackup.Server.Tests;

public sealed class ServerFactAttribute : FactAttribute
{
    public ServerFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ONECBACKUP_SERVER_TEST_CONNECTION_FILE")))
            Skip = "Set ONECBACKUP_SERVER_TEST_CONNECTION_FILE for isolated real PostgreSQL + HTTPS tests.";
    }
}

public sealed class ServerFixture : IAsyncLifetime
{
    public readonly string Root = Path.Combine(Path.GetTempPath(), "onec-server-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string schema = "server_test_" + Guid.NewGuid().ToString("N");
    public readonly string Password = ServerTokens.Create();
    public readonly List<string> Secrets = [];
    private string adminCsrf = "";
    private string connection = "";
    private X509Certificate2 certificate = null!;
    public WebApplication App { get; private set; } = null!;
    public HttpClient Administrator { get; private set; } = null!;
    public ServerStore Store => App.Services.GetRequiredService<ServerStore>();
    public Uri Address => new(App.Urls.Single().TrimEnd('/') + "/");
    private readonly CookieContainer cookies = new();
    public async Task InitializeAsync()
    {
        var sourceFile = Environment.GetEnvironmentVariable("ONECBACKUP_SERVER_TEST_CONNECTION_FILE");
        if (string.IsNullOrEmpty(sourceFile)) return;
        Directory.CreateDirectory(Root);
        var original = File.ReadAllText(sourceFile);
        await using (var database = new NpgsqlConnection(original))
        {
            await database.OpenAsync(); await using var create = new NpgsqlCommand("CREATE SCHEMA " + schema, database); await create.ExecuteNonQueryAsync();
        }
        var parsed = new NpgsqlConnectionStringBuilder(original) { SearchPath = schema };
        connection = parsed.ConnectionString;
        File.WriteAllText(Path.Combine(Root, "connection.txt"), connection);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        var password = ServerTokens.Create(); Secrets.Add(password); Secrets.Add(Password);
        File.WriteAllText(Path.Combine(Root, "certificate-password.txt"), password);
        File.WriteAllBytes(Path.Combine(Root, "certificate.pfx"), generated.Export(X509ContentType.Pfx, password));
        certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(Root, "certificate.pfx"), password);
        App = Build();
        await Store.MigrateAsync(); await Store.BootstrapAsync("administrator", Password);
        await App.StartAsync();
        Administrator = new HttpClient(Handler(cookies)) { BaseAddress = Address };
        await SignInAsync(Administrator);
        using var session = await Administrator.GetAsync("api/v1/management/session"); session.EnsureSuccessStatusCode();
        using var info = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        adminCsrf = info.RootElement.GetProperty("csrfToken").GetString()!;
        Administrator.DefaultRequestHeaders.Add("X-CSRF-TOKEN", adminCsrf);
    }
    public WebApplication Build(string? prefix = null, bool plainHttp = false, bool automaticForwarding = false, bool trustedProxy = false)
    {
        var repository = Environment.GetEnvironmentVariable("ONECBACKUP_TEST_REPOSITORY") ?? FindRepository();
        List<string> args = ["--Server:ConnectionStringFile", Path.Combine(Root, "connection.txt"), "--Server:DataDirectory", Path.Combine(Root, "data"),
            "--Server:HttpsCertificatePath", Path.Combine(Root, "certificate.pfx"), "--Server:HttpsCertificatePasswordFile", Path.Combine(Root, "certificate-password.txt"),
            "--contentRoot", Path.Combine(repository, "src", "OneCBackup.Server"), "--urls", (plainHttp ? "http" : "https") + "://127.0.0.1:0"];
        if (prefix is not null) args.AddRange(["--Server:PathBase", prefix]);
        if (automaticForwarding) args.AddRange(["--ForwardedHeaders_Enabled", "true"]);
        if (trustedProxy) args.AddRange(["--Server:TrustedProxies:0", "127.0.0.1"]);
        return ServerHost.Build(args.ToArray());
    }
    private static string FindRepository()
    {
        var location = new DirectoryInfo(AppContext.BaseDirectory);
        while (location is not null && !File.Exists(Path.Combine(location.FullName, "OneCBackup.slnx"))) location = location.Parent;
        return location?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
    }
    public HttpClientHandler Handler(CookieContainer? jar = null) => new()
    {
        AllowAutoRedirect = false, CookieContainer = jar ?? new(),
        ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
        {
            if (cert is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
            using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(certificate); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(cert);
        }
    };
    public HttpClient Client() => new(Handler()) { BaseAddress = Address };
    public async Task SignInAsync(HttpClient client, string basePath = "")
    {
        using var page = await client.GetAsync(basePath + "login"); page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync();
        var token = AntiToken(html);
        using var response = await client.PostAsync(basePath + "session/login", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = token, ["name"] = "administrator", ["password"] = Password }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("failed", response.Headers.Location!.OriginalString);
    }
    public static string AntiToken(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    public async Task<Invitation> InviteAsync(Guid? id = null)
    {
        using var response = await Administrator.PostAsync("api/v1/management/invitations", Json(new InviteRequest("Test invitation", id)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var invite = JsonSerializer.Deserialize<Invitation>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Secrets.Add(invite.Code); return invite;
    }
    public async Task<EnrollmentResponse> EnrollAsync(Guid? id = null)
    {
        var agentId = id ?? Guid.NewGuid(); var invitation = await InviteAsync(agentId);
        using var client = Client(); using var response = await client.PostAsync("api/v1/enrollment", Json(new EnrollmentRequest(invitation.Code, agentId, "Test agent")));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = JsonSerializer.Deserialize<EnrollmentResponse>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Secrets.Add(result.Token); return result;
    }
    public static StringContent Json(object value) => new(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), System.Text.Encoding.UTF8, "application/json");
    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] values)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        foreach (var item in values) cmd.Parameters.AddWithValue(item.Name, item.Value);
        await cmd.ExecuteNonQueryAsync();
    }
    public async Task RestartAsync()
    {
        await App.StopAsync(); await App.DisposeAsync(); App = Build(); await App.StartAsync();
        Administrator.Dispose(); Administrator = new HttpClient(Handler(cookies)) { BaseAddress = Address };
        Administrator.DefaultRequestHeaders.Add("X-CSRF-TOKEN", adminCsrf);
    }
    public async Task DisposeAsync()
    {
        Administrator?.Dispose();
        if (App is not null) { await App.StopAsync(); await App.DisposeAsync(); }
        certificate?.Dispose();
        if (!string.IsNullOrEmpty(connection))
        {
            var original = new NpgsqlConnectionStringBuilder(connection) { SearchPath = "public" };
            await using var db = new NpgsqlConnection(original.ConnectionString); await db.OpenAsync();
            await using var drop = new NpgsqlCommand("DROP SCHEMA " + schema + " CASCADE", db); await drop.ExecuteNonQueryAsync();
        }
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}
