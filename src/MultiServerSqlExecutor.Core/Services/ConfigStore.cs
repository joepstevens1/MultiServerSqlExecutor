using System.Collections.Concurrent;
using MultiServerSqlExecutor.Core.Models;
using Newtonsoft.Json;

namespace MultiServerSqlExecutor.Core.Services;

public class ConfigStore
{
    private readonly string _configPath;
    private readonly object _lock = new();

    public ConfigStore(string? configPath = null)
    {
        _configPath = configPath ?? GetDefaultConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        if (!File.Exists(_configPath))
        {
            Save(new List<ServerConnection>());
        }
    }

    public static string GetDefaultConfigPath()
    {
        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiServerSqlExecutor");
        return Path.Combine(baseDir, "servers.json");
    }

    public IReadOnlyList<ServerConnection> Load()
    {
        lock (_lock)
        {
            var json = File.ReadAllText(_configPath);
            var list = JsonConvert.DeserializeObject<List<ServerConnection>>(json) ?? new List<ServerConnection>();
            return list;
        }
    }

    public void Save(IReadOnlyList<ServerConnection> servers)
    {
        lock (_lock)
        {
            var json = JsonConvert.SerializeObject(servers, Formatting.Indented);
            File.WriteAllText(_configPath, json);
        }
    }

    public void Add(ServerConnection server)
    {
        var list = Load().ToList();
        if (list.Any(s => string.Equals(s.Name, server.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Server with name '{server.Name}' already exists.");
        list.Add(server);
        Save(list);
    }

    public void AddOrUpdate(ServerConnection server)
    {
        var list = Load().ToList();
        var existingIndex = list.FindIndex(s => string.Equals(s.Name, server.Name, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            list[existingIndex] = server;
        }
        else
        {
            list.Add(server);
        }
        Save(list);
    }

    public bool Remove(string name)
    {
        var list = Load().ToList();
        var removed = list.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) Save(list);
        return removed;
    }
}
