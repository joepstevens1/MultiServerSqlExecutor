using System.Windows;

namespace MultiServerSqlExecutor.Ui;

public partial class ValueRemapWindow : Window
{
    public ValueRemapWindow(string fieldName, string sourceValue, string initialTargetValue)
    {
        InitializeComponent();
        TxtMessage.Text = $"Remap this imported {fieldName} value before it is written into the saved database list.";
        TxtSourceValue.Text = sourceValue;
        TxtTargetValue.Text = initialTargetValue;
        TxtTargetValue.SelectAll();
        TxtTargetValue.Focus();
    }

    public string TargetValue => TxtTargetValue.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
