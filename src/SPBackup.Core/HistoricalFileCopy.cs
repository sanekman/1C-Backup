namespace SPBackup.Core;

public static class HistoricalFileCopy
{
    /// <summary>Rebuilds a read configuration for a catalogued file system destination without altering the live job.</summary>
    public static BackupJob? CreateReadJob(BackupRecord point, BackupJob job)
    {
        if (point.Backup.FileSnapshot is null || point.JobId != job.Id
            || job.StorageJobs.Any(target => StorageAddress.Identity(target) == point.StorageIdentity)) return null;
        var kind = point.StorageIdentity.StartsWith("NetworkShare:", StringComparison.Ordinal) ? StorageKind.NetworkShare
            : point.StorageIdentity.StartsWith("LocalDirectory:", StringComparison.Ordinal) ? StorageKind.LocalDirectory
            : (StorageKind?)null;
        var path = point.Backup.Location;
        if (kind is null || !Path.IsPathFullyQualified(path) || Path.GetDirectoryName(path) is not { } folder) return null;
        if (kind == StorageKind.NetworkShare) NetworkShareSettings.Root(folder);
        var readJob = job with { StorageKind = kind.Value, DestinationDirectory = folder, AdditionalDestinations = [],
            NetworkShare = null, Ftp = null, GoogleDrive = null, RemoteGateway = null, ManagedSecrets = null };
        if (StorageAddress.Identity(readJob) != point.StorageIdentity || readJob.Validate().Count != 0) return null;
        BackupCopyVerifier.ValidateRecord(point, readJob);
        return readJob;
    }
}
