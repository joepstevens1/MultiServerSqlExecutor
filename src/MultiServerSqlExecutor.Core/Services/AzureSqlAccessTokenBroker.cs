using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;
using MultiServerSqlExecutor.Core.Models;

namespace MultiServerSqlExecutor.Core.Services;

internal sealed class AzureSqlAccessTokenBroker
{
    private readonly Func<SqlAuthenticationParameters, CancellationToken, Task<SqlAuthenticationToken>> _accessTokenCallback;
    private readonly AsyncLocal<PendingInteractiveConnection?> _currentPendingConnection = new();

    private readonly ConcurrentDictionary<string, InteractiveBrowserCredential> _credentialsByAuthorityHostAndUser =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _credentialLocksByAuthorityHostAndUser =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tokenRequestLocksByAuthorityAndUser =
        new(StringComparer.OrdinalIgnoreCase);

    public AzureSqlAccessTokenBroker()
    {
        _accessTokenCallback = GetAccessTokenAsync;
    }

    public Func<SqlAuthenticationParameters, CancellationToken, Task<SqlAuthenticationToken>> AccessTokenCallback =>
        _accessTokenCallback;

    public static bool Supports(ServerConnection server)
    {
        return server.Authentication is AuthType.AzureInteractive or AuthType.AzureMfa;
    }

    public void Invalidate(string? username)
    {
        var normalizedUsername = Normalize(username);
        if (normalizedUsername.Length == 0)
        {
            _credentialsByAuthorityHostAndUser.Clear();
            _credentialLocksByAuthorityHostAndUser.Clear();
            _tokenRequestLocksByAuthorityAndUser.Clear();
            return;
        }

        foreach (var key in _credentialsByAuthorityHostAndUser.Keys)
        {
            if (MatchesTrailingUserKey(key, normalizedUsername))
            {
                _credentialsByAuthorityHostAndUser.TryRemove(key, out _);
            }
        }

        foreach (var key in _credentialLocksByAuthorityHostAndUser.Keys)
        {
            if (MatchesTrailingUserKey(key, normalizedUsername))
            {
                _credentialLocksByAuthorityHostAndUser.TryRemove(key, out _);
            }
        }

        foreach (var key in _tokenRequestLocksByAuthorityAndUser.Keys)
        {
            if (MatchesMiddleUserKey(key, normalizedUsername))
            {
                _tokenRequestLocksByAuthorityAndUser.TryRemove(key, out _);
            }
        }
    }

    public static string BuildInteractiveLoginContextKey(ServerConnection server)
    {
        return string.Join(
            "|",
            server.Authentication.ToString(),
            Normalize(server.TenantId),
            Normalize(server.Username));
    }

    public IDisposable BeginConnectionScope(ServerConnection server)
    {
        var previous = _currentPendingConnection.Value;
        var pendingConnection = new PendingInteractiveConnection(
            server.Name,
            Normalize(server.Username),
            Normalize(server.TenantId));

        _currentPendingConnection.Value = pendingConnection;
        return new PendingConnectionScope(this, previous);
    }

    public string? GetDiscoveredTenantId()
    {
        var pendingConnection = _currentPendingConnection.Value;
        if (pendingConnection == null)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(pendingConnection.DiscoveredTenantId)
            ? null
            : pendingConnection.DiscoveredTenantId;
    }

    private async Task<SqlAuthenticationToken> GetAccessTokenAsync(
        SqlAuthenticationParameters authParams,
        CancellationToken cancellationToken)
    {
        var scope = BuildScope(authParams.Resource);
        var tenantId = ResolveTenantId(authParams);
        var tokenRequestContext = CreateTokenRequestContext(scope, tenantId);
        var credentialKey = BuildCredentialKey(authParams);
        var tokenRequestKey = BuildTokenRequestKey(authParams, scope, tenantId);

        var tokenGate = _tokenRequestLocksByAuthorityAndUser.GetOrAdd(tokenRequestKey, _ => new SemaphoreSlim(1, 1));
        await tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (_credentialsByAuthorityHostAndUser.TryGetValue(credentialKey, out var existingCredential))
            {
                var existingToken = await existingCredential.GetTokenAsync(tokenRequestContext, cancellationToken);
                return new SqlAuthenticationToken(existingToken.Token, existingToken.ExpiresOn);
            }

            var gate = _credentialLocksByAuthorityHostAndUser.GetOrAdd(credentialKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (_credentialsByAuthorityHostAndUser.TryGetValue(credentialKey, out existingCredential))
                {
                    var cachedToken = await existingCredential.GetTokenAsync(tokenRequestContext, cancellationToken);
                    return new SqlAuthenticationToken(cachedToken.Token, cachedToken.ExpiresOn);
                }

                var credential = CreateCredential(authParams);
                var token = await credential.GetTokenAsync(tokenRequestContext, cancellationToken);
                _credentialsByAuthorityHostAndUser[credentialKey] = credential;

                return new SqlAuthenticationToken(token.Token, token.ExpiresOn);
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            tokenGate.Release();
        }
    }

    private static InteractiveBrowserCredential CreateCredential(SqlAuthenticationParameters authParams)
    {
        var options = new InteractiveBrowserCredentialOptions();
        var authorityHost = TryExtractAuthorityHost(authParams.Authority);
        if (authorityHost != null)
        {
            options.AuthorityHost = authorityHost;
        }

        if (!string.IsNullOrWhiteSpace(authParams.UserId))
        {
            options.LoginHint = authParams.UserId;
        }

        options.AdditionallyAllowedTenants.Add("*");

        return new InteractiveBrowserCredential(options);
    }

    private static string BuildCredentialKey(SqlAuthenticationParameters authParams)
    {
        return string.Join(
            "|",
            NormalizeAuthorityHost(authParams.Authority),
            Normalize(authParams.UserId));
    }

    private static string BuildTokenRequestKey(SqlAuthenticationParameters authParams, string scope, string? tenantId)
    {
        return string.Join(
            "|",
            Normalize(tenantId),
            Normalize(authParams.UserId),
            scope);
    }

    private static string BuildScope(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            throw new InvalidOperationException("SqlClient did not provide a resource for Azure SQL token acquisition.");
        }

        return resource.EndsWith("/.default", StringComparison.OrdinalIgnoreCase)
            ? resource
            : $"{resource.TrimEnd('/')}/.default";
    }

    private static TokenRequestContext CreateTokenRequestContext(string scope, string? tenantId)
    {
        return new TokenRequestContext([scope], null, null, tenantId);
    }

    private string? ResolveTenantId(SqlAuthenticationParameters authParams)
    {
        var discoveredTenantId = TryExtractTenantId(authParams.Authority);
        var pendingConnection = _currentPendingConnection.Value;
        if (pendingConnection != null)
        {
            if (!string.IsNullOrWhiteSpace(pendingConnection.ConfiguredTenantId))
            {
                return pendingConnection.ConfiguredTenantId;
            }

            pendingConnection.DiscoveredTenantId = discoveredTenantId;
        }

        return discoveredTenantId;
    }

    private static Uri? TryExtractAuthorityHost(string? authority)
    {
        if (string.IsNullOrWhiteSpace(authority) || !Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri))
        {
            return null;
        }

        return new Uri(authorityUri.GetLeftPart(UriPartial.Authority));
    }

    private static string? TryExtractTenantId(string? authority)
    {
        if (string.IsNullOrWhiteSpace(authority) || !Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri))
        {
            return null;
        }

        var pathSegments = authorityUri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return pathSegments.Length == 0 ? null : pathSegments[^1];
    }

    private static string Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string NormalizeAuthorityHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        var normalized = value.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = $"https://{normalized}";
        }

        return Uri.TryCreate(normalized, UriKind.Absolute, out uri)
            ? uri.GetLeftPart(UriPartial.Authority).TrimEnd('/')
            : value.Trim();
    }

    private static bool MatchesTrailingUserKey(string cacheKey, string normalizedUsername)
    {
        var separatorIndex = cacheKey.LastIndexOf('|');
        if (separatorIndex < 0 || separatorIndex == cacheKey.Length - 1)
        {
            return false;
        }

        var keyUsername = cacheKey[(separatorIndex + 1)..];
        return string.Equals(keyUsername, normalizedUsername, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesMiddleUserKey(string cacheKey, string normalizedUsername)
    {
        var firstSeparatorIndex = cacheKey.IndexOf('|');
        var lastSeparatorIndex = cacheKey.LastIndexOf('|');
        if (firstSeparatorIndex < 0 || lastSeparatorIndex <= firstSeparatorIndex + 1)
        {
            return false;
        }

        var keyUsername = cacheKey[(firstSeparatorIndex + 1)..lastSeparatorIndex];
        return string.Equals(keyUsername, normalizedUsername, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PendingInteractiveConnection
    {
        public PendingInteractiveConnection(string serverName, string normalizedUsername, string configuredTenantId)
        {
            ServerName = serverName;
            NormalizedUsername = normalizedUsername;
            ConfiguredTenantId = configuredTenantId;
        }

        public string ServerName { get; }
        public string NormalizedUsername { get; }
        public string ConfiguredTenantId { get; }
        public string? DiscoveredTenantId { get; set; }
    }

    private sealed class PendingConnectionScope : IDisposable
    {
        private readonly AzureSqlAccessTokenBroker _owner;
        private readonly PendingInteractiveConnection? _previous;
        private bool _disposed;

        public PendingConnectionScope(AzureSqlAccessTokenBroker owner, PendingInteractiveConnection? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _owner._currentPendingConnection.Value = _previous;
            _disposed = true;
        }
    }
}
