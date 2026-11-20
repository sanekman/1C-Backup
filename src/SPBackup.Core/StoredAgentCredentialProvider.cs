namespace SPBackup.Core;

/// <summary>Uses the same Windows Vault/delegated service store as enrollment. Never persists a bearer token.</summary>
internal sealed class StoredAgentCredentialProvider : IAgentCredentialProvider
{
    private readonly string key;
    private readonly IFtpCredentialStore store;

    internal StoredAgentCredentialProvider(ServerMonitoringSettings connection, IFtpCredentialStore store)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(store);
        connection.Validate();
        this.key = connection.CredentialKey;
        this.store = store;
    }

    public async Task<string> GetBearerTokenAsync(string credentialKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(key, credentialKey, StringComparison.Ordinal))
            throw new InvalidOperationException("Учётные данные не относятся к подключению агента.");
        string token;
        try { token = (await store.GetAsync(key, cancellationToken)).Password; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Чтение токена подключения отменено.", cancellationToken); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A third-party credential adapter may include its secret in the exception. Do not retain it as InnerException.
            throw new InvalidOperationException("Не удалось прочитать токен подключения агента. Проверьте подключение и экспорт учётных данных для службы.");
        }
        if (!ValidToken(token)) throw new InvalidOperationException("В хранилище нет корректного токена подключения агента. Повторите подключение к серверу.");
        return token;
    }

    private static bool ValidToken(string token)
    {
        if (token.Length is < 1 or > 4096) return false;
        var padding = false;
        var characters = 0;
        foreach (var value in token)
        {
            if (value == '=') { padding = true; continue; }
            if (padding || !(char.IsAsciiLetterOrDigit(value) || value is '-' or '.' or '_' or '~' or '+' or '/')) return false;
            characters++;
        }
        return characters > 0;
    }
}
