using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MultiServerSqlExecutor.Core.Models;

public enum ImportValueSourceMode
{
    None,
    CsvColumn,
    FixedValue,
    PreserveExisting
}

public enum ServerConfigField
{
    Name,
    Server,
    Database,
    Username,
    Password,
    Authentication
}

public sealed class DatabaseImportRequest
{
    public required string CsvPath { get; init; }
    public required ImportMappingProfile Profile { get; init; }
}

public sealed class DatabaseImportPlan
{
    public List<string> AvailableColumns { get; init; } = new();
    public ImportMappingProfile Profile { get; init; } = ImportMappingProfile.CreateEmpty();
    public List<DatabaseImportServerPreview> ServersToImport { get; init; } = new();
    public List<DatabaseImportServerPreview> ServersMissingFromImport { get; init; } = new();
    public HashSet<string> ManagedGroupNames { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int TotalRowsRead { get; init; }
    public int SkippedRows { get; init; }
}

public sealed class ImportMappingProfile
{
    public List<ImportFieldMapping> FieldMappings { get; init; } = new();
    public List<ImportGroupMapping> GroupMappings { get; init; } = new();

    public static ImportMappingProfile CreateEmpty()
    {
        return new ImportMappingProfile
        {
            FieldMappings = Enum.GetValues<ServerConfigField>()
                .Select(field => new ImportFieldMapping
                {
                    TargetField = field
                })
                .ToList()
        };
    }
}

public sealed class DatabaseImportServerPreview
{
    public required string Key { get; init; }
    public required string SourceAddress { get; init; }
    public required ServerConnection Server { get; init; }
    public Dictionary<ServerConfigField, string> SourceFieldValues { get; init; } = new();
    public List<string> SourceGroupValues { get; init; } = new();
    public bool Exists { get; init; }
    public string? MatchedExistingName { get; init; }
}

public sealed class ImportFieldMapping
{
    [JsonConverter(typeof(StringEnumConverter))]
    public ServerConfigField TargetField { get; init; }
    [JsonConverter(typeof(StringEnumConverter))]
    public ImportValueSourceMode SourceMode { get; init; }
    public string SourceColumn { get; init; } = string.Empty;
    public string FixedValue { get; init; } = string.Empty;
    public List<ImportValueMap> ValueMappings { get; init; } = new();
}

public sealed class ImportGroupMapping
{
    [JsonConverter(typeof(StringEnumConverter))]
    public ImportValueSourceMode SourceMode { get; init; }
    public string SourceColumn { get; init; } = string.Empty;
    public string FixedValue { get; init; } = string.Empty;
    public List<ImportValueMap> ValueMappings { get; init; } = new();
}

public sealed class ImportValueMap
{
    public string SourceValue { get; init; } = string.Empty;
    public string TargetValue { get; init; } = string.Empty;
}
