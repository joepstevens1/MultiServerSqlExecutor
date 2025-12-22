using System.Data;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;

namespace MultiServerSqlExecutor.Core.Services;

public class CsvExporter
{
    public void ExportPerServer(IDictionary<string, DataTable> results, string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var kvp in results)
        {
            var path = Path.Combine(directory, SanitizeFileName(kvp.Key) + ".csv");
            WriteDataTableToCsv(kvp.Value, path);
        }
    }

    public void ExportCombined(IDictionary<string, DataTable> results, string outputFile)
    {
        // Combine by unioning columns; add a Server column
        var combined = new DataTable();
        combined.Columns.Add("Server");
        // collect all column names
        var allCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dt in results.Values)
            foreach (DataColumn col in dt.Columns)
                allCols.Add(col.ColumnName);
        foreach (var col in allCols)
            combined.Columns.Add(col);

        foreach (var (server, dt) in results)
        {
            foreach (DataRow row in dt.Rows)
            {
                var newRow = combined.NewRow();
                newRow["Server"] = server;
                foreach (DataColumn col in dt.Columns)
                {
                    newRow[col.ColumnName] = row[col.ColumnName];
                }
                combined.Rows.Add(newRow);
            }
        }

        WriteDataTableToCsv(combined, outputFile);
    }

    private static void WriteDataTableToCsv(DataTable table, string path)
    {
        using var writer = new StreamWriter(path);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true
        };
        using var csv = new CsvWriter(writer, config);
        // headers
        foreach (DataColumn column in table.Columns)
        {
            csv.WriteField(column.ColumnName);
        }
        csv.NextRecord();
        // rows
        foreach (DataRow row in table.Rows)
        {
            foreach (DataColumn column in table.Columns)
            {
                csv.WriteField(row[column]);
            }
            csv.NextRecord();
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
