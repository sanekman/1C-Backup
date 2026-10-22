using System.Runtime.Versioning;
using System.Security.Principal;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

[Collection("SQL service credential export")]
public sealed class WindowsPostgreSqlCredentialTests
{
    [WindowsCredentialTheory]
    [SupportedOSPlatform("windows")]
    [Trait("Category", "WindowsIntegration")]
    [InlineData("PostgreSQL : \\ пароль 🗝")]
    public async Task VaultHasSeparateNamespaceAndExportsOnlyReferencedPostgreSqlJobs(string password)
    {
        var root = Path.Combine(Path.GetTempPath(), "OneCBackup-pg-service-" + Guid.NewGuid());
        var first = Guid.NewGuid().ToString("N"); var unused = Guid.NewGuid().ToString("N");
        var store = new WindowsPostgreSqlCredentialStore();
        try
        {
            store.Save(first, "pg-backup-user", password); store.Save(unused, "unused-user", "unused-password");
            Assert.Equal(password, (await store.GetAsync(first)).Password);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new WindowsFtpCredentialStore().GetAsync(first));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new WindowsSqlServerCredentialStore().GetAsync(first));
            var cache = new WindowsServiceCredentialCache(root, WindowsIdentity.GetCurrent().User!.Value);
            WindowsFtpCredentialStore.SetServiceCache(cache);
            store.Save(unused, "unused-user", "discarded-draft-password");
            Assert.Null(cache.Read("PostgreSQL", unused));
            await WindowsFtpCredentialStore.ExportProfileForServiceAsync([new() { Kind = DatabaseKind.PostgreSql,
                PostgreSql = new() { CredentialKey = first } }, new() { Kind = DatabaseKind.SqlServer,
                PostgreSql = new() { CredentialKey = unused } }], []);
            Assert.Equal(password, cache.Read("PostgreSQL", first)!.Password);
            Assert.Null(cache.Read("PostgreSQL", unused)); Assert.Null(cache.Read("FTP", first)); Assert.Null(cache.Read("SqlServer", first));
            var bytes = await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(root, "service-secrets"))));
            Assert.DoesNotContain(password, System.Text.Encoding.UTF8.GetString(bytes));
            var longerPassword = new string('p', 300); // PostgreSQL is not restricted to SQL Server's 128 characters.
            store.Save(first, "pg-backup-user", longerPassword);
            Assert.Equal(longerPassword, cache.Read("PostgreSQL", first)!.Password);
            Assert.Throws<ArgumentException>(() => store.Save(unused, "pg-user", "bad\npassword"));
            Assert.True(store.Delete(first)); Assert.Null(cache.Read("PostgreSQL", first));
        }
        finally
        {
            WindowsFtpCredentialStore.SetServiceCache(null); store.Delete(first); store.Delete(unused);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
