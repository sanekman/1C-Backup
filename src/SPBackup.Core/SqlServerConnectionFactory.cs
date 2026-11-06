using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace SPBackup.Core;

// Both backup execution and read-only monitoring use the same authentication and TLS policy.
internal static class SqlServerConnectionFactory
{
    internal static string? ValidateServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return "Укажите адрес MS SQL: например SQL-SERVER, SQL-SERVER\\SQLEXPRESS или 192.168.1.10,1433.";
        if (server.Contains("://", StringComparison.Ordinal) || server.Contains(';') || server.Any(char.IsControl))
            return "Укажите только адрес MS SQL, без https:// и строки подключения. Например SQL-SERVER\\SQLEXPRESS или 192.168.1.10,1433 (порт через запятую).";
        return null;
    }

    internal static IReadOnlyList<string> ValidateAuthentication(SqlServerAuthentication authentication, string? key)
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(authentication)) errors.Add("Выберите способ входа в MS SQL.");
        if (authentication == SqlServerAuthentication.SqlServer &&
            (string.IsNullOrWhiteSpace(key) || key.Length > 512 || key.Any(char.IsControl)))
            errors.Add("Сохраните логин и пароль MS SQL в настройках подключения.");
        return errors;
    }

    internal static async Task<DbConnection> CreateAsync(string server, SqlServerAuthentication authentication,
        string? key, string database, string application, ISqlServerCredentialStore? credentials, CancellationToken token, bool trustServerCertificate = false)
    {
        var errors = ValidateAuthentication(authentication, key);
        if (ValidateServer(server) is { } serverError) throw new InvalidDataException(serverError);
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        token.ThrowIfCancellationRequested();
        var sqlLogin = authentication == SqlServerAuthentication.SqlServer;
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server, InitialCatalog = database, IntegratedSecurity = !sqlLogin,
            Encrypt = SqlConnectionEncryptOption.Mandatory, TrustServerCertificate = trustServerCertificate, ConnectTimeout = 15,
            ApplicationName = application, Pooling = !sqlLogin
        };
        if (!sqlLogin) return new SqlConnection(builder.ConnectionString);
        if (credentials is null)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Для входа в MS SQL по логину нужно хранилище учётных данных.");
            credentials = new WindowsSqlServerCredentialStore();
        }
        var value = await credentials.GetAsync(key!, token);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(value.UserName)) throw new InvalidDataException("В сохранённых учётных данных MS SQL отсутствует логин.");
        var password = value.SecurePassword;
        password.MakeReadOnly();
        return new SqlConnection(builder.ConnectionString, new SqlCredential(value.UserName, password));
    }
}
