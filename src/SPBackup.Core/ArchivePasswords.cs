using System.Runtime.Versioning;

namespace SPBackup.Core;

public interface IArchivePasswordStore
{
    Task<string> GetAsync(string key, CancellationToken cancellationToken = default);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsArchivePasswordStore : IArchivePasswordStore
{
    private readonly WindowsFtpCredentialStore store = new("Archive");
    public async Task<string> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try { return (await store.GetAsync(key, cancellationToken)).Password; }
        catch (InvalidOperationException) { throw new InvalidOperationException("Пароль архива не найден в хранилище Windows. Сохраните пароль или введите его при извлечении."); }
    }
    public bool Delete(string key) => store.Delete(key);
    // Each save creates a new binding: old copies keep their original password.
    public string SaveNew(string password)
    {
        ArchivePasswords.Validate(password);
        var key = Guid.NewGuid().ToString("N");
        store.Save(key, "archive", password);
        return key;
    }
}

public static class ArchivePasswords
{
    public static void Validate(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 1024 || password.Any(char.IsControl))
            throw new ArgumentException("Введите пароль от 1 до 1024 символов без управляющих символов.");
    }
    public static async Task<string?> ResolveAsync(string? key, IArchivePasswordStore? store,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (key is null) return null;
        if (store is null)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Хранилище паролей архивов доступно только в Windows.");
            store = new WindowsArchivePasswordStore();
        }
        var password = await store.GetAsync(key, cancellationToken);
        Validate(password);
        return password;
    }
}
