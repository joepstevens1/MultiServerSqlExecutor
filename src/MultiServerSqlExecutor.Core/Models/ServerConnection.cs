using Microsoft.Data.SqlClient;

namespace MultiServerSqlExecutor.Core.Models;

public enum AuthType
{
    SqlPassword,
    AzureInteractive,
    AzurePassword,
    AzureMfa
}

public class ServerConnection
{
    public string Name { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty; // e.g., yourserver.database.windows.net
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty; // Consider using secure storage in production
    public string TenantId { get; set; } = string.Empty;
    public AuthType Authentication { get; set; } = AuthType.SqlPassword;
    public List<string> Groups { get; set; } = new();

    public string BuildConnectionString(bool readOnly = false)
    {
        var builder = CreateBaseConnectionStringBuilder(readOnly);

        switch (Authentication)
        {
            case AuthType.SqlPassword:
                builder.UserID = Username;
                builder.Password = Password;
                break;
            case AuthType.AzureInteractive:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                builder.UserID = Username; // Optional for interactive but often useful as a hint
                break;
            case AuthType.AzurePassword:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryPassword;
                builder.UserID = Username;
                builder.Password = Password;
                break;
            case AuthType.AzureMfa:
                builder.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                builder.UserID = Username;
                break;
        }

        return builder.ConnectionString;
    }

    public string BuildConnectionStringForAccessTokenCallback(bool readOnly = false)
    {
        var builder = CreateBaseConnectionStringBuilder(readOnly);

        if (!string.IsNullOrWhiteSpace(Username))
        {
            builder.UserID = Username;
        }

        return builder.ConnectionString;
    }

    private SqlConnectionStringBuilder CreateBaseConnectionStringBuilder(bool readOnly = false)
    {
        var dataSource = Server;
        if (Authentication != AuthType.SqlPassword && !string.IsNullOrWhiteSpace(dataSource) && !dataSource.Contains('.'))
        {
            dataSource += ".database.windows.net";
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = Database,
            PersistSecurityInfo = false,
            MultipleActiveResultSets = false,
            Encrypt = true,
            TrustServerCertificate = true
        };

        if (readOnly)
        {
            // Request the read-only replica of a failover group / readable secondary
            // and signal intent so the server rejects any accidental write.
            builder.ApplicationIntent = ApplicationIntent.ReadOnly;
        }

        return builder;
    }
}
