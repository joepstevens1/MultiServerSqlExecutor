using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
            var authType = AuthType.SqlPassword;
            if (CboAuthType.SelectedItem is ComboBoxItem item && Enum.TryParse<AuthType>(item.Tag.ToString(), out var parsedAuth))
            {
                authType = parsedAuth;
            }

            var server = new ServerConnection
            {
                Name = TxtName.Text.Trim(),
                Server = TxtServer.Text.Trim(),
                Database = TxtDatabase.Text.Trim(),
                Username = TxtUser.Text.Trim(),
                Password = TxtPass.Password,
                Authentication = authType
            };

            if (string.IsNullOrWhiteSpace(server.Name) || string.IsNullOrWhiteSpace(server.Server) ||
                string.IsNullOrWhiteSpace(server.Database))
            {
                MessageBox.Show("Please fill in Name, Server, and Database.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Validation based on auth type
            if (authType == AuthType.SqlPassword || authType == AuthType.AzurePassword)
            {
                if (string.IsNullOrWhiteSpace(server.Username) || string.IsNullOrWhiteSpace(server.Password))
                {
                    MessageBox.Show("Username and Password are required for this authentication type.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            else if (authType == AuthType.AzureMfa || authType == AuthType.AzureInteractive)
            {
                // Username is optional but recommended as a hint.
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
        CboAuthType.SelectedIndex = 0;
        TxtName.IsEnabled = true;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServersList.SelectedItem is ServerConnection sc)
        {
            TxtName.Text = sc.Name;
            TxtServer.Text = sc.Server;
            TxtDatabase.Text = sc.Database;
            TxtUser.Text = sc.Username;
            TxtPass.Password = sc.Password;

            foreach (ComboBoxItem item in CboAuthType.Items)
            {
                if (item.Tag.ToString() == sc.Authentication.ToString())
                {
                    CboAuthType.SelectedItem = item;
                    break;
                }
            }
        }
    }

    private void OnAuthTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CboAuthType == null || LblUser == null || TxtUser == null || LblPass == null || TxtPass == null) return;
        if (CboAuthType.SelectedItem is ComboBoxItem item && Enum.TryParse<AuthType>(item.Tag.ToString(), out var authType))
        {
            switch (authType)
            {
                case AuthType.SqlPassword:
                case AuthType.AzurePassword:
                    LblUser.Visibility = Visibility.Visible;
                    TxtUser.Visibility = Visibility.Visible;
                    LblPass.Visibility = Visibility.Visible;
                    TxtPass.Visibility = Visibility.Visible;
                    break;
                case AuthType.AzureMfa:
                case AuthType.AzureInteractive:
                    LblUser.Visibility = Visibility.Visible;
                    TxtUser.Visibility = Visibility.Visible;
                    LblPass.Visibility = Visibility.Collapsed;
                    TxtPass.Visibility = Visibility.Collapsed;
                    break;
            }
        }
    }

    private void OnRemoveServer(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ServerConnection sc })
        {
            if (MessageBox.Show($"Are you sure you want to remove the server '{sc.Name}'?", "Confirm Removal",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _store.Remove(sc.Name);
                LoadServers();
            }
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
