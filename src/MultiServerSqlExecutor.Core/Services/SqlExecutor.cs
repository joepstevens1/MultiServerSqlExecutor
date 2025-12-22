using System.Data;
using Microsoft.Data.SqlClient;
using MultiServerSqlExecutor.Core.Models;

namespace MultiServerSqlExecutor.Core.Services;

public class SqlExecutor
{
    public async Task<Dictionary<string, DataTable>> ExecuteOnAllAsync(IEnumerable<ServerConnection> servers, string sql, CancellationToken ct = default)
    {
        var tasks = servers.Select(async s =>
        {
            var dt = await ExecuteAsync(s, sql, ct);
            return (s.Name, dt);
        });

        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(x => x.Name, x => x.dt);
    }

    public async Task<DataTable> ExecuteAsync(ServerConnection server, string sql, CancellationToken ct = default)
    {
        using var conn = new SqlConnection(server.BuildConnectionString());
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0; // no timeout by default
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var dt = new DataTable();
        dt.Load(reader);
        return dt;
    }
}
