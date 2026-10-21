using System.Runtime.Versioning;
using System.Security.Principal;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

[CollectionDefinition("SQL service credential export", DisableParallelization = true)]
public sealed class SqlServiceCredentialCollection { }

[Collection("SQL service credential export")]
public sealed class WindowsSqlCredentialTests
{
    [WindowsCredentialTheory]
    [SupportedOSPlatform("windows")]
    [Trait("Category", "WindowsIntegration")]
    [InlineData("test SQL password 🗝")]
    public async Task SqlVaultBindingsExportOnlyReferencedJobsAndRefreshServiceCache(string password)
    {
        var root = Path.Combine(Path.GetTempPath(), "OneCBackup-sql-service-" + Guid.NewGuid());
        var first = Guid.NewGuid().ToString("N"); var other = Guid.NewGuid().ToString("N");
        var store = new WindowsSqlServerCredentialStore();
        try
        {
            store.Save(first, "sql-backup-user", password);
            store.Save(other, "unused-user", "unused-password");
            Assert.Equal(password, (await store.GetAsync(first)).Password);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new WindowsFtpCredentialStore().GetAsync(first));
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            var cache = new WindowsServiceCredentialCache(root, sid);
            WindowsFtpCredentialStore.SetServiceCache(cache);
            var job = new BackupJob { Kind = DatabaseKind.SqlServer,
                SqlServer = new() { Authentication = SqlServerAuthentication.SqlServer, CredentialKey = first } };
            await WindowsFtpCredentialStore.ExportProfileForServiceAsync([job], []);
            Assert.Equal(password, cache.Read("SqlServer", first)!.Password);
            Assert.Null(cache.Read("SqlServer", other));
            Assert.Null(cache.Read("FTP", first));
            var bytes = await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(root, "service-secrets"))));
            Assert.DoesNotContain(password, System.Text.Encoding.UTF8.GetString(bytes));
            store.Save(first, "updated-user", "changed-password");
            Assert.Equal("updated-user", cache.Read("SqlServer", first)!.UserName);
            Assert.Equal("changed-password", cache.Read("SqlServer", first)!.Password);
            Assert.Throws<ArgumentException>(() => store.Save(other, new string('u', 129), "password"));
            Assert.Throws<ArgumentException>(() => store.Save(other, "user", new string('p', 129)));
            Assert.True(store.Delete(first)); Assert.Null(cache.Read("SqlServer", first));
        }
        finally
        {
            WindowsFtpCredentialStore.SetServiceCache(null);
            store.Delete(first); store.Delete(other);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
