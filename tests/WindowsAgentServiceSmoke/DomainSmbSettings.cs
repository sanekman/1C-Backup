using System.Security.Principal;

// The disposable lab controller supplies these through stdin, never a command line or JSON file.
internal sealed record DomainSmbSettings(string Account, string Password, string AllowedUnc, string DeniedUnc, string DomainSid)
{
    public void Validate()
    {
        if (Environment.GetEnvironmentVariable("SPBACKUP_DOMAIN_SMB_TEST") != "1"
            || string.IsNullOrWhiteSpace(Account) || !Account.Contains('\\')
            || string.IsNullOrEmpty(Password) || string.IsNullOrWhiteSpace(DomainSid))
            throw new InvalidDataException("Explicit disposable domain SMB test configuration is required.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(DomainSid, @"^S-1-5-21-\d+-\d+-\d+$"))
            throw new InvalidDataException("Expected an actual domain SID.");
        string Server(string path)
        {
            if (!path.StartsWith(@"\\", StringComparison.Ordinal) || path.Any(char.IsControl))
                throw new InvalidDataException("Expected a remote UNC fixture share.");
            var parts = path[2..].Split('\\');
            if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace)
                || parts[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                || parts[0] is "." or "localhost" or "127.0.0.1" or "::1")
                throw new InvalidDataException("The domain SMB server must be a separate machine and use a direct fixture share.");
            return parts[0];
        }
        if (!Server(AllowedUnc).Equals(Server(DeniedUnc), StringComparison.OrdinalIgnoreCase)
            || AllowedUnc.Equals(DeniedUnc, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected distinct allowed/denied shares on one separate domain server.");
    }
}
