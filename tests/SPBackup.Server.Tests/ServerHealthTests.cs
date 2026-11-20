using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SPBackup.Server;
using SPBackup.Server.Components;
using Xunit;

namespace SPBackup.Server.Tests;

public sealed class ServerHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, DataDiskState.Unknown)]
    [InlineData(-1L, DataDiskState.Unknown)]
    [InlineData(0L, DataDiskState.Critical)]
    [InlineData(ServerHealth.CriticalDiskBytes - 1, DataDiskState.Critical)]
    [InlineData(ServerHealth.CriticalDiskBytes, DataDiskState.Low)]
    [InlineData(ServerHealth.LowDiskBytes - 1, DataDiskState.Low)]
    [InlineData(ServerHealth.LowDiskBytes, DataDiskState.Healthy)]
    public void DiskThresholdsIncludeEmptyAndUnknownVolumes(long? free, DataDiskState expected)
    {
        var result = ServerHealth.Evaluate(Now, free, 10 * ServerHealth.LowDiskBytes, null, null);
        Assert.Equal(expected, result.DiskState);
        Assert.Equal(expected != DataDiskState.Healthy, result.HasWarnings);
        Assert.Equal(ServerCertificateState.External, result.CertificateState);
    }

    [Theory]
    [InlineData(-1, -60, ServerCertificateState.Expired)]
    [InlineData(0, -60, ServerCertificateState.Expired)]
    [InlineData(30, -60, ServerCertificateState.Expiring)]
    [InlineData(31, -60, ServerCertificateState.Valid)]
    [InlineData(31, 1, ServerCertificateState.NotYetValid)]
    public void CertificateValidityHasExactBoundaries(int remainingDays, int startsInDays, ServerCertificateState expected)
    {
        var result = ServerHealth.Evaluate(Now, ServerHealth.LowDiskBytes, null, Now.AddDays(startsInDays), Now.AddDays(remainingDays));
        Assert.Equal(expected, result.CertificateState);
        Assert.Equal(expected != ServerCertificateState.Valid, result.HasWarnings);
    }

    [Fact]
    public void DataDirectoryOnUnixMountUsesThatVolumeInsteadOfRoot()
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists("/dev/shm")) return;
        using var services = new ServiceCollection().BuildServiceProvider();
        var snapshot = new ServerHealth(new() { DataDirectory = "/dev/shm" }, services).Read();
        Assert.Equal(new DriveInfo("/dev/shm").TotalSize, snapshot.TotalDataDiskBytes);
        Assert.NotNull(snapshot.FreeDataDiskBytes);
    }
}

public sealed class ActiveServerCertificateTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    [ServerTheory]
    [InlineData(DataDiskState.Critical, ServerCertificateState.Valid, "Критически мало места", "файлы базы и ключи сервера не удаляйте")]
    [InlineData(DataDiskState.Low, ServerCertificateState.Valid, "Мало места", "увеличьте диск")]
    [InlineData(DataDiskState.Unknown, ServerCertificateState.External, "Не удалось проверить", "доступ службы")]
    [InlineData(DataDiskState.Healthy, ServerCertificateState.Expired, "Срок HTTPS-сертификата истёк", "перезапустите службу")]
    [InlineData(DataDiskState.Healthy, ServerCertificateState.NotYetValid, "ещё не действует", "дату и время")]
    public async Task NoticesExplainProblemAndNextAction(DataDiskState disk, ServerCertificateState certificate, string problem, string action)
    {
        using var scope = fixture.App.Services.CreateScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());
        var snapshot = new ServerHealthSnapshot(DateTimeOffset.UtcNow, 100, 1000, disk,
            DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(2), certificate);
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = await renderer.RenderComponentAsync<ServerHealthNotices>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Snapshot"] = snapshot, ["Compact"] = true }));
            return WebUtility.HtmlDecode(result.ToHtmlString());
        });
        Assert.Contains(problem, html); Assert.Contains(action, html);
        Assert.Contains("href=\"system\"", html);
    }

    [ServerFact]
    public async Task PanelAndDiagnosticsUseLoadedCertificateWhenPfxIsReplacedOrMissing()
    {
        var path = Path.Combine(fixture.Root, "certificate.pfx");
        var original = await File.ReadAllBytesAsync(path);
        var expected = fixture.App.Services.GetRequiredService<ServerHealth>().Read();
        Assert.Equal(ServerCertificateState.Expiring, expected.CertificateState);
        try
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var replacement = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
            await File.WriteAllBytesAsync(path, replacement.Export(X509ContentType.Pfx, "unloaded-fixture-password"));
            await CheckAsync();
            File.Delete(path);
            await CheckAsync();
        }
        finally { await File.WriteAllBytesAsync(path, original); }

        async Task CheckAsync()
        {
            foreach (var page in new[] { "", "system" })
            {
                using var response = await fixture.Administrator.GetAsync(page);
                response.EnsureSuccessStatusCode();
                var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
                Assert.Contains("HTTPS-сертификат скоро истекает", html);
                Assert.DoesNotContain("unloaded-fixture-password", html);
                if (page == "system") Assert.Contains("Nginx Proxy Manager", html);
            }
            using var responseZip = await fixture.Administrator.PostAsync("api/v1/management/diagnostics", null);
            responseZip.EnsureSuccessStatusCode();
            using var zip = new ZipArchive(new MemoryStream(await responseZip.Content.ReadAsByteArrayAsync()));
            using var summary = JsonDocument.Parse(zip.GetEntry("server.json")!.Open());
            Assert.Equal(expected.CertificateNotAfterUtc, summary.RootElement.GetProperty("CertificateNotAfterUtc").GetDateTimeOffset());
            Assert.Equal("Expiring", summary.RootElement.GetProperty("CertificateState").GetString());
            Assert.False(summary.RootElement.GetProperty("CertificateManagedExternally").GetBoolean());
        }
    }
}
