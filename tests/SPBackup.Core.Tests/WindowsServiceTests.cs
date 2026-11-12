using System.Security.AccessControl;
using System.Buffers.Binary;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public class WindowsServiceTests
{
    [Theory]
    [InlineData("backup", "PC\\backup")]
    [InlineData(".\\backup", "PC\\backup")]
    [InlineData("  DOMAIN\\backup  ", "DOMAIN\\backup")]
    [InlineData("backup@example.org", "backup@example.org")]
    public void AccountNamesSupportLocalDomainAndUpn(string input, string expected) =>
        Assert.Equal(expected, ServiceAccountName.Normalize(input, "PC"));

    [Theory]
    [InlineData("")]
    [InlineData("domain\\")]
    [InlineData("a\\b\\c")]
    [InlineData("user\n")]
    [InlineData("user\" /run")]
    public void InvalidAccountNamesAreRejected(string input) =>
        Assert.Throws<ArgumentException>(() => ServiceAccountName.Normalize(input, "PC"));

    [Fact]
    public async Task SelectedServiceSidRoundTripsSeparatelyFromOwner()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new WindowsServiceProfile("S-1-5-21-11-22-33-1001", Path.GetFullPath("user"), Path.GetFullPath("roaming"), Path.GetFullPath("local"))
                { ServiceSid = "S-1-5-21-11-22-33-1002" };
            await profile.SaveAsync(root);
            var loaded = await WindowsServiceProfile.LoadAsync(root);
            Assert.Equal(profile, loaded);
            Assert.NotEqual(loaded!.OwnerSid, loaded.EffectiveServiceSid);
            profile = profile with { ServiceSid = "S-1-5-18" };
            Assert.Throws<InvalidDataException>(() => profile.Validate());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [WindowsCredentialTheory]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [Trait("Category", "WindowsIntegration")]
    [InlineData("current-account")]
    public void CurrentAccountResolvesAndGroupsAreRejected(string fixture)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var account = WindowsServiceAccounts.Resolve(identity.Name);
        Assert.Equal(identity.User!.Value, account.Sid);
        Assert.All(WindowsServiceAccounts.LocalUsers(), name => Assert.StartsWith(Environment.MachineName + "\\", name));
        var group = ((System.Security.Principal.NTAccount)new System.Security.Principal.SecurityIdentifier(
            System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null).Translate(typeof(System.Security.Principal.NTAccount))).Value;
        Assert.Throws<ArgumentException>(() => WindowsServiceAccounts.Resolve(group));
    }

    [WindowsCredentialTheory]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [Trait("Category", "WindowsIntegration")]
    [InlineData("пароль службы 🗝")]
    public void ServiceCacheEncryptsAndRestrictsAccess(string password)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            var cache = new WindowsServiceCredentialCache(root, sid);
            cache.Save("Archive", "fixture-key", new System.Net.NetworkCredential("user", password));
            Assert.Equal(password, cache.Read("Archive", "fixture-key")!.Password);
            Assert.Null(cache.Read("FTP", "fixture-key"));
            var encrypted = File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.Combine(root, "service-secrets"))));
            Assert.DoesNotContain(password, System.Text.Encoding.UTF8.GetString(encrypted));
            var acl = new DirectoryInfo(Path.Combine(root, "service-secrets")).GetAccessControl();
            Assert.True(acl.AreAccessRulesProtected);
            Assert.Equal(sid, acl.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))!.Value);
            Assert.Equal(3, acl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Count);
            cache.Delete("Archive", "fixture-key"); Assert.Null(cache.Read("Archive", "fixture-key"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [WindowsCredentialTheory]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [Trait("Category", "WindowsIntegration")]
    [InlineData("delegated-cache")]
    public void SelectedServiceGetsReadOnlySecretDirectoryAccess(string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var owner = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            var service = "S-1-5-21-11-22-33-2000000000";
            var cache = new WindowsServiceCredentialCache(root, owner, service);
            cache.Save("FTP", "fixture", new System.Net.NetworkCredential("fixture-user", "fixture-password"));
            var rules = new DirectoryInfo(Path.Combine(root, "service-secrets")).GetAccessControl()
                .GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)).Cast<FileSystemAccessRule>();
            var rule = Assert.Single(rules.Where(rule => rule.IdentityReference.Value == service));
            Assert.Equal(FileSystemRights.ReadAndExecute, rule.FileSystemRights & FileSystemRights.ReadAndExecute);
            Assert.Equal(0, (int)(rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.ChangePermissions)));
            Assert.Equal("fixture-password", cache.Read("FTP", "fixture")!.Password);
            cache.Delete("FTP", "fixture");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-19")]
    [InlineData("S-1-5-20")]
    [InlineData("invalid")]
    public void ServiceRejectsSystemAccounts(string sid) => Assert.Throws<InvalidDataException>(() =>
        new WindowsServiceProfile(sid, Path.GetFullPath("user"), Path.GetFullPath("roaming"), Path.GetFullPath("local")).Validate());

    [Fact]
    public async Task ProfileRoundTripPreservesOwnerAndFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new WindowsServiceProfile("S-1-5-21-11-22-33-1001", Path.GetFullPath("user"), Path.GetFullPath("roaming"), Path.GetFullPath("local"));
            await profile.SaveAsync(root); Assert.Equal(profile, await WindowsServiceProfile.LoadAsync(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void PreRenameServiceRemainsAssociatedWithItsOriginalProfile()
    {
        var root = Path.GetFullPath("existing-profile");
        var current = Path.GetFullPath("SPBackup.Agent.exe");
        var legacy = Path.GetFullPath("OneCBackup.Agent.exe");
        var service = new WindowsAgentServiceInfo(4, WindowsAgentServiceDefinition.ImagePath(legacy, root), "owner");
        Assert.True(service.Matches(current, root));
        Assert.False(service.Matches(current, root + "-other"));
        Assert.False(service.Matches(Path.Combine(Path.GetFullPath("other-install"), "SPBackup.Agent.exe"), root));
    }

    [Fact]
    public void ImagePathQuotesSpacesAndDistinguishesProfiles()
    {
        var exe = Path.GetFullPath(Path.Combine("Program Files", "SPBackup.Agent.exe"));
        var root = Path.GetFullPath("User Profile");
        var command = WindowsAgentServiceDefinition.ImagePath(exe, root);
        Assert.Equal($"\"{exe}\" --service --data-dir \"{root}\"", command);
        Assert.True(new WindowsAgentServiceInfo(4, command, "owner").Matches(exe, root));
        Assert.False(new WindowsAgentServiceInfo(4, command, "owner").Matches(exe, root + "-other"));
        Assert.Throws<ArgumentException>(() => WindowsAgentServiceDefinition.ImagePath(exe, root + "\" --run"));
    }
    [Fact]
    public async Task PipeRoundTripHandlesUnicodePasswordWithoutFile()
    {
        using var stream = new MemoryStream();
        var request = new ServiceManagementRequest("install", "agent", "profile", "account", "sid", "Пароль\\\"$123");
        await ServiceManagementProtocol.WriteAsync(stream, request, default); stream.Position = 0;
        Assert.Equal(request, await ServiceManagementProtocol.ReadAsync<ServiceManagementRequest>(stream, default));
    }
    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(16385)] [InlineData(int.MaxValue)]
    public async Task PipeRejectsInvalidLengthsBeforeAllocation(int length)
    {
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => ServiceManagementProtocol.ReadAsync<ServiceManagementRequest>(stream, default));
    }
    [Fact]
    public async Task PipeRejectsTruncatedPayload()
    {
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, 100);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<EndOfStreamException>(() => ServiceManagementProtocol.ReadAsync<ServiceManagementRequest>(stream, default));
    }
}
