using Microsoft.Win32;
using MultiServerSqlExecutor.Core.Models;
using MultiServerSqlExecutor.Core.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MultiServerSqlExecutor.Ui;

public partial class ImportDatabasesWindow : Window, INotifyPropertyChanged
{
    private readonly ConfigStore _store = new();
    private readonly DatabaseImportService _importService = new();
    private readonly ObservableCollection<FieldMappingEditorItem> _fieldMappings = new();
    private readonly ObservableCollection<GroupMappingEditorItem> _groupMappings = new();
    private readonly ObservableCollection<ValueMapEditorItem> _valueMappings = new();
    private readonly ObservableCollection<ImportPreviewRow> _previewItems = new();
    private readonly ObservableCollection<RemovalCandidateItem> _removalItems = new();
    private readonly ObservableCollection<string> _availableColumns = new();
    private readonly ImportMappingProfile _storedProfile;
    private readonly DispatcherTimer _previewRefreshTimer;
    private MappingEditorBase? _selectedMapping;
    private DatabaseImportPlan? _currentPlan;
    private ImportPreviewRow? _previewContextRow;
    private string? _previewContextColumnHeader;
    private int _nextGroupMappingNumber = 1;
    private bool _suppressAutoPreview;

    public ImportDatabasesWindow()
    {
        InitializeComponent();
        DataContext = this;

        _storedProfile = _store.LoadImportProfile();
        SourceModes = Enum.GetValues<ImportValueSourceMode>().ToList();
        AuthenticationOptions = Enum.GetNames<AuthType>().ToList();
        AvailableColumns = _availableColumns;

        FieldMappingsGrid.ItemsSource = _fieldMappings;
        GroupMappingsGrid.ItemsSource = _groupMappings;
        ValueMappingsGrid.ItemsSource = _valueMappings;
        PreviewGrid.ItemsSource = _previewItems;
        RemovalsGrid.ItemsSource = _removalItems;

        _previewRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _previewRefreshTimer.Tick += OnPreviewRefreshTimerTick;

        TxtCsvPath.TextChanged += OnCsvPathChanged;
        _fieldMappings.CollectionChanged += OnFieldMappingsCollectionChanged;
        _groupMappings.CollectionChanged += OnGroupMappingsCollectionChanged;

        LoadEditorFromProfile(_storedProfile);
        SelectFirstFieldMapping();
    }

    public List<ImportValueSourceMode> SourceModes { get; }
    public List<string> AuthenticationOptions { get; }
    public ObservableCollection<string> AvailableColumns { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnBrowseCsv(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        TxtCsvPath.Text = dialog.FileName;
    }

    private void OnPreviewImport(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitPendingEdits();
            RefreshPreview(showErrors: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Import Preview Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (_currentPlan == null)
        {
            MessageBox.Show("Preview the import before applying it.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            CommitPendingEdits();

            var profile = BuildProfileFromEditor();
            var finalPlan = _importService.Analyze(new DatabaseImportRequest
            {
                CsvPath = TxtCsvPath.Text.Trim(),
                Profile = profile
            }, _store.Load());

            _store.SaveImportProfile(profile);

            var removals = ChkEnableRemovals.IsChecked == true
                ? _removalItems.Where(item => item.IsSelected).Select(item => item.Key).ToList()
                : new List<string>();

            var updatedServers = _importService.ApplyImport(_store.Load(), finalPlan, removals);
            _store.Save(updatedServers);

            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CommitPendingEdits()
    {
        FieldMappingsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        FieldMappingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        GroupMappingsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        GroupMappingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ValueMappingsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        ValueMappingsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        RemovalsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RemovalsGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnCsvPathChanged(object sender, TextChangedEventArgs e)
    {
        SchedulePreviewRefresh();
    }

    private void OnPreviewRefreshTimerTick(object? sender, EventArgs e)
    {
        _previewRefreshTimer.Stop();
        RefreshPreview(showErrors: false);
    }

    private void OnPreviewGridRightMouseButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TrySetPreviewContextFromVisual(e.OriginalSource as DependencyObject))
        {
            ClearPreviewContext();
        }

        ConfigurePreviewContextMenu();
    }

    private void OnPreviewGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (TrySetPreviewContextFromCurrentCell() && TryGetPreviewCellValue(out var cellValue))
            {
                Clipboard.SetText(cellValue);
                e.Handled = true;
            }
        }
    }

    private void OnPreviewCellCopy(object sender, RoutedEventArgs e)
    {
        if (!TryGetPreviewCellValue(out var cellValue))
            return;

        Clipboard.SetText(cellValue);
    }

    private void OnPreviewCellRemap(object sender, RoutedEventArgs e)
    {
        if (!TryGetPreviewRemapContext(out var mapping, out var sourceValue, out var currentValue, out var fieldName))
            return;

        if (string.IsNullOrWhiteSpace(sourceValue))
        {
            MessageBox.Show("This preview value does not have an import value to remap.", "Remap Value", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var existingValueMap = mapping.ValueMappings.FirstOrDefault(item =>
            string.Equals(item.SourceValue, sourceValue, StringComparison.OrdinalIgnoreCase));

        var dialog = new ValueRemapWindow(fieldName, sourceValue, existingValueMap?.TargetValue ?? currentValue)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        ApplyValueRemap(mapping, sourceValue, dialog.TargetValue);
    }

    private void OnAddGroupMapping(object sender, RoutedEventArgs e)
    {
        var item = new GroupMappingEditorItem($"Group {_nextGroupMappingNumber++}");
        _groupMappings.Add(item);
        GroupMappingsGrid.SelectedItem = item;
    }

    private void OnRemoveGroupMapping(object sender, RoutedEventArgs e)
    {
        if (GroupMappingsGrid.SelectedItem is not GroupMappingEditorItem item)
            return;

        _groupMappings.Remove(item);
        if (ReferenceEquals(_selectedMapping, item))
        {
            SetSelectedMapping(null);
        }

        RenumberGroupMappings();
    }

    private void OnAddValueRemap(object sender, RoutedEventArgs e)
    {
        if (_selectedMapping == null)
        {
            MessageBox.Show("Select a field mapping or group mapping first.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var item = new ValueMapEditorItem();
        _valueMappings.Add(item);
        _selectedMapping.ValueMappings.Add(item);
        ValueMappingsGrid.SelectedItem = item;
    }

    private void OnRemoveValueRemap(object sender, RoutedEventArgs e)
    {
        if (ValueMappingsGrid.SelectedItem is not ValueMapEditorItem item || _selectedMapping == null)
            return;

        _valueMappings.Remove(item);
        _selectedMapping.ValueMappings.Remove(item);
    }

    private void OnFieldMappingSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FieldMappingsGrid.SelectedItem is FieldMappingEditorItem item)
        {
            GroupMappingsGrid.SelectedItem = null;
            SetSelectedMapping(item);
        }
    }

    private void OnGroupMappingSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupMappingsGrid.SelectedItem is GroupMappingEditorItem item)
        {
            FieldMappingsGrid.SelectedItem = null;
            SetSelectedMapping(item);
        }
    }

    private void ApplyAvailableColumns(IEnumerable<string> columns)
    {
        _availableColumns.Clear();
        foreach (var column in columns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            _availableColumns.Add(column);
        }

        OnPropertyChanged(nameof(AvailableColumns));
    }

    private void LoadEditorFromProfile(ImportMappingProfile profile)
    {
        var selectedKey = _selectedMapping?.DisplayName;
        _suppressAutoPreview = true;

        _fieldMappings.Clear();
        foreach (var field in Enum.GetValues<ServerConfigField>())
        {
            var mapping = profile.FieldMappings.FirstOrDefault(f => f.TargetField == field) ?? new ImportFieldMapping { TargetField = field };
            _fieldMappings.Add(new FieldMappingEditorItem(field)
            {
                SourceMode = mapping.SourceMode,
                SourceColumn = mapping.SourceColumn,
                FixedValue = mapping.FixedValue,
                ValueMappings = new ObservableCollection<ValueMapEditorItem>(mapping.ValueMappings.Select(ToEditorValueMap))
            });
        }

        _groupMappings.Clear();
        foreach (var mapping in profile.GroupMappings)
        {
            _groupMappings.Add(new GroupMappingEditorItem($"Group {_nextGroupMappingNumber}")
            {
                SourceMode = mapping.SourceMode,
                SourceColumn = mapping.SourceColumn,
                FixedValue = mapping.FixedValue,
                ValueMappings = new ObservableCollection<ValueMapEditorItem>(mapping.ValueMappings.Select(ToEditorValueMap))
            });
            _nextGroupMappingNumber++;
        }

        RenumberGroupMappings();
        RestoreSelection(selectedKey);
        _suppressAutoPreview = false;
    }

    private void RestoreSelection(string? selectedKey)
    {
        if (!string.IsNullOrWhiteSpace(selectedKey))
        {
            var fieldItem = _fieldMappings.FirstOrDefault(i => string.Equals(i.DisplayName, selectedKey, StringComparison.OrdinalIgnoreCase));
            if (fieldItem != null)
            {
                FieldMappingsGrid.SelectedItem = fieldItem;
                SetSelectedMapping(fieldItem);
                return;
            }

            var groupItem = _groupMappings.FirstOrDefault(i => string.Equals(i.DisplayName, selectedKey, StringComparison.OrdinalIgnoreCase));
            if (groupItem != null)
            {
                GroupMappingsGrid.SelectedItem = groupItem;
                SetSelectedMapping(groupItem);
                return;
            }
        }

        SelectFirstFieldMapping();
    }

    private void SelectFirstFieldMapping()
    {
        if (_fieldMappings.Count == 0)
        {
            SetSelectedMapping(null);
            return;
        }

        FieldMappingsGrid.SelectedItem = _fieldMappings[0];
        SetSelectedMapping(_fieldMappings[0]);
    }

    private void SetSelectedMapping(MappingEditorBase? mapping)
    {
        _selectedMapping = mapping;
        _valueMappings.Clear();

        if (mapping == null)
        {
            TxtValueRemapContext.Text = "Select a field mapping or group mapping to edit value remaps.";
            return;
        }

        foreach (var item in mapping.ValueMappings)
        {
            _valueMappings.Add(item);
        }

        TxtValueRemapContext.Text = $"Value remaps for {mapping.DisplayName}";
    }

    private void ApplyValueRemap(MappingEditorBase mapping, string sourceValue, string targetValue)
    {
        CommitPendingEdits();

        var existingValueMap = mapping.ValueMappings.FirstOrDefault(item =>
            string.Equals(item.SourceValue, sourceValue, StringComparison.OrdinalIgnoreCase));

        if (existingValueMap == null)
        {
            existingValueMap = new ValueMapEditorItem
            {
                SourceValue = sourceValue,
                TargetValue = targetValue
            };
            mapping.ValueMappings.Add(existingValueMap);
        }
        else
        {
            existingValueMap.TargetValue = targetValue;
        }

        SelectMapping(mapping);
        ValueMappingsGrid.SelectedItem = existingValueMap;
        SchedulePreviewRefresh();
    }

    private void SelectMapping(MappingEditorBase mapping)
    {
        switch (mapping)
        {
            case FieldMappingEditorItem fieldMapping:
                GroupMappingsGrid.SelectedItem = null;
                FieldMappingsGrid.SelectedItem = fieldMapping;
                SetSelectedMapping(fieldMapping);
                break;
            case GroupMappingEditorItem groupMapping:
                FieldMappingsGrid.SelectedItem = null;
                GroupMappingsGrid.SelectedItem = groupMapping;
                SetSelectedMapping(groupMapping);
                break;
        }
    }

    private void RenumberGroupMappings()
    {
        for (int i = 0; i < _groupMappings.Count; i++)
        {
            _groupMappings[i].DisplayName = $"Group {i + 1}";
        }

        _nextGroupMappingNumber = _groupMappings.Count + 1;
    }

    private ImportMappingProfile BuildProfileFromEditor()
    {
        return new ImportMappingProfile
        {
            FieldMappings = _fieldMappings
                .Select(item => new ImportFieldMapping
                {
                    TargetField = item.TargetField,
                    SourceMode = item.SourceMode,
                    SourceColumn = item.SourceColumn.Trim(),
                    FixedValue = item.FixedValue.Trim(),
                    ValueMappings = item.ValueMappings.Select(ToModelValueMap).ToList()
                })
                .ToList(),
            GroupMappings = _groupMappings
                .Select(item => new ImportGroupMapping
                {
                    SourceMode = item.SourceMode,
                    SourceColumn = item.SourceColumn.Trim(),
                    FixedValue = item.FixedValue.Trim(),
                    ValueMappings = item.ValueMappings.Select(ToModelValueMap).ToList()
                })
                .ToList()
        };
    }

    private void RefreshPreview(bool showErrors)
    {
        var csvPath = TxtCsvPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(csvPath))
        {
            if (showErrors)
            {
                MessageBox.Show("Choose a CSV file first.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }

        try
        {
            var existingServers = _store.Load();
            var requestedProfile = BuildProfileFromEditor();
            var effectiveProfile = _importService.CreateSuggestedProfile(
                csvPath,
                existingServers,
                HasConfiguredMappings(requestedProfile) ? requestedProfile : _storedProfile);
            var plan = _importService.Analyze(new DatabaseImportRequest
            {
                CsvPath = csvPath,
                Profile = effectiveProfile
            }, existingServers);

            ApplyAvailableColumns(plan.AvailableColumns);
            LoadEditorFromProfile(plan.Profile);
            PopulatePreview(plan);
            PopulateRemovals(plan);
            _currentPlan = plan;
        }
        catch when (!showErrors)
        {
            // Ignore background refresh errors until the user explicitly requests preview.
        }
    }

    private void SchedulePreviewRefresh()
    {
        if (_suppressAutoPreview)
            return;

        _previewRefreshTimer.Stop();
        _previewRefreshTimer.Start();
    }

    private static bool HasConfiguredMappings(ImportMappingProfile profile)
    {
        return profile.FieldMappings.Any(mapping =>
                   mapping.SourceMode != ImportValueSourceMode.None ||
                   !string.IsNullOrWhiteSpace(mapping.SourceColumn) ||
                   !string.IsNullOrWhiteSpace(mapping.FixedValue) ||
                   mapping.ValueMappings.Count > 0) ||
               profile.GroupMappings.Any(mapping =>
                   mapping.SourceMode != ImportValueSourceMode.None ||
                   !string.IsNullOrWhiteSpace(mapping.SourceColumn) ||
                   !string.IsNullOrWhiteSpace(mapping.FixedValue) ||
                   mapping.ValueMappings.Count > 0);
    }

    private void PopulatePreview(DatabaseImportPlan plan)
    {
        _previewItems.Clear();
        foreach (var preview in plan.ServersToImport)
        {
            _previewItems.Add(new ImportPreviewRow
            {
                Status = preview.Exists ? "Update" : "Add",
                Name = preview.Server.Name,
                Server = preview.Server.Server,
                Database = preview.Server.Database,
                Authentication = preview.Server.Authentication.ToString(),
                Groups = preview.Server.Groups.Count > 0 ? string.Join(", ", preview.Server.Groups) : "(none)",
                SourceFieldValues = preview.SourceFieldValues,
                SourceGroupValues = preview.SourceGroupValues
            });
        }

        TxtSummary.Text =
            $"Rows read: {plan.TotalRowsRead}\n" +
            $"Rows skipped: {plan.SkippedRows}\n" +
            $"Databases to add or update: {plan.ServersToImport.Count}\n" +
            $"Saved databases not in import: {plan.ServersMissingFromImport.Count}";
    }

    private void PopulateRemovals(DatabaseImportPlan plan)
    {
        _removalItems.Clear();
        foreach (var preview in plan.ServersMissingFromImport)
        {
            _removalItems.Add(new RemovalCandidateItem
            {
                Key = preview.Key,
                Name = preview.Server.Name,
                Server = preview.Server.Server,
                Database = preview.Server.Database
            });
        }

        ChkEnableRemovals.IsChecked = false;
        if (plan.ServersMissingFromImport.Count == 0)
        {
            TxtSummary.Text += "\nNo saved databases are missing from the import.";
        }
    }

    private static ValueMapEditorItem ToEditorValueMap(ImportValueMap valueMap)
    {
        return new ValueMapEditorItem
        {
            SourceValue = valueMap.SourceValue,
            TargetValue = valueMap.TargetValue
        };
    }

    private static ImportValueMap ToModelValueMap(ValueMapEditorItem valueMap)
    {
        return new ImportValueMap
        {
            SourceValue = valueMap.SourceValue.Trim(),
            TargetValue = valueMap.TargetValue.Trim()
        };
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void OnFieldMappingsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (FieldMappingEditorItem item in e.OldItems)
            {
                DetachMapping(item);
            }
        }

        if (e.NewItems != null)
        {
            foreach (FieldMappingEditorItem item in e.NewItems)
            {
                AttachMapping(item);
            }
        }

        SchedulePreviewRefresh();
    }

    private void OnGroupMappingsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (GroupMappingEditorItem item in e.OldItems)
            {
                DetachMapping(item);
            }
        }

        if (e.NewItems != null)
        {
            foreach (GroupMappingEditorItem item in e.NewItems)
            {
                AttachMapping(item);
            }
        }

        SchedulePreviewRefresh();
    }

    private void AttachMapping(MappingEditorBase mapping)
    {
        mapping.PropertyChanged += OnMappingPropertyChanged;
        mapping.ValueMappings.CollectionChanged += OnValueMappingsCollectionChanged;

        foreach (var item in mapping.ValueMappings)
        {
            item.PropertyChanged += OnValueMapPropertyChanged;
        }
    }

    private void DetachMapping(MappingEditorBase mapping)
    {
        mapping.PropertyChanged -= OnMappingPropertyChanged;
        mapping.ValueMappings.CollectionChanged -= OnValueMappingsCollectionChanged;

        foreach (var item in mapping.ValueMappings)
        {
            item.PropertyChanged -= OnValueMapPropertyChanged;
        }
    }

    private void OnMappingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        SchedulePreviewRefresh();
    }

    private void OnValueMappingsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (ValueMapEditorItem item in e.OldItems)
            {
                item.PropertyChanged -= OnValueMapPropertyChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (ValueMapEditorItem item in e.NewItems)
            {
                item.PropertyChanged += OnValueMapPropertyChanged;
            }
        }

        SchedulePreviewRefresh();
    }

    private void OnValueMapPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        SchedulePreviewRefresh();
    }

    private void ConfigurePreviewContextMenu()
    {
        var copyMenuItem = GetPreviewCopyMenuItem();
        var remapMenuItem = GetPreviewRemapMenuItem();
        if (copyMenuItem == null || remapMenuItem == null)
            return;

        if (TryGetPreviewCellValue(out var cellValue))
        {
            copyMenuItem.IsEnabled = true;
            copyMenuItem.Header = string.IsNullOrWhiteSpace(cellValue)
                ? "Copy Empty Value"
                : "Copy Value";
        }
        else
        {
            copyMenuItem.IsEnabled = false;
            copyMenuItem.Header = "Copy Not Available";
        }

        if (TryGetPreviewRemapContext(out _, out var sourceValue, out _, out var fieldName))
        {
            remapMenuItem.IsEnabled = !string.IsNullOrWhiteSpace(sourceValue);
            remapMenuItem.Header = string.IsNullOrWhiteSpace(sourceValue)
                ? $"No Imported {fieldName} Value To Remap"
                : $"Remap {fieldName} Value...";
            return;
        }

        remapMenuItem.IsEnabled = false;
        remapMenuItem.Header = "Remap Not Available For This Column";
    }

    private bool TryGetPreviewRemapContext(
        out MappingEditorBase mapping,
        out string sourceValue,
        out string currentValue,
        out string fieldName)
    {
        mapping = null!;
        sourceValue = string.Empty;
        currentValue = string.Empty;
        fieldName = string.Empty;

        if (_previewContextRow == null || string.IsNullOrWhiteSpace(_previewContextColumnHeader))
            return false;

        ServerConfigField? field = _previewContextColumnHeader switch
        {
            "Name" => ServerConfigField.Name,
            "Server" => ServerConfigField.Server,
            "Database" => ServerConfigField.Database,
            "Auth" => ServerConfigField.Authentication,
            _ => null
        };

        if (field == null)
            return false;

        var fieldMapping = _fieldMappings.FirstOrDefault(item => item.TargetField == field.Value);
        if (fieldMapping == null)
            return false;

        mapping = fieldMapping;
        sourceValue = _previewContextRow.SourceFieldValues.TryGetValue(field.Value, out var value)
            ? value
            : string.Empty;
        currentValue = field.Value switch
        {
            ServerConfigField.Name => _previewContextRow.Name,
            ServerConfigField.Server => _previewContextRow.Server,
            ServerConfigField.Database => _previewContextRow.Database,
            ServerConfigField.Authentication => _previewContextRow.Authentication,
            _ => string.Empty
        };
        fieldName = _previewContextColumnHeader == "Auth" ? "Authentication" : _previewContextColumnHeader;
        return true;
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
                return match;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void ClearPreviewContext()
    {
        _previewContextRow = null;
        _previewContextColumnHeader = null;
    }

    private bool TrySetPreviewContextFromVisual(DependencyObject? dependencyObject)
    {
        var cell = FindAncestor<DataGridCell>(dependencyObject);
        if (cell == null)
            return false;

        if (FindAncestor<DataGridRow>(cell) is not { Item: ImportPreviewRow row })
            return false;

        PreviewGrid.SelectedItem = row;
        PreviewGrid.CurrentCell = new DataGridCellInfo(row, cell.Column);
        _previewContextRow = row;
        _previewContextColumnHeader = cell.Column?.Header?.ToString();
        return true;
    }

    private bool TrySetPreviewContextFromCurrentCell()
    {
        if (PreviewGrid.CurrentCell.Item is not ImportPreviewRow row)
            return false;

        _previewContextRow = row;
        _previewContextColumnHeader = PreviewGrid.CurrentCell.Column?.Header?.ToString();
        return !string.IsNullOrWhiteSpace(_previewContextColumnHeader);
    }

    private bool TryGetPreviewCellValue(out string cellValue)
    {
        cellValue = string.Empty;

        if (_previewContextRow == null || string.IsNullOrWhiteSpace(_previewContextColumnHeader))
            return false;

        cellValue = _previewContextColumnHeader switch
        {
            "Status" => _previewContextRow.Status,
            "Name" => _previewContextRow.Name,
            "Server" => _previewContextRow.Server,
            "Database" => _previewContextRow.Database,
            "Auth" => _previewContextRow.Authentication,
            "Groups" => _previewContextRow.Groups,
            _ => string.Empty
        };

        return true;
    }

    private MenuItem? GetPreviewCopyMenuItem()
    {
        return PreviewGrid.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault();
    }

    private MenuItem? GetPreviewRemapMenuItem()
    {
        return PreviewGrid.ContextMenu?.Items.OfType<MenuItem>().Skip(1).FirstOrDefault();
    }

    private abstract class MappingEditorBase : INotifyPropertyChanged
    {
        private ImportValueSourceMode _sourceMode;
        private string _sourceColumn = string.Empty;
        private string _fixedValue = string.Empty;
        private ObservableCollection<ValueMapEditorItem> _valueMappings = new();
        private string _displayName = string.Empty;

        public string DisplayName
        {
            get => _displayName;
            set
            {
                if (string.Equals(_displayName, value, StringComparison.Ordinal)) return;
                _displayName = value;
                OnPropertyChanged();
            }
        }

        public ImportValueSourceMode SourceMode
        {
            get => _sourceMode;
            set
            {
                if (_sourceMode == value) return;
                _sourceMode = value;
                OnPropertyChanged();
            }
        }

        public string SourceColumn
        {
            get => _sourceColumn;
            set
            {
                if (string.Equals(_sourceColumn, value, StringComparison.Ordinal)) return;
                _sourceColumn = value;
                OnPropertyChanged();
            }
        }

        public string FixedValue
        {
            get => _fixedValue;
            set
            {
                if (string.Equals(_fixedValue, value, StringComparison.Ordinal)) return;
                _fixedValue = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<ValueMapEditorItem> ValueMappings
        {
            get => _valueMappings;
            set
            {
                _valueMappings = value ?? new ObservableCollection<ValueMapEditorItem>();
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed class FieldMappingEditorItem : MappingEditorBase
    {
        public FieldMappingEditorItem(ServerConfigField targetField)
        {
            TargetField = targetField;
            DisplayName = targetField.ToString();
        }

        public ServerConfigField TargetField { get; }
        public bool IsAuthenticationField => TargetField == ServerConfigField.Authentication;
    }

    private sealed class GroupMappingEditorItem : MappingEditorBase
    {
        public GroupMappingEditorItem(string name)
        {
            DisplayName = name;
        }
    }

    private sealed class ValueMapEditorItem : INotifyPropertyChanged
    {
        private string _sourceValue = string.Empty;
        private string _targetValue = string.Empty;

        public string SourceValue
        {
            get => _sourceValue;
            set
            {
                if (string.Equals(_sourceValue, value, StringComparison.Ordinal)) return;
                _sourceValue = value;
                OnPropertyChanged();
            }
        }

        public string TargetValue
        {
            get => _targetValue;
            set
            {
                if (string.Equals(_targetValue, value, StringComparison.Ordinal)) return;
                _targetValue = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed class ImportPreviewRow
    {
        public string Status { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Server { get; init; } = string.Empty;
        public string Database { get; init; } = string.Empty;
        public string Authentication { get; init; } = string.Empty;
        public string Groups { get; init; } = string.Empty;
        public Dictionary<ServerConfigField, string> SourceFieldValues { get; init; } = new();
        public List<string> SourceGroupValues { get; init; } = new();
    }

    private sealed class RemovalCandidateItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Server { get; init; } = string.Empty;
        public string Database { get; init; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
