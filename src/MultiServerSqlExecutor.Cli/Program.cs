using MultiServerSqlExecutor.Core.Models;
using MultiServerSqlExecutor.Core.Services;

namespace MultiServerSqlExecutor.Cli;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            // No args: try to start UI if available
            try
            {
                var uiExe = Path.Combine(AppContext.BaseDirectory, "..", "MultiServerSqlExecutor.Ui", "MultiServerSqlExecutor.Ui.exe");
                uiExe = Path.GetFullPath(uiExe);
                if (File.Exists(uiExe))
                {
                    var proc = new System.Diagnostics.Process();
                    proc.StartInfo.FileName = uiExe;
                    proc.Start();
                    return 0;
                }
            }
            catch
            {
                // ignore and fall through to help
            }

            PrintHelp();
            return 0;
        }

        var store = new ConfigStore();
        switch (args[0].ToLowerInvariant())
        {
            case "add-server":
                return AddServer(args.Skip(1).ToArray(), store);
            case "remove-server":
                return RemoveServer(args.Skip(1).ToArray(), store);
            case "list-servers":
                return ListServers(store);
            case "execute-query":
                return await ExecuteQueryAsync(args.Skip(1).ToArray(), store);
            case "help":
            case "-h":
            case "--help":
                PrintHelp();
                return 0;
            default:
                Console.Error.WriteLine($"Unknown command: {args[0]}");
                PrintHelp();
                return 1;
        }
    }

    private static int AddServer(string[] args, ConfigStore store)
    {
        var dict = ParseArgs(args);
        if (!dict.TryGetValue("name", out var name) ||
            !dict.TryGetValue("server", out var server) ||
            !dict.TryGetValue("database", out var database) ||
            !dict.TryGetValue("username", out var username) ||
            !dict.TryGetValue("password", out var password))
        {
            Console.Error.WriteLine("Usage: add-server --name <name> --server <server> --database <db> --username <user> --password <pass>");
            return 2;
        }

        try
        {
            store.Add(new ServerConnection
            {
                Name = name,
                Server = server,
                Database = database,
                Username = username,
                Password = password
            });
            Console.WriteLine($"Added server '{name}'.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
    }

    private static int RemoveServer(string[] args, ConfigStore store)
    {
        var dict = ParseArgs(args);
        if (!dict.TryGetValue("name", out var name))
        {
            Console.Error.WriteLine("Usage: remove-server --name <name>");
            return 2;
        }
        var removed = store.Remove(name);
        if (removed) Console.WriteLine($"Removed server '{name}'.");
        else Console.WriteLine($"No server named '{name}' was found.");
        return 0;
    }

    private static int ListServers(ConfigStore store)
    {
        var list = store.Load();
        if (list.Count == 0)
        {
            Console.WriteLine("No servers configured. Use 'add-server' to add one.");
            return 0;
        }
        foreach (var s in list)
        {
            Console.WriteLine($"- {s.Name}: {s.Server}; DB={s.Database}; User={s.Username}");
        }
        return 0;
    }

    private static async Task<int> ExecuteQueryAsync(string[] args, ConfigStore store)
    {
        var dict = ParseArgs(args);
        if (!dict.TryGetValue("queryfile", out var queryFile) ||
            !dict.TryGetValue("outputfile", out var outputFile))
        {
            Console.Error.WriteLine("Usage: execute-query --queryFile <path> --outputFile <path> [--perServerDir <dir>]");
            return 2;
        }
        if (!File.Exists(queryFile))
        {
            Console.Error.WriteLine($"Query file not found: {queryFile}");
            return 4;
        }
        var sql = await File.ReadAllTextAsync(queryFile);
        var servers = store.Load();
        if (servers.Count == 0)
        {
            Console.Error.WriteLine("No servers configured.");
            return 5;
        }

        var exec = new SqlExecutor();
        var exporter = new CsvExporter();
        Console.WriteLine($"Executing query on {servers.Count} server(s)...");
        try
        {
            var results = await exec.ExecuteOnAllAsync(servers, sql);
            if (dict.TryGetValue("perserverdir", out var perServerDir))
            {
                exporter.ExportPerServer(results, perServerDir);
                Console.WriteLine($"Per-server results saved to: {perServerDir}");
            }
            exporter.ExportCombined(results, outputFile);
            Console.WriteLine($"Combined results saved to: {outputFile}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error executing query: " + ex.Message);
            return 6;
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                var key = a.Substring(2);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    dict[key] = args[++i];
                }
                else
                {
                    dict[key] = "true";
                }
            }
        }
        return dict;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Multi-Server SQL Executor CLI");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  add-server --name <name> --server <server> --database <db> --username <user> --password <pass>");
        Console.WriteLine("  remove-server --name <name>");
        Console.WriteLine("  list-servers");
        Console.WriteLine("  execute-query --queryFile <path> --outputFile <path> [--perServerDir <dir>]");
    }
}
