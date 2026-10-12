using System.Security.AccessControl;
using System.Buffers.Binary;
using OneCBackup.Core;
using Xunit;

namespace OneCBackup.Core.Tests;

public class WindowsServiceTests
{
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
    public void ImagePathQuotesSpacesAndDistinguishesProfiles()
    {
        var exe = Path.GetFullPath(Path.Combine("Program Files", "OneCBackup.Agent.exe"));
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
