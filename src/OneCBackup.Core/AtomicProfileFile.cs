namespace OneCBackup.Core;

/// <summary>Publishes a complete profile snapshot; the caller owns writer synchronization.</summary>
internal static class AtomicProfileFile
{
    public static void Publish(string temporary, string destination)
    {
        // MoveFileEx cannot replace a Windows file while a reader holds its old
        // contents, even with delete sharing. ReplaceFile preserves that reader
        // and the destination ACL while new readers see the completed snapshot.
        if (OperatingSystem.IsWindows() && File.Exists(destination))
            File.Replace(temporary, destination, destinationBackupFileName: null);
        else
            File.Move(temporary, destination, overwrite: true);
    }
}
