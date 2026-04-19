using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace emfe;

public partial class BreakpointsWindow : Window
{
    private readonly IntPtr _instance;
    private readonly PluginInterop _plugin;
    private readonly FontFamily _consolas = new("Consolas");

    public BreakpointsWindow(IntPtr instance, PluginInterop plugin)
    {
        _instance = instance;
        _plugin = plugin;
        InitializeComponent();
        Loaded += (_, _) => ThemeHelper.ApplyTitleBar(this, ThemeHelper.IsDarkMode);
        Refresh();
    }

    private void NotifyOwnerBreakpointsChanged()
        => (Owner as MainWindow)?.SyncBreakpointCacheFromPlugin();

    public void Refresh()
    {
        EntryList.Items.Clear();
        if (_instance == IntPtr.Zero) return;

        Brush fgNormal = ResBrush("ThemeForeground", Brushes.White);
        Brush fgCond = ResBrush("ThemeDimFg", Brushes.Gray);
        Brush fgHeader = ResBrush("ThemeRegHeaderFg", Brushes.Teal);
        Brush fgWatch = ResBrush("ThemeWarningFg", Brushes.Orange);
        Brush fgEdit = ResBrush("ThemeAccent", Brushes.DodgerBlue);
        Brush fgDelete = ResBrush("ThemeBreakpointFg", Brushes.Red);

        // ---- Breakpoints ----
        var bps = new EmfeBreakpointInfo[128];
        int bpCount = _plugin.emfe_get_breakpoints(_instance, bps, bps.Length);
        Array.Sort(bps, 0, bpCount, Comparer<EmfeBreakpointInfo>.Create((a, b) => a.address.CompareTo(b.address)));
        if (bpCount > 0)
            EntryList.Items.Add(MakeHeader("Breakpoints", fgHeader));
        for (int i = 0; i < bpCount; i++)
        {
            uint addr = (uint)bps[i].address;
            bool enabled = bps[i].enabled;
            string? cond = Marshal.PtrToStringAnsi(bps[i].condition);
            EntryList.Items.Add(MakeBreakpointRow(addr, enabled, cond, fgNormal, fgCond, fgEdit, fgDelete));
        }

        // ---- Watchpoints ----
        var wps = new EmfeWatchpointInfo[128];
        int wpCount = _plugin.emfe_get_watchpoints(_instance, wps, wps.Length);
        Array.Sort(wps, 0, wpCount, Comparer<EmfeWatchpointInfo>.Create((a, b) => a.address.CompareTo(b.address)));
        if (wpCount > 0)
            EntryList.Items.Add(MakeHeader("Watchpoints", fgHeader));
        for (int i = 0; i < wpCount; i++)
        {
            uint addr = (uint)wps[i].address;
            bool enabled = wps[i].enabled;
            string? cond = Marshal.PtrToStringAnsi(wps[i].condition);
            EntryList.Items.Add(MakeWatchpointRow(addr, wps[i].size, wps[i].type, enabled, cond,
                fgWatch, fgCond, fgEdit, fgDelete));
        }
    }

    private TextBlock MakeHeader(string text, Brush fg) => new()
    {
        Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold,
        Foreground = fg, Margin = new Thickness(8, 4, 0, 2)
    };

    private Grid MakeBreakpointRow(uint addr, bool enabled, string? cond,
        Brush fgNormal, Brush fgCond, Brush fgEdit, Brush fgDelete)
    {
        var grid = BaseRowGrid();
        bool isActive = (Owner is MainWindow mw &&
            mw.LastStopReason == EmfeStopReason.Breakpoint && mw.LastStopAddress == addr);
        if (isActive)
            grid.SetResourceReference(Grid.BackgroundProperty, "ThemeCheckedBg");
        grid.MouseEnter += (_, _) => grid.SetResourceReference(Grid.BackgroundProperty, "ThemeMenuHover");
        grid.MouseLeave += (_, _) =>
        {
            if (isActive) grid.SetResourceReference(Grid.BackgroundProperty, "ThemeCheckedBg");
            else grid.Background = System.Windows.Media.Brushes.Transparent;
        };
        grid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) (Owner as MainWindow)?.ScrollDisassemblyTo(addr);
        };

        var cb = new CheckBox { IsChecked = enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        cb.Checked += (_, _) => { _plugin.emfe_enable_breakpoint(_instance, addr, true); NotifyOwnerBreakpointsChanged(); };
        cb.Unchecked += (_, _) => { _plugin.emfe_enable_breakpoint(_instance, addr, false); NotifyOwnerBreakpointsChanged(); };
        Grid.SetColumn(cb, 0); grid.Children.Add(cb);

        var stack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = $"${addr:X8}", FontFamily = _consolas, FontSize = 13,
            Foreground = fgNormal, Margin = new Thickness(4, 0, 0, 0)
        });
        if (!string.IsNullOrEmpty(cond))
            stack.Children.Add(new TextBlock
            {
                Text = $"  if {cond}", FontFamily = _consolas, FontSize = 11,
                Foreground = fgCond, Margin = new Thickness(4, 0, 0, 0)
            });
        Grid.SetColumn(stack, 1); grid.Children.Add(stack);

        var editBtn = new Button
        {
            Content = "Edit Condition...", FontSize = 11, Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = fgEdit
        };
        editBtn.Click += (_, _) =>
        {
            var dlg = new EditConditionDialog(addr, cond ?? "") { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _plugin.emfe_set_breakpoint_condition(_instance, addr,
                    string.IsNullOrWhiteSpace(dlg.Condition) ? null : dlg.Condition);
                Refresh();
                NotifyOwnerBreakpointsChanged();
            }
        };
        Grid.SetColumn(editBtn, 2); grid.Children.Add(editBtn);

        var delBtn = new Button
        {
            Content = "Delete", FontSize = 11, Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, Foreground = fgDelete
        };
        delBtn.Click += (_, _) =>
        {
            _plugin.emfe_remove_breakpoint(_instance, addr);
            Refresh();
            NotifyOwnerBreakpointsChanged();
        };
        Grid.SetColumn(delBtn, 3); grid.Children.Add(delBtn);

        return grid;
    }

    private Grid MakeWatchpointRow(uint addr, EmfeWatchpointSize size, EmfeWatchpointType type,
        bool enabled, string? cond, Brush fgWatch, Brush fgCond, Brush fgEdit, Brush fgDelete)
    {
        var grid = BaseRowGrid();
        bool isActive = (Owner is MainWindow mw &&
            mw.LastStopReason == EmfeStopReason.Watchpoint && mw.LastStopAddress == addr);
        if (isActive)
            grid.SetResourceReference(Grid.BackgroundProperty, "ThemeCheckedBg");
        grid.MouseEnter += (_, _) => grid.SetResourceReference(Grid.BackgroundProperty, "ThemeMenuHover");
        grid.MouseLeave += (_, _) =>
        {
            if (isActive) grid.SetResourceReference(Grid.BackgroundProperty, "ThemeCheckedBg");
            else grid.Background = System.Windows.Media.Brushes.Transparent;
        };

        var cb = new CheckBox { IsChecked = enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        cb.Checked += (_, _) => { _plugin.emfe_enable_watchpoint(_instance, addr, true); NotifyOwnerBreakpointsChanged(); };
        cb.Unchecked += (_, _) => { _plugin.emfe_enable_watchpoint(_instance, addr, false); NotifyOwnerBreakpointsChanged(); };
        Grid.SetColumn(cb, 0); grid.Children.Add(cb);

        string sizeStr = size switch
        {
            EmfeWatchpointSize.Byte => ".B",
            EmfeWatchpointSize.Long => ".L",
            _ => ".W"
        };
        string typeStr = type switch
        {
            EmfeWatchpointType.Read => "R",
            EmfeWatchpointType.Write => "W",
            _ => "RW"
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = $"${addr:X8}{sizeStr} [{typeStr}]", FontFamily = _consolas, FontSize = 13,
            Foreground = fgWatch, Margin = new Thickness(4, 0, 0, 0)
        });
        if (!string.IsNullOrEmpty(cond))
            stack.Children.Add(new TextBlock
            {
                Text = $"  if {cond}", FontFamily = _consolas, FontSize = 11,
                Foreground = fgCond, Margin = new Thickness(4, 0, 0, 0)
            });
        Grid.SetColumn(stack, 1); grid.Children.Add(stack);

        var editBtn = new Button
        {
            Content = "Edit...", FontSize = 11, Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = fgEdit
        };
        editBtn.Click += (_, _) =>
        {
            var dlg = new AddWatchpointDialog(addr, size, type, cond ?? "") { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _plugin.emfe_remove_watchpoint(_instance, addr);
                _plugin.emfe_add_watchpoint(_instance, dlg.Address, dlg.Size, dlg.Type);
                if (!string.IsNullOrWhiteSpace(dlg.Condition))
                    _plugin.emfe_set_watchpoint_condition(_instance, dlg.Address, dlg.Condition);
                Refresh();
                NotifyOwnerBreakpointsChanged();
            }
        };
        Grid.SetColumn(editBtn, 2); grid.Children.Add(editBtn);

        var delBtn = new Button
        {
            Content = "Delete", FontSize = 11, Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right, Foreground = fgDelete
        };
        delBtn.Click += (_, _) =>
        {
            _plugin.emfe_remove_watchpoint(_instance, addr);
            Refresh();
            NotifyOwnerBreakpointsChanged();
        };
        Grid.SetColumn(delBtn, 3); grid.Children.Add(delBtn);

        return grid;
    }

    private static Grid BaseRowGrid()
    {
        var grid = new Grid { Margin = new Thickness(2, 1, 2, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    private static Brush ResBrush(string key, Brush fallback)
        => (Brush)(Application.Current.TryFindResource(key) ?? fallback);

    private void OnAddWatchpoint(object sender, RoutedEventArgs e)
    {
        var dlg = new AddWatchpointDialog { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            var res = _plugin.emfe_add_watchpoint(_instance, dlg.Address, dlg.Size, dlg.Type);
            if (res != EmfeResult.OK)
            {
                MessageBox.Show(this, $"Failed to add watchpoint: {res}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (!string.IsNullOrWhiteSpace(dlg.Condition))
            {
                _plugin.emfe_set_watchpoint_condition(_instance, dlg.Address, dlg.Condition);
            }
            Refresh();
            NotifyOwnerBreakpointsChanged();
        }
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        _plugin.emfe_clear_breakpoints(_instance);
        _plugin.emfe_clear_watchpoints(_instance);
        Refresh();
        NotifyOwnerBreakpointsChanged();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
