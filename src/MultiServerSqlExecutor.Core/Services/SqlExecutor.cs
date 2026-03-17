using System.Data;
using System.Threading;
using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using MultiServerSqlExecutor.Core.Models;

namespace MultiServerSqlExecutor.Core.Services;

public class SqlExecutor
{
    private readonly ConcurrentDictionary<string, byte> _authenticatedServerKeys =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<Dictionary<string, DataTable>> ExecuteOnAllAsync(
        IReadOnlyList<ServerConnection> servers,
        string sql,
        CancellationToken ct = default,
        Action<int, int, int>? progressCallback = null)
    {
        var executionResults = await ExecuteOnAllWithStatusAsync(
            servers,
            sql,
            ct,
            statusCallback: null,
            progressCallback: progressCallback);

        var failures = executionResults.Where(r => !r.Succeeded).ToList();
        if (failures.Count > 0)
        {
            var exceptions = failures
                .Select(r => new Exception($"{r.Server.Name}: {r.Error?.Message}", r.Error))
                .ToList();
            throw new AggregateException("One or more servers failed to execute the query.", exceptions);
        }

        return executionResults.ToDictionary(x => x.Server.Name, x => x.Data!);
    }

    public async Task<IReadOnlyList<ServerExecutionResult>> ExecuteOnAllWithStatusAsync(
        IReadOnlyList<ServerConnection> servers,
        string sql,
        CancellationToken ct = default,
        Action<ServerExecutionStatusUpdate>? statusCallback = null,
        Action<int, int, int>? progressCallback = null)
    {
        progressCallback?.Invoke(servers.Count, servers.Count, 0);
        foreach (var server in servers)
        {
            statusCallback?.Invoke(new ServerExecutionStatusUpdate
            {
                Server = server,
                Status = QueryExecutionStatus.NotStarted
            });
        }

        var completed = 0;
        var results = new List<ServerExecutionResult>();
        var connectedServers = new List<ServerConnection>();

        // Force interactive auth serially so login prompts are not shown in parallel.
        foreach (var server in servers)
        {
            try
            {
                var serverKey = BuildServerKey(server);
                if (!_authenticatedServerKeys.ContainsKey(serverKey))
                {
                    _ = await ExecuteAsync(server, "Select 1", ct);
                    _authenticatedServerKeys.TryAdd(serverKey, 0);
                }

                statusCallback?.Invoke(new ServerExecutionStatusUpdate
                {
                    Server = server,
                    Status = QueryExecutionStatus.Connected
                });
                connectedServers.Add(server);
            }
            catch (Exception ex)
            {
                statusCallback?.Invoke(new ServerExecutionStatusUpdate
                {
                    Server = server,
                    Status = QueryExecutionStatus.Errored,
                    ErrorMessage = ex.Message
                });

                results.Add(new ServerExecutionResult
                {
                    Server = server,
                    Error = ex
                });

                var currentCompleted = Interlocked.Increment(ref completed);
                var pending = servers.Count - currentCompleted;
                progressCallback?.Invoke(servers.Count, pending, currentCompleted);
            }
        }

        var tasks = connectedServers.Select(async server =>
        {
            var serverKey = BuildServerKey(server);
            try
            {
                statusCallback?.Invoke(new ServerExecutionStatusUpdate
                {
                    Server = server,
                    Status = QueryExecutionStatus.Running
                });

                var data = await ExecuteAsync(server, sql, ct);
                statusCallback?.Invoke(new ServerExecutionStatusUpdate
                {
                    Server = server,
                    Status = QueryExecutionStatus.Completed
                });

                return new ServerExecutionResult
                {
                    Server = server,
                    Data = data
                };
            }
            catch (Exception ex)
            {
                _authenticatedServerKeys.TryRemove(serverKey, out _);

                statusCallback?.Invoke(new ServerExecutionStatusUpdate
                {
                    Server = server,
                    Status = QueryExecutionStatus.Errored,
                    ErrorMessage = ex.Message
                });

                return new ServerExecutionResult
                {
                    Server = server,
                    Error = ex
                };
            }
            finally
            {
                var currentCompleted = Interlocked.Increment(ref completed);
                var pending = servers.Count - currentCompleted;
                progressCallback?.Invoke(servers.Count, pending, currentCompleted);
            }
        });

        var queryResults = await Task.WhenAll(tasks);
        results.AddRange(queryResults);
        return results;
    }

    private static string BuildServerKey(ServerConnection server)
    {
        return string.Join(
            "|",
            server.Authentication.ToString(),
            server.Server ?? string.Empty,
            server.Database ?? string.Empty,
            server.Username ?? string.Empty);
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
