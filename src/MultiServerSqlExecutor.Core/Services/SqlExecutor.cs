using System.Data;
using System.Threading;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Data.SqlClient;
using MultiServerSqlExecutor.Core.Models;

namespace MultiServerSqlExecutor.Core.Services;

public class SqlExecutor
{
    private readonly ConcurrentDictionary<string, byte> _authenticatedInteractiveContextKeys =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly AzureSqlAccessTokenBroker _azureSqlAccessTokenBroker = new();
    private readonly ConfigStore _configStore = new();

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
        // Non-interactive connections can skip this warm-up and go straight to the query wave.
        foreach (var server in servers)
        {
            if (!RequiresSerializedInteractiveAuthentication(server))
            {
                connectedServers.Add(server);
                continue;
            }

            try
            {
                var interactiveContextKey = BuildInteractiveAuthenticationKey(server);
                if (!_authenticatedInteractiveContextKeys.ContainsKey(interactiveContextKey))
                {
                    _ = await ExecuteAsync(server, "Select 1", ct);
                    _authenticatedInteractiveContextKeys.TryAdd(interactiveContextKey, 0);
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

    private static bool RequiresSerializedInteractiveAuthentication(ServerConnection server)
    {
        return server.Authentication is AuthType.AzureInteractive or AuthType.AzureMfa;
    }

    private static string BuildInteractiveAuthenticationKey(ServerConnection server)
    {
        return AzureSqlAccessTokenBroker.BuildInteractiveLoginContextKey(server);
    }

    public async Task<DataTable> ExecuteAsync(ServerConnection server, string sql, CancellationToken ct = default)
    {
        var attemptedInteractiveRefresh = false;

        while (true)
        {
            try
            {
                using var connectionScope = _azureSqlAccessTokenBroker.BeginConnectionScope(server);
                using var conn = CreateConnection(server);
                await conn.OpenAsync(ct);
                PersistDiscoveredTenantId(server);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 0; // no timeout by default
                using var reader = await cmd.ExecuteReaderAsync(ct);
                var dt = new DataTable();
                dt.Load(reader);
                return dt;
            }
            catch (Exception ex) when (!attemptedInteractiveRefresh && IsRetryableInteractiveAuthenticationFailure(server, ex))
            {
                attemptedInteractiveRefresh = true;
                ResetInteractiveAuthentication(server);
            }
        }
    }

    private SqlConnection CreateConnection(ServerConnection server)
    {
        if (!AzureSqlAccessTokenBroker.Supports(server))
        {
            return new SqlConnection(server.BuildConnectionString());
        }

        return new SqlConnection(server.BuildConnectionStringForAccessTokenCallback())
        {
            AccessTokenCallback = _azureSqlAccessTokenBroker.AccessTokenCallback
        };
    }

    private void ResetInteractiveAuthentication(ServerConnection server)
    {
        var interactiveContextKey = BuildInteractiveAuthenticationKey(server);
        _authenticatedInteractiveContextKeys.TryRemove(interactiveContextKey, out _);
        _azureSqlAccessTokenBroker.Invalidate(server.Username);
    }

    private static bool IsRetryableInteractiveAuthenticationFailure(ServerConnection server, Exception ex)
    {
        if (!RequiresSerializedInteractiveAuthentication(server))
        {
            return false;
        }

        var message = FlattenExceptionMessages(ex);
        return message.Contains("token", StringComparison.OrdinalIgnoreCase)
            || message.Contains("login failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || message.Contains("aadsts", StringComparison.OrdinalIgnoreCase)
            || message.Contains("expired", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
    }

    private static string FlattenExceptionMessages(Exception ex)
    {
        var builder = new StringBuilder();
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(current.Message);
        }

        return builder.ToString();
    }

    private void PersistDiscoveredTenantId(ServerConnection server)
    {
        if (!RequiresSerializedInteractiveAuthentication(server) || !string.IsNullOrWhiteSpace(server.TenantId))
        {
            return;
        }

        var discoveredTenantId = _azureSqlAccessTokenBroker.GetDiscoveredTenantId();
        if (string.IsNullOrWhiteSpace(discoveredTenantId))
        {
            return;
        }

        server.TenantId = discoveredTenantId;

        var configuredServers = _configStore.Load().ToList();
        var configuredServer = configuredServers.FirstOrDefault(s =>
            string.Equals(s.Name, server.Name, StringComparison.OrdinalIgnoreCase));

        if (configuredServer == null || !string.IsNullOrWhiteSpace(configuredServer.TenantId))
        {
            return;
        }

        configuredServer.TenantId = discoveredTenantId;
        _configStore.Save(configuredServers);
    }
}
