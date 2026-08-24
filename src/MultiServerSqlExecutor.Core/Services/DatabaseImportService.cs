using CsvHelper;
using CsvHelper.Configuration;
using MultiServerSqlExecutor.Core.Models;
using System.Globalization;

namespace MultiServerSqlExecutor.Core.Services;

public sealed class DatabaseImportService
{
    public DatabaseImportPlan Analyze(DatabaseImportRequest request, IReadOnlyList<ServerConnection> existingServers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CsvPath);

        if (!File.Exists(request.CsvPath))
            throw new FileNotFoundException("Import file not found.", request.CsvPath);

        var records = LoadRecords(request.CsvPath);
        if (records.Count == 0)
        {
            return new DatabaseImportPlan
            {
                Profile = request.Profile
            };
        }

        var availableColumns = records[0].Keys
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var normalizedProfile = NormalizeProfile(request.Profile, availableColumns);
        var managedGroupNames = BuildManagedGroupNames(records, normalizedProfile);
        var existingByKey = existingServers
            .GroupBy(GetServerKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var existingByDatabase = existingServers
            .GroupBy(s => s.Database, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var serversToImport = new List<DatabaseImportServerPreview>();
        var importedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedRows = 0;

        foreach (var record in records)
        {
            var projectedServer = ProjectServer(record, normalizedProfile, existingByDatabase);
            if (projectedServer == null)
            {
                skippedRows++;
                continue;
            }

            var key = GetServerKey(projectedServer);
            if (!importedKeys.Add(key))
                continue;

            existingByKey.TryGetValue(key, out var matchedExisting);
            var mergedServer = MergeWithExisting(projectedServer, matchedExisting, managedGroupNames, existingByDatabase);

            serversToImport.Add(new DatabaseImportServerPreview
            {
                Key = key,
                SourceAddress = ResolveSourceValue(record, normalizedProfile, ServerConfigField.Server),
                Server = mergedServer,
                SourceFieldValues = BuildSourceFieldValues(record, normalizedProfile, matchedExisting),
                SourceGroupValues = BuildSourceGroupValues(record, normalizedProfile, matchedExisting),
                Exists = matchedExisting != null,
                MatchedExistingName = matchedExisting?.Name
            });
        }

        var serversMissingFromImport = existingServers
            .Where(server => !importedKeys.Contains(GetServerKey(server)))
            .OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .Select(server => new DatabaseImportServerPreview
            {
                Key = GetServerKey(server),
                SourceAddress = string.Empty,
                Server = CloneServer(server),
                SourceFieldValues = new Dictionary<ServerConfigField, string>(),
                SourceGroupValues = new List<string>(),
                Exists = true,
                MatchedExistingName = server.Name
            })
            .ToList();

        return new DatabaseImportPlan
        {
            AvailableColumns = availableColumns,
            Profile = normalizedProfile,
            ServersToImport = serversToImport.OrderBy(s => s.Server.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            ServersMissingFromImport = serversMissingFromImport,
            ManagedGroupNames = managedGroupNames,
            TotalRowsRead = records.Count,
            SkippedRows = skippedRows
        };
    }

    public IReadOnlyList<ServerConnection> ApplyImport(
        IReadOnlyList<ServerConnection> existingServers,
        DatabaseImportPlan plan,
        IEnumerable<string> keysToRemove)
    {
        var removals = new HashSet<string>(keysToRemove, StringComparer.OrdinalIgnoreCase);
        var results = existingServers
            .Where(server => !removals.Contains(GetServerKey(server)))
            .Select(CloneServer)
            .ToList();

        foreach (var importedServer in plan.ServersToImport)
        {
            var existingIndex = results.FindIndex(s => string.Equals(GetServerKey(s), importedServer.Key, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                results[existingIndex] = CloneServer(importedServer.Server);
            }
            else
            {
                results.Add(CloneServer(importedServer.Server));
            }
        }

        return results
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ImportMappingProfile CreateDefaultProfile(IReadOnlyCollection<string> availableColumns, ImportMappingProfile? storedProfile = null)
    {
        var normalizedStoredProfile = NormalizeProfile(storedProfile ?? ImportMappingProfile.CreateEmpty(), availableColumns);
        var defaults = ImportMappingProfile.CreateEmpty();

        foreach (var field in Enum.GetValues<ServerConfigField>())
        {
            var storedMapping = normalizedStoredProfile.FieldMappings.FirstOrDefault(f => f.TargetField == field);
            if (storedMapping != null && HasConfiguredSource(storedMapping))
            {
                ReplaceFieldMapping(defaults.FieldMappings, CopyFieldMapping(storedMapping));
                continue;
            }

            var exactColumnMatch = availableColumns.FirstOrDefault(c => string.Equals(c, field.ToString(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exactColumnMatch))
            {
                ReplaceFieldMapping(defaults.FieldMappings, new ImportFieldMapping
                {
                    TargetField = field,
                    SourceMode = ImportValueSourceMode.CsvColumn,
                    SourceColumn = exactColumnMatch
                });
            }
        }

        return new ImportMappingProfile
        {
            FieldMappings = defaults.FieldMappings,
            GroupMappings = normalizedStoredProfile.GroupMappings
                .Select(CopyGroupMapping)
                .Where(g => g.SourceMode != ImportValueSourceMode.None)
                .ToList()
        };
    }

    public ImportMappingProfile CreateSuggestedProfile(
        string csvPath,
        IReadOnlyList<ServerConnection> existingServers,
        ImportMappingProfile? storedProfile = null)
    {
        var records = LoadRecords(csvPath);
        if (records.Count == 0)
            return storedProfile ?? ImportMappingProfile.CreateEmpty();

        var availableColumns = records[0].Keys
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var defaults = CreateDefaultProfile(availableColumns, storedProfile);

        foreach (var field in Enum.GetValues<ServerConfigField>())
        {
            var mapping = defaults.FieldMappings.First(f => f.TargetField == field);
            if (mapping.SourceMode != ImportValueSourceMode.None)
                continue;

            var suggestedColumn = SuggestColumn(field, availableColumns, records);
            if (!string.IsNullOrWhiteSpace(suggestedColumn))
            {
                ReplaceFieldMapping(defaults.FieldMappings, WithSuggestedColumn(mapping, suggestedColumn));
            }
        }

        if (!defaults.GroupMappings.Any())
        {
            foreach (var column in SuggestGroupColumns(availableColumns, defaults.FieldMappings))
            {
                defaults.GroupMappings.Add(new ImportGroupMapping
                {
                    SourceMode = ImportValueSourceMode.CsvColumn,
                    SourceColumn = column
                });
            }
        }

        var authenticationMapping = defaults.FieldMappings.First(f => f.TargetField == ServerConfigField.Authentication);
        if (authenticationMapping.SourceMode == ImportValueSourceMode.None)
        {
            var commonAuthentication = existingServers
                .GroupBy(s => s.Authentication)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();

            ReplaceFieldMapping(defaults.FieldMappings, WithFixedValue(authenticationMapping, commonAuthentication.ToString()));
        }

        return defaults;
    }

    private static ImportMappingProfile NormalizeProfile(ImportMappingProfile profile, IReadOnlyCollection<string> availableColumns)
    {
        var result = ImportMappingProfile.CreateEmpty();

        foreach (var field in Enum.GetValues<ServerConfigField>())
        {
            var existing = profile.FieldMappings.FirstOrDefault(f => f.TargetField == field);
            ReplaceFieldMapping(result.FieldMappings, existing == null
                ? new ImportFieldMapping { TargetField = field }
                : NormalizeFieldMapping(existing, availableColumns));
        }

        return new ImportMappingProfile
        {
            FieldMappings = result.FieldMappings,
            GroupMappings = profile.GroupMappings
                .Select(g => NormalizeGroupMapping(g, availableColumns))
                .Where(g => g.SourceMode != ImportValueSourceMode.None)
                .ToList()
        };
    }

    private static ImportFieldMapping NormalizeFieldMapping(ImportFieldMapping mapping, IReadOnlyCollection<string> availableColumns)
    {
        var normalizedColumn = ResolveColumnName(mapping.SourceColumn, availableColumns);
        var sourceMode = mapping.SourceMode == ImportValueSourceMode.CsvColumn && string.IsNullOrWhiteSpace(normalizedColumn)
            ? ImportValueSourceMode.None
            : mapping.SourceMode;

        return new ImportFieldMapping
        {
            TargetField = mapping.TargetField,
            SourceMode = sourceMode,
            SourceColumn = normalizedColumn,
            FixedValue = (mapping.FixedValue ?? string.Empty).Trim(),
            ValueMappings = NormalizeValueMappings(mapping.ValueMappings)
        };
    }

    private static ImportGroupMapping NormalizeGroupMapping(ImportGroupMapping mapping, IReadOnlyCollection<string> availableColumns)
    {
        var normalizedColumn = ResolveColumnName(mapping.SourceColumn, availableColumns);
        var sourceMode = mapping.SourceMode == ImportValueSourceMode.CsvColumn && string.IsNullOrWhiteSpace(normalizedColumn)
            ? ImportValueSourceMode.None
            : mapping.SourceMode;

        return new ImportGroupMapping
        {
            SourceMode = sourceMode,
            SourceColumn = normalizedColumn,
            FixedValue = (mapping.FixedValue ?? string.Empty).Trim(),
            ValueMappings = NormalizeValueMappings(mapping.ValueMappings)
        };
    }

    private static List<ImportValueMap> NormalizeValueMappings(IEnumerable<ImportValueMap> mappings)
    {
        return mappings
            .Where(m => !string.IsNullOrWhiteSpace(m.SourceValue) || !string.IsNullOrWhiteSpace(m.TargetValue))
            .GroupBy(m => (m.SourceValue ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ImportValueMap
            {
                SourceValue = g.First().SourceValue.Trim(),
                TargetValue = (g.First().TargetValue ?? string.Empty).Trim()
            })
            .ToList();
    }

    private static ServerConnection? ProjectServer(
        IReadOnlyDictionary<string, string> record,
        ImportMappingProfile profile,
        IReadOnlyDictionary<string, List<ServerConnection>> existingByDatabase)
    {
        var database = ResolveFieldValue(record, profile, ServerConfigField.Database);
        var template = string.IsNullOrWhiteSpace(database) || !existingByDatabase.TryGetValue(database, out var matches)
            ? null
            : matches.FirstOrDefault();

        var name = ResolveFieldValue(record, profile, ServerConfigField.Name, template);
        var server = ResolveFieldValue(record, profile, ServerConfigField.Server, template);
        database = ResolveFieldValue(record, profile, ServerConfigField.Database, template);

        if (string.IsNullOrWhiteSpace(name))
        {
            name = template?.Name;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = database;
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
            return null;

        var groups = profile.GroupMappings
            .Select(mapping => ResolveGroupValue(record, mapping, template))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ServerConnection
        {
            Name = name,
            Server = server,
            Database = database,
            Username = ResolveFieldValue(record, profile, ServerConfigField.Username, template),
            Password = ResolveFieldValue(record, profile, ServerConfigField.Password, template),
            TenantId = template?.TenantId ?? string.Empty,
            Authentication = ResolveAuthentication(record, profile, template),
            Groups = groups
        };
    }

    private static ServerConnection MergeWithExisting(
        ServerConnection projectedServer,
        ServerConnection? matchedExisting,
        HashSet<string> managedGroupNames,
        IReadOnlyDictionary<string, List<ServerConnection>> existingByDatabase)
    {
        var template = matchedExisting;
        if (template == null && existingByDatabase.TryGetValue(projectedServer.Database, out var matches))
        {
            template = matches.FirstOrDefault();
        }

        var preservedGroups = template?.Groups?
            .Where(g => !managedGroupNames.Contains(g))
            .ToList() ?? new List<string>();

        return new ServerConnection
        {
            Name = projectedServer.Name,
            Server = projectedServer.Server,
            Database = projectedServer.Database,
            Username = string.IsNullOrWhiteSpace(projectedServer.Username) ? template?.Username ?? string.Empty : projectedServer.Username,
            Password = string.IsNullOrWhiteSpace(projectedServer.Password) ? template?.Password ?? string.Empty : projectedServer.Password,
            TenantId = template?.TenantId ?? string.Empty,
            Authentication = projectedServer.Authentication,
            Groups = preservedGroups
                .Concat(projectedServer.Groups)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static HashSet<string> BuildManagedGroupNames(IEnumerable<IReadOnlyDictionary<string, string>> records, ImportMappingProfile profile)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            foreach (var groupMapping in profile.GroupMappings)
            {
                var value = ResolveGroupValue(record, groupMapping, null);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value);
                }
            }
        }

        return result;
    }

    private static string ResolveFieldValue(
        IReadOnlyDictionary<string, string> record,
        ImportMappingProfile profile,
        ServerConfigField field,
        ServerConnection? existingServer = null)
    {
        var mapping = profile.FieldMappings.First(f => f.TargetField == field);
        return ResolveMappedValue(record, mapping.SourceMode, mapping.SourceColumn, mapping.FixedValue, mapping.ValueMappings, GetExistingValue(existingServer, field));
    }

    private static AuthType ResolveAuthentication(
        IReadOnlyDictionary<string, string> record,
        ImportMappingProfile profile,
        ServerConnection? existingServer)
    {
        var rawValue = ResolveFieldValue(record, profile, ServerConfigField.Authentication, existingServer);
        if (Enum.TryParse<AuthType>(rawValue, true, out var parsed))
            return parsed;

        return existingServer?.Authentication ?? AuthType.SqlPassword;
    }

    private static string ResolveGroupValue(
        IReadOnlyDictionary<string, string> record,
        ImportGroupMapping mapping,
        ServerConnection? existingServer)
    {
        return ResolveMappedValue(record, mapping.SourceMode, mapping.SourceColumn, mapping.FixedValue, mapping.ValueMappings, string.Empty);
    }

    private static string ResolveMappedValue(
        IReadOnlyDictionary<string, string> record,
        ImportValueSourceMode sourceMode,
        string sourceColumn,
        string fixedValue,
        IReadOnlyCollection<ImportValueMap> valueMappings,
        string existingValue)
    {
        var rawValue = sourceMode switch
        {
            ImportValueSourceMode.CsvColumn => GetValue(record, sourceColumn),
            ImportValueSourceMode.FixedValue => fixedValue,
            ImportValueSourceMode.PreserveExisting => existingValue,
            _ => string.Empty
        };

        var valueMap = valueMappings.FirstOrDefault(m => string.Equals(m.SourceValue, rawValue, StringComparison.OrdinalIgnoreCase));
        return valueMap?.TargetValue ?? rawValue.Trim();
    }

    private static string GetExistingValue(ServerConnection? server, ServerConfigField field)
    {
        if (server == null)
            return string.Empty;

        return field switch
        {
            ServerConfigField.Name => server.Name,
            ServerConfigField.Server => server.Server,
            ServerConfigField.Database => server.Database,
            ServerConfigField.Username => server.Username,
            ServerConfigField.Password => server.Password,
            ServerConfigField.Authentication => server.Authentication.ToString(),
            _ => string.Empty
        };
    }

    private static bool HasConfiguredSource(ImportFieldMapping mapping)
    {
        return mapping.SourceMode != ImportValueSourceMode.None ||
               !string.IsNullOrWhiteSpace(mapping.SourceColumn) ||
               !string.IsNullOrWhiteSpace(mapping.FixedValue) ||
               mapping.ValueMappings.Count > 0;
    }

    private static bool HasConfiguredMappings(ImportMappingProfile profile)
    {
        return profile.FieldMappings.Any(HasConfiguredSource) ||
               profile.GroupMappings.Any(mapping =>
                   mapping.SourceMode != ImportValueSourceMode.None ||
                   !string.IsNullOrWhiteSpace(mapping.SourceColumn) ||
                   !string.IsNullOrWhiteSpace(mapping.FixedValue) ||
                   mapping.ValueMappings.Count > 0);
    }

    private static void ReplaceFieldMapping(List<ImportFieldMapping> mappings, ImportFieldMapping replacement)
    {
        var index = mappings.FindIndex(m => m.TargetField == replacement.TargetField);
        if (index >= 0)
        {
            mappings[index] = replacement;
        }
        else
        {
            mappings.Add(replacement);
        }
    }

    private static ImportFieldMapping WithSuggestedColumn(ImportFieldMapping mapping, string sourceColumn)
    {
        return new ImportFieldMapping
        {
            TargetField = mapping.TargetField,
            SourceMode = ImportValueSourceMode.CsvColumn,
            SourceColumn = sourceColumn,
            FixedValue = mapping.FixedValue,
            ValueMappings = mapping.ValueMappings.Select(CopyValueMap).ToList()
        };
    }

    private static ImportFieldMapping WithFixedValue(ImportFieldMapping mapping, string fixedValue)
    {
        return new ImportFieldMapping
        {
            TargetField = mapping.TargetField,
            SourceMode = ImportValueSourceMode.FixedValue,
            SourceColumn = mapping.SourceColumn,
            FixedValue = fixedValue,
            ValueMappings = mapping.ValueMappings.Select(CopyValueMap).ToList()
        };
    }

    private static ImportFieldMapping CopyFieldMapping(ImportFieldMapping mapping)
    {
        return new ImportFieldMapping
        {
            TargetField = mapping.TargetField,
            SourceMode = mapping.SourceMode,
            SourceColumn = mapping.SourceColumn,
            FixedValue = mapping.FixedValue,
            ValueMappings = mapping.ValueMappings.Select(CopyValueMap).ToList()
        };
    }

    private static ImportGroupMapping CopyGroupMapping(ImportGroupMapping mapping)
    {
        return new ImportGroupMapping
        {
            SourceMode = mapping.SourceMode,
            SourceColumn = mapping.SourceColumn,
            FixedValue = mapping.FixedValue,
            ValueMappings = mapping.ValueMappings.Select(CopyValueMap).ToList()
        };
    }

    private static ImportValueMap CopyValueMap(ImportValueMap valueMap)
    {
        return new ImportValueMap
        {
            SourceValue = valueMap.SourceValue,
            TargetValue = valueMap.TargetValue
        };
    }

    private static string ResolveColumnName(string configuredColumn, IReadOnlyCollection<string> availableColumns)
    {
        return availableColumns.FirstOrDefault(c => string.Equals(c, configuredColumn, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    }

    private static string SuggestColumn(
        ServerConfigField field,
        IReadOnlyCollection<string> availableColumns,
        IReadOnlyList<Dictionary<string, string>> records)
    {
        var candidates = availableColumns
            .Select(column => new
            {
                Column = column,
                Score = ScoreColumn(field, column, records)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Column.Length)
            .ThenBy(x => x.Column, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates.FirstOrDefault()?.Column ?? string.Empty;
    }

    private static double ScoreColumn(
        ServerConfigField field,
        string column,
        IReadOnlyList<Dictionary<string, string>> records)
    {
        var normalizedColumn = NormalizeToken(column);
        var normalizedField = NormalizeToken(field.ToString());
        double score = 0;

        if (string.Equals(normalizedColumn, normalizedField, StringComparison.OrdinalIgnoreCase))
            score += 100;

        if (normalizedColumn.StartsWith(normalizedField, StringComparison.OrdinalIgnoreCase))
            score += 45;

        if (normalizedColumn.Contains(normalizedField, StringComparison.OrdinalIgnoreCase))
            score += 30;

        if (field == ServerConfigField.Server)
        {
            score += ScoreServerLikeValues(column, records);
            if (normalizedColumn.Contains("address", StringComparison.OrdinalIgnoreCase) ||
                normalizedColumn.Contains("host", StringComparison.OrdinalIgnoreCase) ||
                normalizedColumn.Contains("endpoint", StringComparison.OrdinalIgnoreCase))
            {
                score += 35;
            }
        }

        if (field == ServerConfigField.Database)
        {
            score += ScoreDatabaseLikeValues(column, records);
        }

        if (field == ServerConfigField.Name)
        {
            if (normalizedColumn.Contains("database", StringComparison.OrdinalIgnoreCase))
                score -= 15;

            if (normalizedColumn.Contains("server", StringComparison.OrdinalIgnoreCase))
                score -= 15;

            score += ScoreNameLikeValues(column, records);
        }

        if (field == ServerConfigField.Authentication)
        {
            if (normalizedColumn.Contains("auth", StringComparison.OrdinalIgnoreCase))
                score += 30;

            if (normalizedColumn.Contains("login", StringComparison.OrdinalIgnoreCase))
                score += 15;
        }

        return score;
    }

    private static double ScoreServerLikeValues(string column, IReadOnlyList<Dictionary<string, string>> records)
    {
        var values = records.Select(r => GetValue(r, column)).Where(v => !string.IsNullOrWhiteSpace(v)).Take(20).ToList();
        if (values.Count == 0)
            return 0;

        var hits = values.Count(v =>
            v.Contains('.') ||
            v.Contains('\\') ||
            v.Contains(':') ||
            v.Contains('/') ||
            v.EndsWith("net", StringComparison.OrdinalIgnoreCase) ||
            v.EndsWith("com", StringComparison.OrdinalIgnoreCase));

        return (double)hits / values.Count * 40;
    }

    private static double ScoreDatabaseLikeValues(string column, IReadOnlyList<Dictionary<string, string>> records)
    {
        var values = records.Select(r => GetValue(r, column)).Where(v => !string.IsNullOrWhiteSpace(v)).Take(20).ToList();
        if (values.Count == 0)
            return 0;

        var hits = values.Count(v => v.Contains("db", StringComparison.OrdinalIgnoreCase) || v.Contains("database", StringComparison.OrdinalIgnoreCase));
        return (double)hits / values.Count * 20;
    }

    private static double ScoreNameLikeValues(string column, IReadOnlyList<Dictionary<string, string>> records)
    {
        var distinctCount = records.Select(r => GetValue(r, column)).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return distinctCount > 1 ? 5 : 0;
    }

    private static IEnumerable<string> SuggestGroupColumns(
        IReadOnlyCollection<string> availableColumns,
        IReadOnlyCollection<ImportFieldMapping> fieldMappings)
    {
        var usedColumns = fieldMappings
            .Where(m => m.SourceMode == ImportValueSourceMode.CsvColumn)
            .Select(m => m.SourceColumn)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return availableColumns
            .Where(column => !usedColumns.Contains(column))
            .Where(column =>
            {
                var normalized = NormalizeToken(column);
                return normalized.Contains("environment", StringComparison.OrdinalIgnoreCase) ||
                       normalized.Contains("service", StringComparison.OrdinalIgnoreCase) ||
                       normalized.Contains("group", StringComparison.OrdinalIgnoreCase) ||
                       normalized.Contains("set", StringComparison.OrdinalIgnoreCase);
            })
            .Take(3)
            .ToList();
    }

    private static string NormalizeToken(string value)
    {
        return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
    }

    private static string ResolveSourceValue(
        IReadOnlyDictionary<string, string> record,
        ImportMappingProfile profile,
        ServerConfigField field,
        ServerConnection? existingServer = null)
    {
        var mapping = profile.FieldMappings.First(f => f.TargetField == field);
        return mapping.SourceMode switch
        {
            ImportValueSourceMode.CsvColumn => GetValue(record, mapping.SourceColumn),
            ImportValueSourceMode.FixedValue => mapping.FixedValue,
            ImportValueSourceMode.PreserveExisting => GetExistingValue(existingServer, field),
            _ => string.Empty
        };
    }

    private static Dictionary<ServerConfigField, string> BuildSourceFieldValues(
        IReadOnlyDictionary<string, string> record,
        ImportMappingProfile profile,
        ServerConnection? existingServer)
    {
        return Enum.GetValues<ServerConfigField>()
            .ToDictionary(
                field => field,
                field => ResolveSourceValue(record, profile, field, existingServer));
    }

    private static List<string> BuildSourceGroupValues(
        IReadOnlyDictionary<string, string> record,
        ImportMappingProfile profile,
        ServerConnection? existingServer)
    {
        return profile.GroupMappings
            .Select(mapping => ResolveGroupSourceValue(record, mapping, existingServer))
            .ToList();
    }

    private static string ResolveGroupSourceValue(
        IReadOnlyDictionary<string, string> record,
        ImportGroupMapping mapping,
        ServerConnection? existingServer)
    {
        return mapping.SourceMode switch
        {
            ImportValueSourceMode.CsvColumn => GetValue(record, mapping.SourceColumn),
            ImportValueSourceMode.FixedValue => mapping.FixedValue,
            ImportValueSourceMode.PreserveExisting => string.Empty,
            _ => string.Empty
        };
    }

    private static List<Dictionary<string, string>> LoadRecords(string csvPath)
    {
        using var reader = new StreamReader(csvPath);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            IgnoreBlankLines = true,
            MissingFieldFound = null,
            HeaderValidated = null
        });

        if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord == null)
            return new List<Dictionary<string, string>>();

        var headers = csv.HeaderRecord.ToList();
        var records = new List<Dictionary<string, string>>();

        while (csv.Read())
        {
            var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                record[header] = csv.GetField(header)?.Trim() ?? string.Empty;
            }

            records.Add(record);
        }

        return records;
    }

    private static string GetValue(IReadOnlyDictionary<string, string> record, string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
            return string.Empty;

        return record.TryGetValue(columnName, out var value)
            ? (value ?? string.Empty).Trim()
            : string.Empty;
    }

    private static string GetServerKey(ServerConnection server)
    {
        return $"{server.Server.Trim()}|{server.Database.Trim()}";
    }

    private static ServerConnection CloneServer(ServerConnection server)
    {
        return new ServerConnection
        {
            Name = server.Name,
            Server = server.Server,
            Database = server.Database,
            Username = server.Username,
            Password = server.Password,
            TenantId = server.TenantId,
            Authentication = server.Authentication,
            Groups = server.Groups?.ToList() ?? new List<string>()
        };
    }
}
