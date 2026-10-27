namespace SPBackup.Core;

/// <summary>Builds fail-closed libpq settings for a future explicitly protected connection.</summary>
internal static class PostgreSqlTlsPolicy
{
    public static IReadOnlyDictionary<string, string?> VerifyFull(string? rootCertificatePath)
    {
        if (rootCertificatePath is not null && (string.IsNullOrWhiteSpace(rootCertificatePath)
            || !Path.IsPathFullyQualified(rootCertificatePath) || rootCertificatePath.Any(char.IsControl)))
            throw new ArgumentException("Укажите полный путь к корневому сертификату PostgreSQL.", nameof(rootCertificatePath));
        return new Dictionary<string, string?>
        {
            ["PGSSLMODE"] = "verify-full",
            // Do not silently inherit a different destination or trust anchor.
            ["PGHOSTADDR"] = null,
            ["PGSSLROOTCERT"] = rootCertificatePath
        };
    }
}
