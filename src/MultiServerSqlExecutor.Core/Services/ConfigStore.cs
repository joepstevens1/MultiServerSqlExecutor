using MultiServerSqlExecutor.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.ObjectModel;

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
            lock (_lock)
            {
                SaveDocumentNoLock(new ConfigDocument());
            }
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
            return LoadDocumentNoLock().Servers;
        }
    }

    public void Save(IReadOnlyList<ServerConnection> servers)
    {
        lock (_lock)
        {
            var doc = LoadDocumentNoLock();
            doc.Servers = servers.ToList();
            doc.Groups = MergeGroups(doc.Groups, CollectGroupsFromServers(doc.Servers));
            SaveDocumentNoLock(doc);
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

    public IReadOnlyList<string> LoadGroups()
    {
        lock (_lock)
        {
            return LoadDocumentNoLock().Groups;
        }
    }

    public ImportMappingProfile LoadImportProfile()
    {
        lock (_lock)
        {
            return CloneImportProfile(LoadDocumentNoLock().ImportSettings.Profile);
        }
    }

    public void SaveImportProfile(ImportMappingProfile profile)
    {
        lock (_lock)
        {
            var doc = LoadDocumentNoLock();
            doc.ImportSettings ??= new ImportSettingsDocument();
            doc.ImportSettings.Profile = CloneImportProfile(profile);
            SaveDocumentNoLock(doc);
        }
    }

    public string? LoadLastSqlFileFolder()
    {
        lock (_lock)
        {
            return LoadDocumentNoLock().LastSqlFileFolder;
        }
    }

    public void SaveLastSqlFileFolder(string? folderPath)
    {
        lock (_lock)
        {
            var doc = LoadDocumentNoLock();
            doc.LastSqlFileFolder = string.IsNullOrWhiteSpace(folderPath)
                ? null
                : folderPath.Trim();
            SaveDocumentNoLock(doc);
        }
    }

    public void AddGroup(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            throw new ArgumentException("Group name is required.", nameof(groupName));

        lock (_lock)
        {
            var doc = LoadDocumentNoLock();
            var normalized = groupName.Trim();
            if (doc.Groups.Any(g => string.Equals(g, normalized, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Group '{normalized}' already exists.");

            doc.Groups.Add(normalized);
            doc.Groups = MergeGroups(doc.Groups, CollectGroupsFromServers(doc.Servers));
            SaveDocumentNoLock(doc);
        }
    }

    public bool RemoveGroup(string groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            return false;

        lock (_lock)
        {
            var doc = LoadDocumentNoLock();
            var removed = doc.Groups.RemoveAll(g => string.Equals(g, groupName, StringComparison.OrdinalIgnoreCase)) > 0;
            foreach (var server in doc.Servers)
            {
                server.Groups.RemoveAll(g => string.Equals(g, groupName, StringComparison.OrdinalIgnoreCase));
            }

            if (removed)
            {
                doc.Groups = MergeGroups(doc.Groups, CollectGroupsFromServers(doc.Servers));
                SaveDocumentNoLock(doc);
            }

            return removed;
        }
    }

    public void SetGroupMembership(string groupName, IEnumerable<string> serverNamesInGroup)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            throw new ArgumentException("Group name is required.", nameof(groupName));

        lock (_lock)
        {
            var doc = LoadDocumentNoLock();
            var normalizedGroup = ResolveOrAddGroupName(doc, groupName.Trim());
            var membership = new HashSet<string>(serverNamesInGroup, StringComparer.OrdinalIgnoreCase);

            foreach (var server in doc.Servers)
            {
                server.Groups ??= new List<string>();
                var isMember = server.Groups.Any(g => string.Equals(g, normalizedGroup, StringComparison.OrdinalIgnoreCase));
                var shouldBeMember = membership.Contains(server.Name);

                if (shouldBeMember && !isMember)
                {
                    server.Groups.Add(normalizedGroup);
                }
                else if (!shouldBeMember && isMember)
                {
                    server.Groups.RemoveAll(g => string.Equals(g, normalizedGroup, StringComparison.OrdinalIgnoreCase));
                }
            }

            doc.Groups = MergeGroups(doc.Groups, CollectGroupsFromServers(doc.Servers));
            SaveDocumentNoLock(doc);
        }
    }

    private ConfigDocument LoadDocumentNoLock()
    {
        if (!File.Exists(_configPath))
            return new ConfigDocument();

        var json = File.ReadAllText(_configPath);
        if (string.IsNullOrWhiteSpace(json))
            return new ConfigDocument();

        try
        {
            var token = JToken.Parse(json);
            ConfigDocument doc;

            if (token.Type == JTokenType.Array)
            {
                doc = new ConfigDocument
                {
                    Servers = token.ToObject<List<ServerConnection>>() ?? new List<ServerConnection>()
                };
            }
            else
            {
                doc = token.ToObject<ConfigDocument>() ?? new ConfigDocument();
            }

            NormalizeDocument(doc);
            return doc;
        }
        catch (JsonException)
        {
            return new ConfigDocument();
        }
    }

    private void SaveDocumentNoLock(ConfigDocument doc)
    {
        NormalizeDocument(doc);
        var json = JsonConvert.SerializeObject(doc, Formatting.Indented);
        File.WriteAllText(_configPath, json);
    }

    private static void NormalizeDocument(ConfigDocument doc)
    {
        doc.Servers ??= new List<ServerConnection>();
        doc.Groups ??= new List<string>();
        doc.ImportSettings ??= new ImportSettingsDocument();
        doc.ImportSettings.Profile ??= ImportMappingProfile.CreateEmpty();
        doc.LastSqlFileFolder = string.IsNullOrWhiteSpace(doc.LastSqlFileFolder)
            ? null
            : doc.LastSqlFileFolder.Trim();

        foreach (var server in doc.Servers)
        {
            server.Name = (server.Name ?? string.Empty).Trim();
            server.Server = (server.Server ?? string.Empty).Trim();
            server.Database = (server.Database ?? string.Empty).Trim();
            server.Username = (server.Username ?? string.Empty).Trim();
            server.TenantId = (server.TenantId ?? string.Empty).Trim();
            server.Groups ??= new List<string>();
            server.Groups = DistinctNonEmpty(server.Groups);
        }

        doc.Groups = MergeGroups(doc.Groups, CollectGroupsFromServers(doc.Servers));
        doc.ImportSettings.Profile = CloneImportProfile(doc.ImportSettings.Profile);
    }

    private static List<string> CollectGroupsFromServers(IEnumerable<ServerConnection> servers)
    {
        var groups = new List<string>();
        foreach (var server in servers)
        {
            if (server.Groups == null) continue;
            groups.AddRange(server.Groups);
        }
        return DistinctNonEmpty(groups);
    }

    private static List<string> MergeGroups(IEnumerable<string> explicitGroups, IEnumerable<string> assignedGroups)
    {
        var merged = new List<string>();
        merged.AddRange(explicitGroups);
        merged.AddRange(assignedGroups);
        return DistinctNonEmpty(merged);
    }

    private static List<string> DistinctNonEmpty(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var value in values)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed)) continue;
            result.Add(trimmed);
        }
        return result;
    }

    private static string ResolveOrAddGroupName(ConfigDocument doc, string groupName)
    {
        var existing = doc.Groups.FirstOrDefault(g => string.Equals(g, groupName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(existing))
            return existing;

        doc.Groups.Add(groupName);
        return groupName;
    }

    private sealed class ConfigDocument
    {
        [JsonProperty("servers")]
        public List<ServerConnection> Servers { get; set; } = new();

        [JsonProperty("groups")]
        public List<string> Groups { get; set; } = new();

        [JsonProperty("lastSqlFileFolder", NullValueHandling = NullValueHandling.Ignore)]
        public string? LastSqlFileFolder { get; set; }

        [JsonProperty("importSettings")]
        public ImportSettingsDocument ImportSettings { get; set; } = new();
    }

    private sealed class ImportSettingsDocument
    {
        [JsonProperty("profile")]
        public ImportMappingProfile Profile { get; set; } = new();
    }

    private static ImportMappingProfile CloneImportProfile(ImportMappingProfile profile)
    {
        var fieldMappings = ImportMappingProfile.CreateEmpty().FieldMappings;
        foreach (var mapping in profile.FieldMappings?
                     .Select(mapping => new ImportFieldMapping
                     {
                         TargetField = mapping.TargetField,
                         SourceMode = mapping.SourceMode,
                         SourceColumn = (mapping.SourceColumn ?? string.Empty).Trim(),
                         FixedValue = (mapping.FixedValue ?? string.Empty).Trim(),
                         ValueMappings = CloneValueMappings(mapping.ValueMappings)
                     })
                     .GroupBy(mapping => mapping.TargetField)
                     .Select(group => group.First())
                     .ToList() ?? new List<ImportFieldMapping>())
        {
            var index = fieldMappings.FindIndex(existing => existing.TargetField == mapping.TargetField);
            if (index >= 0)
            {
                fieldMappings[index] = mapping;
            }
            else
            {
                fieldMappings.Add(mapping);
            }
        }

        return new ImportMappingProfile
        {
            FieldMappings = fieldMappings,
            GroupMappings = profile.GroupMappings?
                .Select(mapping => new ImportGroupMapping
                {
                    SourceMode = mapping.SourceMode,
                    SourceColumn = (mapping.SourceColumn ?? string.Empty).Trim(),
                    FixedValue = (mapping.FixedValue ?? string.Empty).Trim(),
                    ValueMappings = CloneValueMappings(mapping.ValueMappings)
                })
                .Where(mapping => mapping.SourceMode != ImportValueSourceMode.None ||
                                  !string.IsNullOrWhiteSpace(mapping.SourceColumn) ||
                                  !string.IsNullOrWhiteSpace(mapping.FixedValue) ||
                                  mapping.ValueMappings.Count > 0)
                .ToList() ?? new List<ImportGroupMapping>()
        };
    }

    private static List<ImportValueMap> CloneValueMappings(IEnumerable<ImportValueMap>? valueMappings)
    {
        return valueMappings?
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.SourceValue) || !string.IsNullOrWhiteSpace(mapping.TargetValue))
            .GroupBy(mapping => (mapping.SourceValue ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ImportValueMap
            {
                SourceValue = group.First().SourceValue.Trim(),
                TargetValue = (group.First().TargetValue ?? string.Empty).Trim()
            })
            .ToList() ?? new List<ImportValueMap>();
    }
}
