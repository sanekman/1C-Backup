using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SPBackup.Core;

internal static partial class Program
{
    private static async Task<int> VerifyNotificationServiceAsync(string agent, string output)
    {
        var directory = DefaultOwnerProfile;
        agent = Path.GetFullPath(agent);
        output = Path.GetFullPath(output);
        Require(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" && File.Exists(agent)
            && WindowsAgentService.QueryInfo() is null && !Directory.Exists(directory),
            "Notification fixture requires a clean disposable Windows runner.");
        Directory.CreateDirectory(output);
        var account = "spbn" + Guid.NewGuid().ToString("N")[..10];
        var password = "Nt!2z-" + Guid.NewGuid().ToString("N");
        var credential = "fixture-" + Guid.NewGuid().ToString("N");
        var invalidToken = "invalid-telegram-fixture-" + Guid.NewGuid().ToString("N");
        var owner = WindowsIdentity.GetCurrent().User!;
        var created = false;
        var checks = new List<string>();
        try
        {
            Native.AddUser(account, password);
            created = true;
            var selected = WindowsServiceAccounts.Resolve(account);
            var target = Directory.CreateDirectory(directory);
            var acl = target.GetAccessControl();
            acl.SetOwner(owner);
            target.SetAccessControl(acl);
            var profile = new WindowsServiceProfile(owner.Value, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) { ServiceSid = selected.Sid };
            await profile.SaveAsync(directory);
            var binary = new DirectoryInfo(Path.GetDirectoryName(agent)!);
            var access = binary.GetAccessControl();
            access.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(selected.Sid), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            binary.SetAccessControl(access);
            var telegram = new NotificationChannel { Enabled = true, Recipient = "-100123", CredentialKey = credential,
                EnabledSinceUtc = DateTimeOffset.UtcNow };
            var max = telegram with { Id = Guid.NewGuid(), Provider = NotificationProvider.Max, CredentialKey = credential + "-missing" };
            await new NotificationSettingsStore(directory).SaveAsync(new() { Channels = [telegram, max] });
            var control = new LocalAgentControlStore(directory);
            async Task<LocalAgentState> Ready(Guid? previous = null)
            {
                await WaitService(4);
                LocalAgentState? live = null;
                await Wait(async () =>
                {
                    live = await control.ReadAsync();
                    return live?.SessionId != previous && LocalNotificationTests.IsAvailable(live, DateTimeOffset.UtcNow);
                }, "Service did not advertise notification tests.");
                return live!;
            }

            WindowsAgentService.Install(agent, directory, selected.Name, owner.Value, password);
            var first = await Ready();
            var tests = new LocalNotificationTests(directory);
            foreach (var provider in new[] { NotificationProvider.Telegram, NotificationProvider.Max })
            {
                var probe = await tests.TestAsync(provider);
                Require(probe.ProcessId == first.ProcessId && probe.SessionId == first.SessionId &&
                    probe is { Outcome: LocalNotificationTestOutcome.Failed, Failure: NotificationFailure.CredentialUnavailable },
                    "The service did not report missing protected credentials for both providers.");
            }
            checks.Add("both-test-requests-reached-the-actual-service-before-network");
            WindowsFtpCredentialStore.SetServiceCache(new(directory, owner.Value, selected.Sid));
            new WindowsNotificationCredentialStore().Save(credential, invalidToken);
            var saved = await tests.TestAsync(NotificationProvider.Telegram);
            Require(saved.ProcessId == first.ProcessId && saved.Failure == NotificationFailure.InvalidToken,
                "The running service could not read the newly exported token.");
            WindowsAgentService.Stop(agent, directory);
            await WaitService(1);
            await WaitProcessExit(first.ProcessId, agent);
            WindowsAgentService.Start(agent, directory);
            var second = await Ready(first.SessionId);
            var restarted = await tests.TestAsync(NotificationProvider.Telegram);
            Require(restarted.ProcessId == second.ProcessId && restarted.SessionId == second.SessionId &&
                restarted is { Outcome: LocalNotificationTestOutcome.Failed, Failure: NotificationFailure.InvalidToken },
                "A new service session could not read the exported token.");
            checks.Add("protected-token-reached-running-and-restarted-service-without-http");
            var receipt = await File.ReadAllTextAsync(Path.Combine(directory, "notification-test-result.json"));
            Require(!receipt.Contains(invalidToken, StringComparison.Ordinal) && !receipt.Contains(credential, StringComparison.Ordinal)
                && !receipt.Contains(telegram.Recipient, StringComparison.Ordinal), "A secret or recipient was written to the test result.");
            await File.WriteAllTextAsync(Path.Combine(output, "notification-service-result.json"), JsonSerializer.Serialize(new { success = true, checks }));
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "notification-service-result.json"),
                JsonSerializer.Serialize(new { success = false, checks, error = error.Message.Replace(invalidToken, "[redacted]") }));
            return 1;
        }
        finally
        {
            WindowsFtpCredentialStore.SetServiceCache(null);
            new WindowsNotificationCredentialStore().Delete(credential);
            if (WindowsAgentService.QueryInfo() is not null)
            {
                WindowsAgentService.Stop(agent, directory);
                await WaitService(1);
                WindowsAgentService.Remove(agent, directory);
            }
            if (created) Native.DeleteUser(account);
        }
    }
}
