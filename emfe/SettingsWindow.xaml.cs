using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace emfe;

public partial class SettingsWindow : Window
{
    private readonly IntPtr _instance;
    private readonly PluginInterop _plugin;
    private readonly List<(string Key, EmfeSettingType Type, FrameworkElement Control)> _settingControls = new();

    // Staged edit model for EMFE_SETTING_LIST settings. Populated lazily the
    // first time a list is rendered in the dialog; every add/remove/field
    // edit goes here. Applied to the plugin only on OK (before
    // emfe_apply_settings).
    private readonly Dictionary<string, List<Dictionary<string, string>>> _pendingLists = new();

    public SettingsWindow(IntPtr instance, PluginInterop plugin)
    {
        _instance = instance;
        _plugin = plugin;
        InitializeComponent();
        BuildSettingsUI();
        Loaded += (_, _) => ThemeHelper.ApplyTitleBar(this, ThemeHelper.IsDarkMode);
    }

    private void BuildSettingsUI()
    {
        if (_instance == IntPtr.Zero) return;

        int count = _plugin.emfe_get_setting_defs(_instance, out var defsPtr);
        if (count <= 0) return;

        int structSize = Marshal.SizeOf<EmfeSettingDef>();
        var allDefs = new List<EmfeSettingDef>();
        for (int i = 0; i < count; i++)
            allDefs.Add(Marshal.PtrToStructure<EmfeSettingDef>(defsPtr + i * structSize));

        var groups = new List<string>();
        foreach (var def in allDefs)
        {
            var g = Marshal.PtrToStringAnsi(def.group) ?? "";
            if (!groups.Contains(g)) groups.Add(g);
        }

        foreach (var group in groups)
        {
            var panel = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };

            foreach (var def in allDefs)
            {
                var g = Marshal.PtrToStringAnsi(def.group) ?? "";
                if (g != group) continue;

                string key = Marshal.PtrToStringAnsi(def.key) ?? "";
                string label = Marshal.PtrToStringAnsi(def.label) ?? key;
                string? dependsOn = Marshal.PtrToStringAnsi(def.depends_on);
                string? dependsValue = Marshal.PtrToStringAnsi(def.depends_value);

                if (!IsSettingVisible(allDefs, def)) continue;

                string currentVal = Marshal.PtrToStringAnsi(_plugin.emfe_get_setting(_instance, key)) ?? "";
                string? constraints = Marshal.PtrToStringAnsi(def.constraints);

                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
                row.Children.Add(new TextBlock
                {
                    Text = label, Width = 180, VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 13
                });

                // Pending (deferred) indicator: REQUIRES_RESET setting whose staged
                // value differs from the currently-applied value.
                bool isPending = false;
                if ((def.flags & EmfeSettingFlags.REQUIRES_RESET) != 0)
                {
                    string appliedVal = Marshal.PtrToStringAnsi(
                        _plugin.emfe_get_applied_setting(_instance, key)) ?? "";
                    if (appliedVal != currentVal) isPending = true;
                }
                if (isPending)
                {
                    var pendingMark = new TextBlock
                    {
                        Text = "*",
                        FontSize = 14,
                        FontWeight = FontWeights.Bold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(-6, 0, 4, 0),
                        Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x99, 0x00)),
                        ToolTip = "This change is staged but not yet applied. It will take effect on the next full reset or when emfe restarts."
                    };
                    row.Children.Add(pendingMark);
                }

                FrameworkElement? control = null;
                switch (def.type)
                {
                    case EmfeSettingType.Bool:
                    {
                        var cb = new CheckBox
                        {
                            IsChecked = currentVal == "true" || currentVal == "1",
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        row.Children.Add(cb);
                        control = cb;
                        break;
                    }
                    case EmfeSettingType.Combo:
                    {
                        var combo = new ComboBox { Width = 180, FontSize = 13 };
                        if (constraints != null)
                        {
                            foreach (var item in constraints.Split('|'))
                            {
                                combo.Items.Add(item);
                                if (item == currentVal) combo.SelectedItem = item;
                            }
                        }
                        combo.SelectionChanged += OnComboChanged;
                        row.Children.Add(combo);
                        control = combo;
                        break;
                    }
                    case EmfeSettingType.Int:
                    {
                        var box = new TextBox
                        {
                            Text = currentVal, Width = 100, FontFamily = new FontFamily("Consolas"),
                            FontSize = 13
                        };
                        row.Children.Add(box);
                        if (constraints != null)
                        {
                            var hint = new TextBlock
                            {
                                Text = $"({constraints})", FontSize = 11,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(8, 0, 0, 0)
                            };
                            hint.SetResourceReference(TextBlock.ForegroundProperty, "ThemeDimFg");
                            row.Children.Add(hint);
                        }
                        control = box;
                        break;
                    }
                    case EmfeSettingType.List:
                    {
                        BuildListControl(panel, key, label);
                        continue;
                    }
                    case EmfeSettingType.File:
                    {
                        var box = new TextBox
                        {
                            Text = currentVal, Width = 260, FontSize = 13
                        };
                        row.Children.Add(box);
                        var browseBtn = new Button
                        {
                            Content = "...", FontSize = 11,
                            Padding = new Thickness(6, 1, 6, 1),
                            Margin = new Thickness(4, 0, 0, 0)
                        };
                        browseBtn.Click += (_, _) =>
                        {
                            var fileDlg = new Microsoft.Win32.OpenFileDialog { Filter = "All Files|*.*" };
                            if (!string.IsNullOrEmpty(box.Text))
                            {
                                try { fileDlg.InitialDirectory = System.IO.Path.GetDirectoryName(box.Text); }
                                catch { }
                            }
                            if (fileDlg.ShowDialog() == true) box.Text = fileDlg.FileName;
                        };
                        row.Children.Add(browseBtn);
                        control = box;
                        break;
                    }
                    case EmfeSettingType.String:
                    default:
                    {
                        var box = new TextBox
                        {
                            Text = currentVal, Width = 300, FontSize = 13
                        };
                        row.Children.Add(box);
                        control = box;
                        break;
                    }
                }

                if (control != null)
                    _settingControls.Add((key, def.type, control));

                panel.Children.Add(row);
            }

            var tab = new TabItem { Header = group };
            if (panel.Children.Count > 0)
            {
                tab.Content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = panel
                };
            }
            else
            {
                tab.Content = new TextBlock
                {
                    Text = "No settings available for this tab.",
                    FontSize = 12, Margin = new Thickness(12, 16, 0, 0),
                    Foreground = (Brush)(Application.Current.TryFindResource("ThemeDimFg") ?? Brushes.Gray)
                };
            }
            SettingsTabs.Items.Add(tab);
        }

        if (SettingsTabs.Items.Count > 0)
            SettingsTabs.SelectedIndex = 0;
    }

    // Lazily snapshot the plugin's current list into `_pendingLists`. All
    // dialog mutations work against the snapshot; the plugin is only touched
    // on OK via `ApplyStagedListsToPlugin`.
    private void EnsureListStaged(string listKey)
    {
        if (_pendingLists.ContainsKey(listKey)) return;
        var items = new List<Dictionary<string, string>>();
        int nFields = _plugin.emfe_get_list_item_defs(_instance, listKey, out var defsPtr);
        var fieldKeys = new List<string>();
        for (int f = 0; f < nFields; f++)
        {
            var def = Marshal.PtrToStructure<EmfeListItemDef>(
                defsPtr + f * Marshal.SizeOf<EmfeListItemDef>());
            var fk = Marshal.PtrToStringAnsi(def.key);
            if (!string.IsNullOrEmpty(fk)) fieldKeys.Add(fk);
        }
        int nItems = _plugin.emfe_get_list_item_count(_instance, listKey);
        for (int i = 0; i < nItems; i++)
        {
            var item = new Dictionary<string, string>();
            foreach (var fk in fieldKeys)
            {
                var raw = Marshal.PtrToStringAnsi(
                    _plugin.emfe_get_list_item_field(_instance, listKey, i, fk));
                item[fk] = raw ?? "";
            }
            items.Add(item);
        }
        _pendingLists[listKey] = items;
    }

    // Push the staged list state into the plugin by wiping and rebuilding.
    // Called from OnOK just before emfe_apply_settings so list and other
    // staged settings commit together.
    private void ApplyStagedListsToPlugin()
    {
        foreach (var (listKey, items) in _pendingLists)
        {
            int existing = _plugin.emfe_get_list_item_count(_instance, listKey);
            for (int i = existing - 1; i >= 0; i--)
                _plugin.emfe_remove_list_item(_instance, listKey, i);
            foreach (var item in items)
            {
                int idx = _plugin.emfe_add_list_item(_instance, listKey);
                if (idx < 0) continue;
                foreach (var (field, val) in item)
                    _plugin.emfe_set_list_item_field(_instance, listKey, idx, field, val);
            }
        }
    }

    private HashSet<int> GetUsedScsiIds(string listKey, int excludeIdx)
    {
        var used = new HashSet<int>();

        // CD-ROM ID — check staged value (a ComboBox in _settingControls)
        // first, fall back to the committed plugin value if the user hasn't
        // edited it this session.
        string cdromId = "";
        foreach (var (key, _, control) in _settingControls)
        {
            if (key == "Mvme147ScsiCdromId" && control is ComboBox combo && combo.SelectedItem != null)
            {
                cdromId = combo.SelectedItem.ToString() ?? "";
                break;
            }
        }
        if (string.IsNullOrEmpty(cdromId))
            cdromId = Marshal.PtrToStringAnsi(_plugin.emfe_get_setting(_instance, "Mvme147ScsiCdromId")) ?? "3";
        if (int.TryParse(cdromId, out int cid)) used.Add(cid);

        // Other disk entries — read from the edit model (NOT the plugin).
        if (_pendingLists.TryGetValue(listKey, out var items))
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (i == excludeIdx) continue;
                if (items[i].TryGetValue("ScsiId", out var sid) &&
                    int.TryParse(sid, out int id)) used.Add(id);
            }
        }
        return used;
    }

    private void BuildListControl(StackPanel parent, string listKey, string label)
    {
        EnsureListStaged(listKey);
        var items = _pendingLists[listKey];

        var header = new TextBlock
        {
            Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 4)
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "ThemeRegHeaderFg");
        parent.Children.Add(header);

        for (int idx = 0; idx < items.Count; idx++)
        {
            var itemRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            int capturedIdx = idx;

            // SCSI ID (ComboBox, excluding used IDs from the edit model)
            int currentId = 0;
            if (items[idx].TryGetValue("ScsiId", out var sid) &&
                int.TryParse(sid, out int cid)) currentId = cid;
            var usedIds = GetUsedScsiIds(listKey, idx);
            var idCombo = new ComboBox { Width = 45, FontSize = 13 };
            for (int id = 0; id <= 7; id++)
            {
                if (!usedIds.Contains(id) || id == currentId)
                {
                    idCombo.Items.Add(id.ToString());
                    if (id == currentId) idCombo.SelectedItem = id.ToString();
                }
            }
            idCombo.SelectionChanged += (_, _) =>
            {
                if (idCombo.SelectedItem is string sel &&
                    _pendingLists.TryGetValue(listKey, out var editItems) &&
                    capturedIdx < editItems.Count)
                {
                    editItems[capturedIdx]["ScsiId"] = sel;
                    RebuildUI();
                }
            };
            itemRow.Children.Add(new TextBlock
            {
                Text = "ID:", Width = 25, VerticalAlignment = VerticalAlignment.Center, FontSize = 12
            });
            itemRow.Children.Add(idCombo);

            // Path (TextBox + Browse button)
            string path = items[idx].TryGetValue("Path", out var p) ? p : "";
            var pathBox = new TextBox { Text = path, Width = 220, FontSize = 13, Margin = new Thickness(8, 0, 0, 0) };
            pathBox.LostFocus += (_, _) =>
            {
                if (_pendingLists.TryGetValue(listKey, out var editItems) &&
                    capturedIdx < editItems.Count)
                    editItems[capturedIdx]["Path"] = pathBox.Text;
            };
            itemRow.Children.Add(pathBox);

            var browseBtn = new Button { Content = "...", FontSize = 11, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(4, 0, 0, 0) };
            browseBtn.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Disk Images|*.img;*.raw;*.iso|All Files|*.*" };
                if (!string.IsNullOrEmpty(pathBox.Text))
                    dlg.InitialDirectory = System.IO.Path.GetDirectoryName(pathBox.Text);
                if (dlg.ShowDialog() == true)
                {
                    pathBox.Text = dlg.FileName;
                    if (_pendingLists.TryGetValue(listKey, out var editItems) &&
                        capturedIdx < editItems.Count)
                        editItems[capturedIdx]["Path"] = dlg.FileName;
                }
            };
            itemRow.Children.Add(browseBtn);

            // Remove button
            var removeBtn = new Button { Content = "×", FontSize = 13, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0) };
            removeBtn.Click += (_, _) =>
            {
                if (_pendingLists.TryGetValue(listKey, out var editItems) &&
                    capturedIdx < editItems.Count)
                    editItems.RemoveAt(capturedIdx);
                RebuildUI();
            };
            itemRow.Children.Add(removeBtn);

            parent.Children.Add(itemRow);
        }

        var addBtn = new Button { Content = "+ Add Disk", FontSize = 11, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 4, 0, 0) };
        addBtn.Click += (_, _) =>
        {
            SaveToStaging();
            if (!_pendingLists.TryGetValue(listKey, out var editItems)) return;

            var usedAll = GetUsedScsiIds(listKey, -1);
            int freeId = 0;
            while (usedAll.Contains(freeId) && freeId <= 7) freeId++;

            var newItem = new Dictionary<string, string>();
            if (freeId <= 7) newItem["ScsiId"] = freeId.ToString();
            newItem["Path"] = "";
            editItems.Add(newItem);

            RebuildUI();
        };
        parent.Children.Add(addBtn);
    }

    private int _lastValidTabIndex;

    private bool IsSettingVisible(List<EmfeSettingDef> allDefs, EmfeSettingDef def)
    {
        string? dependsOn = Marshal.PtrToStringAnsi(def.depends_on);
        string? dependsValue = Marshal.PtrToStringAnsi(def.depends_value);
        if (dependsOn == null || dependsValue == null) return true;

        var depVal = Marshal.PtrToStringAnsi(_plugin.emfe_get_setting(_instance, dependsOn)) ?? "";
        if (depVal != dependsValue) return false;

        // Check if the dependency itself is visible (chain check)
        foreach (var parentDef in allDefs)
        {
            string parentKey = Marshal.PtrToStringAnsi(parentDef.key) ?? "";
            if (parentKey == dependsOn)
                return IsSettingVisible(allDefs, parentDef);
        }
        return true;
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsTabs.SelectedItem is TabItem tab && !tab.IsEnabled)
        {
            SettingsTabs.SelectedIndex = _lastValidTabIndex;
        }
        else
        {
            _lastValidTabIndex = SettingsTabs.SelectedIndex;
        }
    }

    private bool _rebuildingUI;

    private void OnComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuildingUI) return;
        SaveToStaging();
        RebuildUI();
    }

    private void SaveToStaging()
    {
        foreach (var (key, type, control) in _settingControls)
        {
            string? val = type switch
            {
                EmfeSettingType.Bool => ((CheckBox)control).IsChecked == true ? "true" : "false",
                EmfeSettingType.Combo => ((ComboBox)control).SelectedItem?.ToString(),
                _ => ((TextBox)control).Text
            };
            if (val != null)
                _plugin.emfe_set_setting(_instance, key, val);
        }
    }

    private void RebuildUI()
    {
        _rebuildingUI = true;
        int selectedTab = SettingsTabs.SelectedIndex;
        string? selectedHeader = (SettingsTabs.SelectedItem as TabItem)?.Header as string;
        _settingControls.Clear();
        SettingsTabs.Items.Clear();
        BuildSettingsUI();
        // Restore selected tab by header name
        if (selectedHeader != null)
        {
            for (int i = 0; i < SettingsTabs.Items.Count; i++)
            {
                if (SettingsTabs.Items[i] is TabItem ti && ti.Header as string == selectedHeader)
                { SettingsTabs.SelectedIndex = i; break; }
            }
        }
        _rebuildingUI = false;
    }

    private void OnOK(object sender, RoutedEventArgs e)
    {
        foreach (var (key, type, control) in _settingControls)
        {
            string? val = null;
            switch (type)
            {
                case EmfeSettingType.Bool:
                    val = ((CheckBox)control).IsChecked == true ? "true" : "false";
                    break;
                case EmfeSettingType.Combo:
                    val = ((ComboBox)control).SelectedItem?.ToString();
                    break;
                default:
                    val = ((TextBox)control).Text;
                    break;
            }
            if (val != null)
                _plugin.emfe_set_setting(_instance, key, val);
        }
        ApplyStagedListsToPlugin();
        _plugin.emfe_apply_settings(_instance);
        _plugin.emfe_save_settings(_instance);
        _pendingLists.Clear();
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _pendingLists.Clear();  // discard list edits
        DialogResult = false;
        Close();
    }
}
