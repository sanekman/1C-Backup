namespace OneCBackup.Core;

/// <summary>Publishes a complete profile snapshot; the caller owns writer synchronization.</summary>
internal static class AtomicProfileFile
{
    public static async Task<FileStream?> OpenReadAsync(string path, CancellationToken token)
    {
        // File.Exists also returns false for transient access failures. Opening
        // directly avoids reporting a published configuration/history as empty.
        // Windows replacement can briefly hide the name or retain open handles;
        // retry only those conditions, for at most 250 ms, honoring cancellation.
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (Exception error) when (OperatingSystem.IsWindows() && attempt < 5
                && (error is FileNotFoundException or UnauthorizedAccessException
                    || error is IOException && (error.HResult & 0xffff) is 32 or 33))
            { await Task.Delay(Math.Min(10 << attempt, 100), token); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
    }

    public static void Publish(string temporary, string destination)
    {
        // MoveFileEx cannot replace a Windows file while a reader holds its old
        // contents, even with delete sharing. ReplaceFile preserves that reader
        // and the destination ACL while new readers see the completed snapshot.
        // Readers must share write/delete: ReplaceFile briefly retains a writable
        // handle to the replacement after it becomes visible at the final path.
        if (OperatingSystem.IsWindows() && File.Exists(destination))
            File.Replace(temporary, destination, destinationBackupFileName: null);
        else
            File.Move(temporary, destination, overwrite: true);
    }
}
