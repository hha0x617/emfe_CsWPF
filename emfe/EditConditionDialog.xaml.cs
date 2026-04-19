using System.Windows;

namespace emfe;

public partial class EditConditionDialog : Window
{
    public string Condition { get; private set; } = "";

    public EditConditionDialog(uint address, string currentCondition)
    {
        InitializeComponent();
        HeaderText.Text = $"Breakpoint at ${address:X8}";
        ConditionBox.Text = currentCondition;
        Loaded += (_, _) => { ThemeHelper.ApplyTitleBar(this, ThemeHelper.IsDarkMode); ConditionBox.Focus(); ConditionBox.SelectAll(); };
    }

    private void OnOK(object sender, RoutedEventArgs e)
    {
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
