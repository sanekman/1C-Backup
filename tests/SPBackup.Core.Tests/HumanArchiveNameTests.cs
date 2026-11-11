using System.IO.Compression;
using SPBackup.Core;
using Xunit;

namespace SPBackup.Core.Tests;

public sealed class HumanArchiveNameTests
{
    [Fact]
    public async Task SqlArchiveOmitsInternalMediaIdentifierAndRetainsOriginalMedia()
    {
        var root = Path.Combine(Path.GetTempPath(), "spbackup-human-archive-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(root);
            var source = Path.Combine(root, "Ledger_2026-11-11_17-36-47_abcdef.bak");
            await File.WriteAllTextAsync(source, "verified fixture backup");
            var job = new BackupJob { Name = "Ledger", Source = "fixture", Kind = DatabaseKind.SqlServer, ArchiveCopies = true,
                DestinationDirectory = Path.Combine(root, "copies"), SqlServer = new() { Server = ".", ServerBackupDirectory = root, AgentReadDirectory = root } };
            var result = await new BackupDelivery(new FileBackupStorage()).DeliverAsync(job, source, Path.Combine(root, "work"));
            Assert.Matches(@"^Ledger_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}( \(\d+\))?\.bak\.zip$", Path.GetFileName(result.Location));
            Assert.DoesNotContain("abcdef", Path.GetFileName(result.Location));
            Assert.Equal("verified fixture backup", await File.ReadAllTextAsync(source));
            using var zip = ZipFile.OpenRead(result.Location);
            using var reader = new StreamReader(Assert.Single(zip.Entries).Open());
            Assert.Equal("verified fixture backup", await reader.ReadToEndAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
