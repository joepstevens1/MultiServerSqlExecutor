namespace MultiServerSqlExecutor.Core.Models;

public class ServerConnection
{
    public string Name { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty; // e.g., yourserver.database.windows.net
    public string Database { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty; // Consider using secure storage in production

    public string BuildConnectionString()
    {
        // Encrypt=False to avoid certificate issues by default; TrustServerCertificate=True for convenience.
        return $"Server=tcp:{Server},1433;Initial Catalog={Database};Persist Security Info=False;User ID={Username};Password={Password};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=True;Connection Timeout=30;";
    }
}
