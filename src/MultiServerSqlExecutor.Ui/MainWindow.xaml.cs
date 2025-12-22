using System;
using System.Data;
using System.Windows;
using System.Collections.Generic;
using System.Windows.Input;
using MultiServerSqlExecutor.Core.Services;

namespace MultiServerSqlExecutor.Ui;

public partial class MainWindow : Window
{
    public static readonly RoutedUICommand RunCommand = new RoutedUICommand(
        "Run Query",
        "Run",
        typeof(MainWindow),
        new InputGestureCollection { new KeyGesture(Key.F5) }
    );

    private readonly ConfigStore _store = new();
    private readonly SqlExecutor _executor = new();
    private readonly CsvExporter _exporter = new();
    private DataTable? _lastCombined;

    public MainWindow()
    {
        InitializeComponent();
        QueryEditor.Text = "SELECT TOP 10 * FROM INFORMATION_SCHEMA.TABLES;";
    }

    private void OnOpenServers(object sender, RoutedEventArgs e)
    {
        var dlg = new ServersWindow();
        dlg.Owner = this;
        dlg.ShowDialog();
    }

    private void OnRun(object sender, ExecutedRoutedEventArgs e)
    {
        RunQuery();
    }

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        RunQuery();
    }

    private async void RunQuery()
    {
        var servers = _store.Load();
        if (servers.Count == 0)
        {
            MessageBox.Show("No servers configured.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (this.FindName("MenuRun") is System.Windows.Controls.MenuItem mi)
            mi.IsEnabled = false;
        try
        {
            var sql = QueryEditor.Text;
            var results = await _executor.ExecuteOnAllAsync(servers, sql);
            _lastCombined = Combine(results);
            ResultsGrid.ItemsSource = _lastCombined.DefaultView;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Execution Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (this.FindName("MenuRun") is System.Windows.Controls.MenuItem mi2)
                mi2.IsEnabled = true;
        }
    }

    private void OnSaveCombined(object sender, RoutedEventArgs e)
    {
        if (_lastCombined == null)
        {
            MessageBox.Show("No results to save. Run a query first.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = "combined.csv"
        };
        if (dlg.ShowDialog() == true)
        {
            // Convert DataTable to dictionary with single entry for exporter
            var dict = new Dictionary<string, DataTable> { { "Combined", _lastCombined } };
            _exporter.ExportCombined(dict, dlg.FileName);
            MessageBox.Show("Saved.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnExit(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private static DataTable Combine(IDictionary<string, DataTable> results)
    {
        var combined = new DataTable();
        combined.Columns.Add("Server");
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
                    newRow[col.ColumnName] = row[col.ColumnName];
                combined.Rows.Add(newRow);
            }
        }
        return combined;
    }
}
