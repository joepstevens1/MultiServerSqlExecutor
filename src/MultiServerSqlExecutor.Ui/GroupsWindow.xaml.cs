using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using MultiServerSqlExecutor.Core.Services;

namespace MultiServerSqlExecutor.Ui;

public partial class GroupsWindow : Window
{
    private readonly ConfigStore _store = new();
    private readonly ObservableCollection<ServerMembershipItem> _serverMembership = new();

    public GroupsWindow()
    {
        InitializeComponent();
        ServersItems.ItemsSource = _serverMembership;
        LoadGroups();
    }

    private void LoadGroups(string? selectedGroup = null)
    {
        var groups = _store.LoadGroups().OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        GroupsList.ItemsSource = groups;

        if (groups.Count == 0)
        {
            GroupsList.SelectedItem = null;
            _serverMembership.Clear();
            LblSelectedGroup.Text = "No group selected.";
            return;
        }

        var toSelect = selectedGroup != null
            ? groups.FirstOrDefault(g => string.Equals(g, selectedGroup, StringComparison.OrdinalIgnoreCase))
            : groups[0];
        GroupsList.SelectedItem = toSelect ?? groups[0];
    }

    private void OnAddGroup(object sender, RoutedEventArgs e)
    {
        try
        {
            var groupName = TxtGroupName.Text.Trim();
            _store.AddGroup(groupName);
            TxtGroupName.Clear();
            LoadGroups(groupName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRemoveGroup(object sender, RoutedEventArgs e)
    {
        if (GroupsList.SelectedItem is not string selectedGroup)
        {
            MessageBox.Show("Select a group to remove.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"Are you sure you want to remove the group '{selectedGroup}'?",
            "Confirm Removal",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        _store.RemoveGroup(selectedGroup);
        LoadGroups();
    }

    private void OnGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshMembershipForSelectedGroup();
    }

    private void RefreshMembershipForSelectedGroup()
    {
        if (GroupsList.SelectedItem is not string selectedGroup)
        {
            _serverMembership.Clear();
            LblSelectedGroup.Text = "No group selected.";
            return;
        }

        var servers = _store.Load()
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _serverMembership.Clear();
        foreach (var server in servers)
        {
            _serverMembership.Add(new ServerMembershipItem
            {
                Name = server.Name,
                IsInGroup = server.Groups.Any(g => string.Equals(g, selectedGroup, StringComparison.OrdinalIgnoreCase))
            });
        }

        LblSelectedGroup.Text = $"Selected Group: {selectedGroup}";
    }

    private void OnSaveMembership(object sender, RoutedEventArgs e)
    {
        if (GroupsList.SelectedItem is not string selectedGroup)
        {
            MessageBox.Show("Select a group first.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var serversInGroup = _serverMembership
            .Where(s => s.IsInGroup)
            .Select(s => s.Name)
            .ToList();

        _store.SetGroupMembership(selectedGroup, serversInGroup);
        MessageBox.Show("Group membership saved.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        RefreshMembershipForSelectedGroup();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed class ServerMembershipItem : INotifyPropertyChanged
    {
        private bool _isInGroup;

        public string Name { get; set; } = string.Empty;

        public bool IsInGroup
        {
            get => _isInGroup;
            set
            {
                if (_isInGroup == value) return;
                _isInGroup = value;
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
