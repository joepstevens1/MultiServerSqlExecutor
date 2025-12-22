using System;
using System.Windows;
using MultiServerSqlExecutor.Core.Models;
using MultiServerSqlExecutor.Core.Services;

namespace MultiServerSqlExecutor.Ui;

public partial class ServersWindow : Window
{
    private readonly ConfigStore _store = new();

    public ServersWindow()
    {
        InitializeComponent();
        LoadServers();
    }

    private void LoadServers()
    {
        ServersList.ItemsSource = _store.Load();
    }

    private void OnSaveServer(object sender, RoutedEventArgs e)
    {
        try
        {
            var server = new ServerConnection
            {
                Name = TxtName.Text.Trim(),
                Server = TxtServer.Text.Trim(),
                Database = TxtDatabase.Text.Trim(),
                Username = TxtUser.Text.Trim(),
                Password = TxtPass.Password
            };
            if (string.IsNullOrWhiteSpace(server.Name) || string.IsNullOrWhiteSpace(server.Server) ||
                string.IsNullOrWhiteSpace(server.Database) || string.IsNullOrWhiteSpace(server.Username))
            {
                MessageBox.Show("Please fill in all fields.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _store.AddOrUpdate(server);
            ClearFields();
            LoadServers();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnClearFields(object sender, RoutedEventArgs e)
    {
        ClearFields();
    }

    private void ClearFields()
    {
        ServersList.SelectedItem = null;
        TxtName.Clear();
        TxtServer.Clear();
        TxtDatabase.Clear();
        TxtUser.Clear();
        TxtPass.Clear();
        TxtName.IsEnabled = true;
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ServersList.SelectedItem is ServerConnection sc)
        {
            TxtName.Text = sc.Name;
            TxtServer.Text = sc.Server;
            TxtDatabase.Text = sc.Database;
            TxtUser.Text = sc.Username;
            TxtPass.Password = sc.Password;
            // Name is the key in our simple AddOrUpdate, so let's keep it disabled if editing
            // Or we could allow changing it but then it would be a "Save As" if not careful.
            // Requirement says "select an existing server it populates the edit fields and you can modify the values and save that changes"
            // If they change the name, it might create a new entry.
            // TxtName.IsEnabled = false; 
        }
    }

    private void OnRemoveServer(object sender, RoutedEventArgs e)
    {
        if (ServersList.SelectedItem is ServerConnection sc)
        {
            _store.Remove(sc.Name);
            LoadServers();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
