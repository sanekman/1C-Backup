using OneCBackup.Core;
using System.Runtime.Versioning;
using Xunit;

namespace OneCBackup.Core.Tests;

public sealed class WindowsCredentialTheoryAttribute : TheoryAttribute
{
    public WindowsCredentialTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Native Windows credential manager requires Windows.";
    }
}

public sealed class WindowsCredentialTests
{
    [WindowsCredentialTheory]
    [SupportedOSPlatform("windows")]
    [Trait("Category", "WindowsIntegration")]
    [InlineData("пароль 🗝")]
    [InlineData("")]
    public async Task NativeCredentialRoundTripUpdateAndExplicitCleanup(string password)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var key = "integration-fixture-" + Guid.NewGuid().ToString("N");
        var store = new WindowsFtpCredentialStore();
        try
        {
            store.Save(key, "fixture-user", password);
            var first = await store.GetAsync(key);
            Assert.Equal("fixture-user", first.UserName);
            Assert.Equal(password, first.Password);
            store.Save(key, "updated-user", "updated-password");
            var updated = await store.GetAsync(key);
            Assert.Equal("updated-user", updated.UserName);
            Assert.Equal("updated-password", updated.Password);
            Assert.True(store.Delete(key));
            Assert.False(store.Delete(key));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync(key));
        }
        finally { store.Delete(key); }
    }
}
