using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using MultiServerSqlExecutor.Core.Models;
using MultiServerSqlExecutor.Core.Services;

namespace MultiServerSqlExecutor.Ui;

public partial class MainWindow : Window
{
    private const string UnassignedGroupOption = "(Unassigned)";
    private const string DefaultQueryHeader = "SQL Query";
    private const string SqlFileDialogFilter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*";
    private const string DefaultQueryFileName = "query.sql";
    private const string DefaultQueryText = "SELECT\n\tGetUtcDate() as QueryTime,\n\t@@SERVERNAME as ServerName,\t\n\tDB_NAME() AS DatabaseName;";

    public static readonly RoutedUICommand OpenSqlFileCommand = new RoutedUICommand(
        "Open SQL File",
        "OpenSqlFile",
        typeof(MainWindow),
        new InputGestureCollection { new KeyGesture(Key.O, ModifierKeys.Control) }
    );

    public static readonly RoutedUICommand SaveSqlFileCommand = new RoutedUICommand(
        "Save SQL File",
        "SaveSqlFile",
        typeof(MainWindow),
        new InputGestureCollection { new KeyGesture(Key.S, ModifierKeys.Control) }
    );

    public static readonly RoutedUICommand SaveSqlFileAsCommand = new RoutedUICommand(
        "Save SQL File As",
        "SaveSqlFileAs",
        typeof(MainWindow),
        new InputGestureCollection { new KeyGesture(Key.S, ModifierKeys.Control | ModifierKeys.Shift) }
    );

    public static readonly RoutedUICommand RunCommand = new RoutedUICommand(
        "Run Query",
        "Run",
        typeof(MainWindow),
        new InputGestureCollection { new KeyGesture(Key.F5) }
    );

    private readonly ConfigStore _store = new();
    private readonly SqlExecutor _executor = new();
    private readonly CsvExporter _exporter = new();
    private readonly DispatcherTimer _runTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HashSet<string> _selectedGroupFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<DatabaseExecutionItem> _databaseStatuses = new();
    private readonly Dictionary<string, DatabaseExecutionItem> _databaseStatusByServerName = new(StringComparer.OrdinalIgnoreCase);
    private GridLength _sidebarExpandedWidth = new(280, GridUnitType.Pixel);
    private DataTable? _lastCombined;
    private string? _currentQueryFilePath;
    private bool _isQueryDirty;
    private DateTime _runStartUtc;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        _runTimer.Tick += OnRunTimerTick;
        DatabaseStatusList.ItemsSource = _databaseStatuses;
        QueryEditor.Text = DefaultQueryText;
        QueryEditor.TextChanged += OnQueryEditorTextChanged;
        UpdateQueryHeader();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSqlHighlighting();
        LoadGroupFilterMenu();
    }

    private void LoadSqlHighlighting()
    {
        try
        {
            var executingAssembly = Assembly.GetExecutingAssembly();
            using var stream = executingAssembly
                .GetManifestResourceStream("MultiServerSqlExecutor.Ui.SQL.xshd");
            if (stream == null)
            {
                return;
            }

            using XmlReader reader = new XmlTextReader(stream);
            QueryEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading highlighting: {ex.Message}");
        }
    }

    private void OnOpenServers(object sender, RoutedEventArgs e)
    {
        var dlg = new ServersWindow();
        dlg.Owner = this;
        dlg.ShowDialog();
        LoadGroupFilterMenu();
    }

    private void OnOpenGroups(object sender, RoutedEventArgs e)
    {
        var dlg = new GroupsWindow();
        dlg.Owner = this;
        dlg.ShowDialog();
        LoadGroupFilterMenu();
    }

    private void OnRefreshGroups(object sender, RoutedEventArgs e)
    {
        LoadGroupFilterMenu();
    }

    private void OnClearGroupSelection(object sender, RoutedEventArgs e)
    {
        _selectedGroupFilters.Clear();
        UpdateGroupFilterSummary();
        UpdateSelectedServerCount();
        BuildGroupFilterItems();
        RefreshSelectedDatabaseStatuses();
    }

    private void OnGroupFilterMenuOpened(object sender, RoutedEventArgs e)
    {
        BuildGroupFilterItems();
    }

    private void OnRun(object sender, ExecutedRoutedEventArgs e)
    {
        RunQuery();
    }

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        RunQuery();
    }

    private void OnOpenSqlFile(object sender, ExecutedRoutedEventArgs e)
    {
        OpenSqlFile();
    }

    private void OnSaveSqlFile(object sender, ExecutedRoutedEventArgs e)
    {
        SaveSqlFile();
    }

    private void OnSaveSqlFileAs(object sender, ExecutedRoutedEventArgs e)
    {
        SaveSqlFileAs();
    }

    private async void RunQuery()
    {
        _lastCombined = null;
        ResultsGrid.ItemsSource = null;

        var servers = GetSelectedServers();

        if (servers.Count == 0)
        {
            var message = _selectedGroupFilters.Count == 0
                ? "No servers configured."
                : "No servers matched the selected group(s).";
            MessageBox.Show(message, "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            SetRunStats(0, 0, 0);
            return;
        }

        PrepareDatabaseStatusesForRun(servers);

        if (this.FindName("MenuRun") is MenuItem mi)
            mi.IsEnabled = false;

        StartRunStats(servers.Count);
        try
        {
            var sql = QueryEditor.Text;
            var executionResults = await _executor.ExecuteOnAllWithStatusAsync(
                servers,
                sql,
                progressCallback: OnRunProgress,
                statusCallback: OnServerExecutionStatusUpdated);

            var successfulResults = executionResults
                .Where(r => r.Succeeded)
                .ToDictionary(r => r.Server.Name, r => r.Data!);

            if (successfulResults.Count > 0)
            {
                _lastCombined = Combine(successfulResults);
                ResultsGrid.ItemsSource = _lastCombined.DefaultView;
            }

            var failures = executionResults.Where(r => !r.Succeeded).ToList();
            if (failures.Count > 0)
            {
                var failedNames = string.Join(", ", failures.Select(f => f.Server.Name));
                MessageBox.Show(
                    $"Query completed with errors on {failures.Count} database(s): {failedNames}",
                    "Execution Completed With Errors",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Execution Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StopRunStats();
            if (this.FindName("MenuRun") is MenuItem mi2)
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
            var dict = new Dictionary<string, DataTable> { { "Combined", _lastCombined } };
            _exporter.ExportCombined(dict, dlg.FileName);
            MessageBox.Show("Saved.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnExit(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!PromptToSaveChangesIfNeeded())
        {
            e.Cancel = true;
        }
    }

    private static DataTable Combine(IDictionary<string, DataTable> results)
    {
        var combined = new DataTable();
        combined.Columns.Add("Server", typeof(string));

        var combinedColumnTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var dt in results.Values)
        {
            foreach (DataColumn col in dt.Columns)
            {
                if (combinedColumnTypes.TryGetValue(col.ColumnName, out var existingType))
                {
                    if (existingType != col.DataType)
                    {
                        combinedColumnTypes[col.ColumnName] = typeof(object);
                    }
                }
                else
                {
                    combinedColumnTypes[col.ColumnName] = col.DataType;
                }
            }
        }

        foreach (var (columnName, columnType) in combinedColumnTypes)
        {
            combined.Columns.Add(columnName, columnType);
        }

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

    private void LoadGroupFilterMenu()
    {
        var groups = _store.LoadGroups()
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();
        groups.Insert(0, UnassignedGroupOption);

        _selectedGroupFilters.RemoveWhere(g => !groups.Any(x => string.Equals(x, g, StringComparison.OrdinalIgnoreCase)));

        UpdateGroupFilterSummary();
        UpdateSelectedServerCount();
        BuildGroupFilterItems();
        RefreshSelectedDatabaseStatuses();
    }

    private void UpdateGroupFilterSummary()
    {
        var selected = _selectedGroupFilters.ToList();
        if (selected.Count == 0)
        {
            MenuGroupFilter.Header = "Group _Filter (All)";
            return;
        }

        if (selected.Count == 1)
        {
            MenuGroupFilter.Header = $"Group _Filter ({selected[0]})";
            return;
        }

        MenuGroupFilter.Header = $"Group _Filter ({selected.Count} selected)";
    }

    private void BuildGroupFilterItems()
    {
        var staticItems = MenuGroupFilter.Items.Cast<object>().Take(3).ToList();
        MenuGroupFilter.Items.Clear();
        foreach (var item in staticItems)
        {
            MenuGroupFilter.Items.Add(item);
        }

        var groups = _store.LoadGroups()
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();
        groups.Insert(0, UnassignedGroupOption);

        foreach (var group in groups)
        {
            var menuItem = new MenuItem
            {
                Header = group,
                IsCheckable = true,
                IsChecked = _selectedGroupFilters.Contains(group),
                StaysOpenOnClick = true,
                Tag = group
            };
            menuItem.Click += OnGroupFilterItemClick;
            MenuGroupFilter.Items.Add(menuItem);
        }
    }

    private void OnGroupFilterItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string groupName } item)
            return;

        if (item.IsChecked)
        {
            _selectedGroupFilters.Add(groupName);
        }
        else
        {
            _selectedGroupFilters.RemoveWhere(g => string.Equals(g, groupName, StringComparison.OrdinalIgnoreCase));
        }

        UpdateGroupFilterSummary();
        UpdateSelectedServerCount();
        RefreshSelectedDatabaseStatuses();
    }

    private void UpdateSelectedServerCount()
    {
        TxtSelectedServers.Text = GetSelectedServers().Count.ToString();
    }

    private List<ServerConnection> GetSelectedServers()
    {
        var servers = _store.Load();
        var selectedGroups = _selectedGroupFilters.ToList();

        if (selectedGroups.Count > 0)
        {
            var includeUnassigned = selectedGroups.Any(g => string.Equals(g, UnassignedGroupOption, StringComparison.OrdinalIgnoreCase));
            var selectedGroupSet = new HashSet<string>(
                selectedGroups.Where(g => !string.Equals(g, UnassignedGroupOption, StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);

            servers = servers
                .Where(s =>
                {
                    var hasNoGroups = s.Groups == null || s.Groups.Count == 0;
                    var inSelectedGroup = s.Groups?.Any(g => selectedGroupSet.Contains(g)) == true;
                    return inSelectedGroup || (includeUnassigned && hasNoGroups);
                })
                .ToList();
        }

        return servers
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private void RefreshSelectedDatabaseStatuses()
    {
        var servers = GetSelectedServers();
        _databaseStatuses.Clear();
        _databaseStatusByServerName.Clear();

        foreach (var server in servers)
        {
            var item = new DatabaseExecutionItem(server);
            _databaseStatuses.Add(item);
            _databaseStatusByServerName[server.Name] = item;
        }
    }

    private void PrepareDatabaseStatusesForRun(IReadOnlyList<ServerConnection> servers)
    {
        _databaseStatuses.Clear();
        _databaseStatusByServerName.Clear();

        foreach (var server in servers)
        {
            var item = new DatabaseExecutionItem(server);
            item.SetStatus(QueryExecutionStatus.NotStarted, null);
            _databaseStatuses.Add(item);
            _databaseStatusByServerName[server.Name] = item;
        }
    }

    private void OnServerExecutionStatusUpdated(ServerExecutionStatusUpdate update)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyServerStatusUpdate(update);
        }
        else
        {
            Dispatcher.Invoke(() => ApplyServerStatusUpdate(update));
        }
    }

    private void ApplyServerStatusUpdate(ServerExecutionStatusUpdate update)
    {
        if (_databaseStatusByServerName.TryGetValue(update.Server.Name, out var item))
        {
            item.SetStatus(update.Status, update.ErrorMessage);
        }
    }

    private void StartRunStats(int totalServers)
    {
        _runStartUtc = DateTime.UtcNow;
        SetRunStats(totalServers, totalServers, 0);
        UpdateElapsed();
        _runTimer.Start();
    }

    private void StopRunStats()
    {
        _runTimer.Stop();
    }

    private void OnRunProgress(int total, int pending, int completed)
    {
        if (Dispatcher.CheckAccess())
        {
            SetRunStats(total, pending, completed);
        }
        else
        {
            Dispatcher.Invoke(() => SetRunStats(total, pending, completed));
        }
    }

    private void SetRunStats(int total, int pending, int completed)
    {
        TxtTotalServers.Text = total.ToString();
        TxtPendingServers.Text = pending.ToString();
        TxtCompletedServers.Text = completed.ToString();
    }

    private void OnRunTimerTick(object? sender, EventArgs e)
    {
        UpdateElapsed();
    }

    private void UpdateElapsed()
    {
        if (_runStartUtc == default)
        {
            TxtElapsed.Text = "00:00:00";
            return;
        }

        var elapsed = DateTime.UtcNow - _runStartUtc;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        TxtElapsed.Text = elapsed.ToString(@"hh\:mm\:ss");
    }

    private void OnQueryEditorTextChanged(object? sender, EventArgs e)
    {
        _isQueryDirty = true;
    }

    private void OpenSqlFile()
    {
        if (!PromptToSaveChangesIfNeeded())
        {
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = SqlFileDialogFilter,
            InitialDirectory = GetInitialSqlFolder()
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            QueryEditor.Text = File.ReadAllText(dlg.FileName);
            _currentQueryFilePath = dlg.FileName;
            _isQueryDirty = false;
            UpdateQueryHeader();
            RememberSqlFolderFromPath(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Open Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool SaveSqlFile()
    {
        if (string.IsNullOrWhiteSpace(_currentQueryFilePath))
        {
            return SaveSqlFileAs();
        }

        return SaveSqlFileToPath(_currentQueryFilePath);
    }

    private bool SaveSqlFileAs()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = SqlFileDialogFilter,
            DefaultExt = ".sql",
            AddExtension = true,
            InitialDirectory = GetInitialSqlFolder(),
            FileName = GetSaveFileName()
        };

        if (dlg.ShowDialog() != true)
        {
            return false;
        }

        return SaveSqlFileToPath(dlg.FileName);
    }

    private bool SaveSqlFileToPath(string filePath)
    {
        try
        {
            File.WriteAllText(filePath, QueryEditor.Text);
            _currentQueryFilePath = filePath;
            _isQueryDirty = false;
            UpdateQueryHeader();
            RememberSqlFolderFromPath(filePath);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private bool PromptToSaveChangesIfNeeded()
    {
        if (!_isQueryDirty)
        {
            return true;
        }

        var result = MessageBox.Show(
            "Do you want to save changes to the current SQL file?",
            "Unsaved Changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        return result switch
        {
            MessageBoxResult.Yes => SaveSqlFile(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void UpdateQueryHeader()
    {
        var header = DefaultQueryHeader;
        if (!string.IsNullOrWhiteSpace(_currentQueryFilePath))
        {
            header = $"{header} - {Path.GetFileName(_currentQueryFilePath)}";
        }

        QueryGroupBox.Header = header;
    }

    private string GetInitialSqlFolder()
    {
        var currentFileFolder = string.IsNullOrWhiteSpace(_currentQueryFilePath)
            ? null
            : Path.GetDirectoryName(_currentQueryFilePath);
        if (!string.IsNullOrWhiteSpace(currentFileFolder) && Directory.Exists(currentFileFolder))
        {
            return currentFileFolder;
        }

        var savedFolder = _store.LoadLastSqlFileFolder();
        if (!string.IsNullOrWhiteSpace(savedFolder) && Directory.Exists(savedFolder))
        {
            return savedFolder;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private string GetSaveFileName()
    {
        if (!string.IsNullOrWhiteSpace(_currentQueryFilePath))
        {
            return Path.GetFileName(_currentQueryFilePath);
        }

        return DefaultQueryFileName;
    }

    private void RememberSqlFolderFromPath(string filePath)
    {
        var folder = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            _store.SaveLastSqlFileFolder(folder);
        }
    }

    private void OnDatabaseSidebarCollapsed(object sender, RoutedEventArgs e)
    {
        if (SidebarColumn.Width.Value > 28)
        {
            _sidebarExpandedWidth = SidebarColumn.Width;
        }

        SidebarSplitter.Visibility = Visibility.Collapsed;
        SidebarSplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
        SidebarColumn.Width = new GridLength(28, GridUnitType.Pixel);
    }

    private void OnDatabaseSidebarExpanded(object sender, RoutedEventArgs e)
    {
        SidebarSplitter.Visibility = Visibility.Visible;
        SidebarSplitterColumn.Width = new GridLength(6, GridUnitType.Pixel);
        if (_sidebarExpandedWidth.Value <= 28)
        {
            _sidebarExpandedWidth = new GridLength(280, GridUnitType.Pixel);
        }

        SidebarColumn.Width = _sidebarExpandedWidth;
    }
}

internal sealed class DatabaseExecutionItem : INotifyPropertyChanged
{
    private static readonly Brush NotStartedBrush = new SolidColorBrush(Color.FromRgb(120, 120, 120));
    private static readonly Brush ConnectedBrush = new SolidColorBrush(Color.FromRgb(0, 120, 215));
    private static readonly Brush RunningBrush = new SolidColorBrush(Color.FromRgb(230, 145, 0));
    private static readonly Brush CompletedBrush = new SolidColorBrush(Color.FromRgb(46, 125, 50));
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(183, 28, 28));

    private QueryExecutionStatus _status;
    private string? _errorMessage;

    public DatabaseExecutionItem(ServerConnection server)
    {
        ServerName = server.Name;
        DatabaseName = string.IsNullOrWhiteSpace(server.Database) ? "(No Database Name)" : server.Database;
        DisplayName = ServerName;
        _status = QueryExecutionStatus.NotStarted;
    }

    public string ServerName { get; }
    public string DatabaseName { get; }
    public string DisplayName { get; }

    public string StatusGlyph => _status switch
    {
        QueryExecutionStatus.NotStarted => "N",
        QueryExecutionStatus.Connected => "C",
        QueryExecutionStatus.Running => "R",
        QueryExecutionStatus.Completed => "D",
        QueryExecutionStatus.Errored => "!",
        _ => "?"
    };

    public Brush StatusBrush => _status switch
    {
        QueryExecutionStatus.NotStarted => NotStartedBrush,
        QueryExecutionStatus.Connected => ConnectedBrush,
        QueryExecutionStatus.Running => RunningBrush,
        QueryExecutionStatus.Completed => CompletedBrush,
        QueryExecutionStatus.Errored => ErrorBrush,
        _ => NotStartedBrush
    };

    public string StatusTooltip => _status switch
    {
        QueryExecutionStatus.NotStarted => "Not started yet",
        QueryExecutionStatus.Connected => "Connected",
        QueryExecutionStatus.Running => "Running",
        QueryExecutionStatus.Completed => "Completed",
        QueryExecutionStatus.Errored => string.IsNullOrWhiteSpace(_errorMessage)
            ? "Errored"
            : $"Errored: {_errorMessage}",
        _ => "Unknown"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetStatus(QueryExecutionStatus status, string? errorMessage)
    {
        _status = status;
        _errorMessage = errorMessage;
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(StatusTooltip));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
