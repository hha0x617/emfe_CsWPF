using System;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace emfe;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            LogFatal(a.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, a) =>
        {
            LogFatal(a.Exception);
            a.Handled = false;
        };

        bool dark = ThemeHelper.ResolveDarkMode(LoadThemeFromSettings());

        ThemeHelper.SetAppMode(dark);
        ApplyTheme(dark);

        base.OnStartup(e);
    }

    private static void LogFatal(Exception? ex)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(localAppData, "emfe_CsWPF");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:O}] {ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}\n\n");
        }
        catch { }
    }

    public static void ApplyTheme(bool dark)
    {
        var merged = Current.Resources.MergedDictionaries;
        if (merged.Count == 0) return;

        string source = dark ? "Themes/Dark.xaml" : "Themes/Light.xaml";
        merged[0] = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };

        ThemeHelper.IsDarkMode = dark;
    }

    private static string LoadThemeFromSettings()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var path = Path.Combine(localAppData, "emfe_CsWPF", "appsettings.json");
            if (!File.Exists(path)) return "Dark";
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("Theme", out var el) &&
                el.ValueKind == JsonValueKind.String)
            {
                return el.GetString() ?? "Dark";
            }
        }
        catch { }
        return "Dark";
    }
}
