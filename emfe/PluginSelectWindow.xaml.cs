using System.IO;
using System.Windows;

namespace emfe;

public partial class PluginSelectWindow : Window
{
    private readonly string[] _paths;

    public string? SelectedPath { get; private set; }

    public PluginSelectWindow(string[] pluginPaths, string[]? displayNames = null,
                              string? preselectPath = null)
    {
        _paths = pluginPaths;
        InitializeComponent();
        Loaded += (_, _) => ThemeHelper.ApplyTitleBar(this, ThemeHelper.IsDarkMode);

        int preselectIdx = 0;
        for (int i = 0; i < _paths.Length; i++)
        {
            var label = (displayNames != null && i < displayNames.Length && !string.IsNullOrEmpty(displayNames[i]))
                ? displayNames[i]
                : Path.GetFileName(_paths[i]);
            PluginList.Items.Add(label);
            if (preselectPath != null &&
                string.Equals(Path.GetFullPath(_paths[i]), Path.GetFullPath(preselectPath),
                              System.StringComparison.OrdinalIgnoreCase))
            {
                preselectIdx = i;
            }
        }

        if (PluginList.Items.Count > 0)
            PluginList.SelectedIndex = preselectIdx;
    }

    private void OnOK(object sender, RoutedEventArgs e)
    {
        if (PluginList.SelectedIndex >= 0)
        {
            SelectedPath = _paths[PluginList.SelectedIndex];
            DialogResult = true;
        }
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PluginList.SelectedIndex >= 0)
            OnOK(sender, e);
    }
}
