using System.Runtime.Versioning;
using System.Security.Principal;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

[Collection("SQL service credential export")]
public sealed class WindowsOneCCredentialTests
{
    [WindowsCredentialTheory]
    [SupportedOSPlatform("windows")]
    [Trait("Category","WindowsIntegration")]
    [InlineData("owned OneC : \\ пароль")]
    public async Task VaultNamespaceAndExportAreLimitedToReferencedPlatformDumpJobs(string password)
    {
        var root=Path.Combine(Path.GetTempPath(),"sp-onec-vault-"+Guid.NewGuid().ToString("N"));
        var key=Guid.NewGuid().ToString("N");var unused=Guid.NewGuid().ToString("N");var store=new WindowsOneCCredentialStore();
        try
        {
            WindowsFtpCredentialStore.SetServiceCache(null);
            store.Save(key,"ib-user",password);store.Save(unused,"unused","owned-unused-password");
            Assert.Equal(password,(await store.GetAsync(key)).Password);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>new WindowsFtpCredentialStore().GetAsync(key));
            var cache=new WindowsServiceCredentialCache(root,WindowsIdentity.GetCurrent().User!.Value);WindowsFtpCredentialStore.SetServiceCache(cache);
            var job=new BackupJob { Name="DT credential",Source=root,DestinationDirectory=Path.Combine(root,"copies"),
                OneC=new() { ExecutablePath=Path.Combine(root,"1cv8.exe"),Username="ib-user",CredentialKey=key } };
            await WindowsFtpCredentialStore.ExportProfileForServiceAsync([job],[]);
            Assert.Equal(password,cache.Read("OneCInfobase",key)!.Password);
            Assert.Null(cache.Read("OneCInfobase",unused));Assert.Null(cache.Read("FTP",key));
            Assert.All(Directory.GetFiles(Path.Combine(root,"service-secrets")),file=>Assert.DoesNotContain(password,System.Text.Encoding.Unicode.GetString(File.ReadAllBytes(file))));
        }
        finally { WindowsFtpCredentialStore.SetServiceCache(null);store.Delete(key);store.Delete(unused);if(Directory.Exists(root))Directory.Delete(root,true); }
    }
}
