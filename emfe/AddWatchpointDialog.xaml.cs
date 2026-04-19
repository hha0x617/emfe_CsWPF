using System;
using System.Globalization;
using System.Windows;

namespace emfe;

public partial class AddWatchpointDialog : Window
{
    public ulong Address { get; private set; }
    public EmfeWatchpointSize Size { get; private set; } = EmfeWatchpointSize.Word;
    public EmfeWatchpointType Type { get; private set; } = EmfeWatchpointType.Write;
    public string Condition { get; private set; } = "";

    public AddWatchpointDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => { ThemeHelper.ApplyTitleBar(this, ThemeHelper.IsDarkMode); AddressBox.Focus(); };
    }

    public AddWatchpointDialog(ulong address, EmfeWatchpointSize size, EmfeWatchpointType type, string condition)
        : this()
    {
        Title = "Edit Watchpoint";
        AddressBox.Text = $"{address:X8}";
        SizeBox.SelectedIndex = size switch
        {
            EmfeWatchpointSize.Byte => 0,
            EmfeWatchpointSize.Long => 2,
            _ => 1
        };
        TypeBox.SelectedIndex = type switch
        {
            EmfeWatchpointType.Read => 0,
            EmfeWatchpointType.ReadWrite => 2,
            _ => 1
        };
        ConditionBox.Text = condition;
    }

    private void OnOK(object sender, RoutedEventArgs e)
    {
        var text = AddressBox.Text.Trim().TrimStart('$').TrimStart('0', 'x').TrimStart('0', 'X');
        if (text.Length == 0) text = AddressBox.Text.Trim().TrimStart('$');
        if (!ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong addr))
        {
            MessageBox.Show(this, "Invalid hex address.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Address = addr;
        Size = SizeBox.SelectedIndex switch
        {
            0 => EmfeWatchpointSize.Byte,
            2 => EmfeWatchpointSize.Long,
            _ => EmfeWatchpointSize.Word
        };
        Type = TypeBox.SelectedIndex switch
        {
            0 => EmfeWatchpointType.Read,
            2 => EmfeWatchpointType.ReadWrite,
            _ => EmfeWatchpointType.Write
        };
        Condition = ConditionBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
