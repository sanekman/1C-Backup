namespace SPBackup.Core;

/// <summary>An explicit outbound SMB login, bound to one share. The password stays in Windows.</summary>
public sealed record NetworkShareSettings
{
    public string ShareRoot { get; init; } = "";
    public string CredentialKey { get; init; } = "";
    public static string Root(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException(@"Укажите сетевой путь вида \\NAS\Backups.");
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (!path.StartsWith(@"\\") || parts.Length < 2 || parts.Any(p => p is "." or ".."
            || p.Any(c => char.IsControl(c) || "<>:\"/|?*".Contains(c))))
            throw new ArgumentException(@"Укажите сетевой путь вида \\NAS\Backups.");
        return @"\\" + parts[0] + @"\" + parts[1];
    }
    public IReadOnlyList<string> Validate(string destination)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(CredentialKey) || CredentialKey.Length > 512 || CredentialKey.Any(char.IsControl))
            errors.Add("Задайте логин и пароль сетевого хранилища заново.");
        try
        {
            if (!string.Equals(Root(destination), ShareRoot, StringComparison.OrdinalIgnoreCase))
                errors.Add("Сетевое хранилище изменено. Задайте его логин и пароль заново.");
        }
        catch (ArgumentException) { errors.Add(@"Для сетевого входа укажите путь вида \\NAS\Backups."); }
        return errors;
    }
}
