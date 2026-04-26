using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace emfe;

public partial class MainWindow : Window
{
    // Routed commands for key bindings
    public static readonly RoutedCommand RunCommand = new();
    public static readonly RoutedCommand StopCommand = new();
    public static readonly RoutedCommand StepCommand = new();
    public static readonly RoutedCommand StepOverCommand = new();
    public static readonly RoutedCommand StepOutCommand = new();
    public static readonly RoutedCommand LoadElfCommand = new();

    private IntPtr _instance;
    private PluginInterop? _plugin;
    private string _loadedPluginStem = "";  // DLL filename without extension
    private ulong _capabilities;  // EmfeCap.* bitmask reported by current plugin
    private readonly List<RegUIEntry> _regEntries = new();
    private readonly List<FlagCheckEntry> _flagEntries = new();
    private readonly Dictionary<uint, bool> _breakpointAddresses = new();
    private readonly List<uint> _disasmAddresses = new();
    private readonly List<string> _disasmTexts = new();
    // One-shot execution breakpoints installed by "Run to here".  Removed
    // automatically when the CPU stops at the target address.
    private readonly HashSet<uint> _tempBreakpoints = new();
    private uint _memoryAddress;
    private EmfeStateChangeCallback? _stateChangeCb;
    private EmfeConsoleCharCallback? _consoleCharCb;
    private Button? _btnRegEdit, _btnRegApply, _btnRegCancel;
    private ConsoleWindow? _consoleWindow;
    private readonly Queue<char> _pendingConsoleChars = new();
    private readonly object _pendingConsoleLock = new();
    private BreakpointsWindow? _breakpointsWindow;
    private CallStackWindow? _callStackWindow;
    private FramebufferWindow? _framebufferWindow;
    private uint _lastStopAddress;
    private EmfeStopReason _lastStopReason;
    private string? _lastLoadedFilePath;
    private string _lastLoadedFileType = ""; // "elf", "srec", "binary"

    // Periodic MHz/MIPS update while the emulator is running, ported from
    // emfe_WinUI3Cpp's MainWindow: every ~500ms sample cycle/instruction
    // counters, compute rate over the interval, and refresh the toolbar
    // text. Click CyclesText to cycle through three views so all three
    // fit in the toolbar without overflow:
    //   0 = Cycles / Instrs
    //   1 = MHz / MIPS (instantaneous over the last 500 ms)
    //   2 = avg MHz / MIPS (since the current Run started)
    private System.Windows.Threading.DispatcherTimer? _statsTimer;
    private System.Diagnostics.Stopwatch _statsClock = new();
    private long _statsLastTicks;
    private long _statsLastCycles;
    private long _statsLastInstrs;
    private long _runStartTicks;
    private long _runStartCycles;
    private long _runStartInstrs;
    private double _instMhz, _instMips, _avgMhz, _avgMips;
    private int _statsViewMode;

    private record RegUIEntry(uint RegId, uint BitWidth, EmfeRegType Type, TextBox ValueBox);
    private record FlagCheckEntry(uint RegId, byte BitIndex, CheckBox CheckBox);
    private uint _pcRegId = 16; // default m68030, updated from plugin register defs
    private uint _spRegId = 15; // default m68030, updated from plugin register defs
    private int _addrDigits = 8; // hex digits for address display (4 or 8)

    private sealed class MemCell
    {
        public FrameworkElement Box = null!;   // TextBlock in view mode, TextBox in edit mode
        public TextBlock AsciiBlock = null!;
        public uint Address;
        public byte Original;
        public int Row;
        public int Col;
    }
    private readonly List<MemCell> _memCells = new();
    private readonly List<TextBlock> _memAsciiBlocks = new();
    private bool _memEditMode;
    private uint _memBuiltAddress;
    private int _memBuiltSize;
    private bool _memBuiltEditMode;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"emfe - Emulator Frontend [{GitVersion.CommitHash}]";
        Loaded += OnMainWindowLoaded;

        CommandBindings.Add(new CommandBinding(RunCommand, (_, _) => OnRun(this, new RoutedEventArgs())));
        CommandBindings.Add(new CommandBinding(StopCommand, (_, _) => OnStop(this, new RoutedEventArgs())));
        CommandBindings.Add(new CommandBinding(StepCommand, (_, _) => OnStep(this, new RoutedEventArgs())));
        CommandBindings.Add(new CommandBinding(StepOverCommand, (_, _) => OnStepOver(this, new RoutedEventArgs())));
        CommandBindings.Add(new CommandBinding(StepOutCommand, (_, _) => OnStepOut(this, new RoutedEventArgs())));
        CommandBindings.Add(new CommandBinding(LoadElfCommand, (_, _) => OnLoadElf(this, new RoutedEventArgs())));

        StartStatsTimer();
    }

    private void StartStatsTimer()
    {
        if (_statsTimer != null) return;
        _statsClock.Start();
        _statsTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statsTimer.Tick += (_, _) => UpdateStatsDisplay();
        _statsTimer.Start();
    }

    private void ResetRunStatsBaseline()
    {
        if (_instance == IntPtr.Zero || _plugin == null) return;
        _statsLastTicks = 0;
        _runStartTicks = _statsClock.ElapsedTicks;
        _runStartCycles = _plugin.emfe_get_cycle_count(_instance);
        _runStartInstrs = _plugin.emfe_get_instruction_count(_instance);
        _instMhz = _instMips = 0;
        _avgMhz = _avgMips = 0;
    }

    private void UpdateStatsDisplay()
    {
        if (_instance == IntPtr.Zero || _plugin == null) return;

        // UpdateRegisters() already repaints the toolbar on stop/step/breakpoint.
        // Only recompute rates while actually executing — otherwise a stale
        // snapshot would produce nonsense MHz numbers.
        var state = _plugin.emfe_get_state(_instance);
        bool running = state == EmfeState.Running;

        long cycles = _plugin.emfe_get_cycle_count(_instance);
        long instrs = _plugin.emfe_get_instruction_count(_instance);
        long nowTicks = _statsClock.ElapsedTicks;

        if (running)
        {
            if (_statsLastTicks != 0)
            {
                double seconds = (nowTicks - _statsLastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                if (seconds >= 0.001)
                {
                    _instMhz = (cycles - _statsLastCycles) / seconds / 1_000_000.0;
                    _instMips = (instrs - _statsLastInstrs) / seconds / 1_000_000.0;
                }
            }
            if (_runStartTicks != 0)
            {
                double runSec = (nowTicks - _runStartTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                if (runSec >= 0.01)
                {
                    _avgMhz = (cycles - _runStartCycles) / runSec / 1_000_000.0;
                    _avgMips = (instrs - _runStartInstrs) / runSec / 1_000_000.0;
                }
            }
            _statsLastTicks = nowTicks;
            _statsLastCycles = cycles;
            _statsLastInstrs = instrs;
        }

        CyclesText.Text = _statsViewMode switch
        {
            1 => $"{_instMhz:F2} MHz ({_instMips:F2} MIPS)",
            2 => $"avg {_avgMhz:F2} MHz ({_avgMips:F2} MIPS)",
            _ => $"Cycles: {cycles}  Instrs: {instrs}",
        };
    }

    private void OnCyclesTextClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _statsViewMode = (_statsViewMode + 1) % 3;
        UpdateStatsDisplay();  // repaint immediately, don't wait for the next tick
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        ThemeHelper.ApplyTitleBar(this, ThemeHelper.IsDarkMode);
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                LoadPlugin();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"LoadPlugin failed: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"LoadPlugin exception: {ex}");
            }
        });
    }

    // ========================================================================
    // Plugin loading
    // ========================================================================

    // Scan the plugins\ subdirectory next to the exe for emfe_plugin_*.dll.
    private static string[] ScanPlugins()
    {
        var pluginsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!System.IO.Directory.Exists(pluginsDir))
            return Array.Empty<string>();
        var files = System.IO.Directory.GetFiles(pluginsDir, "emfe_plugin_*.dll");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
    }

    private static string GetAppSettingsPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return System.IO.Path.Combine(localAppData, "emfe_CsWPF", "appsettings.json");
    }

    private static string? ReadSavedPluginPath()
    {
        var path = GetAppSettingsPath();
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            var content = System.IO.File.ReadAllText(path);
            var key = "\"PluginPath\"";
            var pos = content.IndexOf(key, StringComparison.Ordinal);
            if (pos < 0) return null;
            var colon = content.IndexOf(':', pos + key.Length);
            if (colon < 0) return null;
            var q1 = content.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            var q2 = content.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            var val = content.Substring(q1 + 1, q2 - q1 - 1).Replace("\\\\", "\\");
            return string.IsNullOrEmpty(val) ? null : val;
        }
        catch { return null; }
    }

    private static void SavePluginPath(string pluginPath)
    {
        try
        {
            var path = GetAppSettingsPath();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            string content = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";
            var escaped = pluginPath.Replace("\\", "\\\\");
            var key = "\"PluginPath\"";
            var pos = content.IndexOf(key, StringComparison.Ordinal);
            if (pos >= 0)
            {
                var colon = content.IndexOf(':', pos + key.Length);
                var q1 = colon >= 0 ? content.IndexOf('"', colon + 1) : -1;
                var q2 = q1 >= 0 ? content.IndexOf('"', q1 + 1) : -1;
                if (q1 >= 0 && q2 >= 0)
                    content = content.Substring(0, q1 + 1) + escaped + content.Substring(q2);
            }
            else if (!string.IsNullOrWhiteSpace(content))
            {
                var brace = content.LastIndexOf('}');
                if (brace >= 0)
                {
                    var lastNonSpace = content.LastIndexOfAny(new[] { '{', ',', '"', '}', ']' }, brace - 1);
                    var prefix = (lastNonSpace >= 0 && content[lastNonSpace] != '{' && content[lastNonSpace] != ',')
                                 ? "," : "";
                    content = content.Substring(0, brace)
                              + prefix + "\n    \"PluginPath\": \"" + escaped + "\"\n"
                              + content.Substring(brace);
                }
            }
            else
            {
                content = "{\n    \"PluginPath\": \"" + escaped + "\"\n}\n";
            }
            System.IO.File.WriteAllText(path, content);
        }
        catch { /* best-effort persistence */ }
    }

    // Build "BoardName (CpuName) [filename]" display strings for the dialog.
    private static string[] BuildPluginDisplayNames(string[] paths)
    {
        var result = new string[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            var filename = System.IO.Path.GetFileName(paths[i]);
            string label;
            try
            {
                using var probe = new PluginInterop();
                if (probe.Load(paths[i]))
                {
                    var nego = new EmfeNegotiateInfo { api_version_major = 1, api_version_minor = 0 };
                    if (probe.emfe_negotiate(ref nego) == EmfeResult.OK &&
                        probe.emfe_get_board_info(out var info) == EmfeResult.OK)
                    {
                        var board = info.BoardName ?? "Unknown";
                        var cpu = info.CpuName ?? "";
                        label = string.IsNullOrEmpty(cpu)
                                ? $"{board}  [{filename}]"
                                : $"{board} ({cpu})  [{filename}]";
                    }
                    else
                    {
                        label = $"{filename}  (negotiate failed)";
                    }
                }
                else
                {
                    label = $"{filename}  (load error)";
                }
            }
            catch
            {
                label = filename;
            }
            result[i] = label;
        }
        return result;
    }

    private void OnSwitchPlugin(object sender, RoutedEventArgs e)
    {
        var pluginFiles = ScanPlugins();
        if (pluginFiles.Length == 0)
        {
            StatusText.Text = "No plugin DLLs found in plugins\\ directory";
            return;
        }
        var displayNames = BuildPluginDisplayNames(pluginFiles);
        var current = ReadSavedPluginPath();
        var selectWindow = new PluginSelectWindow(pluginFiles, displayNames, current)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        if (selectWindow.ShowDialog() != true || selectWindow.SelectedPath == null) return;

        // Stop and destroy the current instance before loading the new one.
        if (_instance != IntPtr.Zero && _plugin != null)
        {
            _plugin.emfe_stop(_instance);
            _plugin.emfe_destroy(_instance);
            _instance = IntPtr.Zero;
        }
        _plugin?.Dispose();
        _plugin = null;
        _loadedPluginStem = "";

        if (!LoadPluginFromPath(selectWindow.SelectedPath))
            StatusText.Text = $"Failed to load {System.IO.Path.GetFileName(selectWindow.SelectedPath)}";
    }

    private void LoadPlugin()
    {
        var pluginFiles = ScanPlugins();
        if (pluginFiles.Length == 0)
        {
            StatusText.Text = "No plugin DLLs found — place emfe_plugin_*.dll in the plugins\\ directory next to emfe.exe";
            return;
        }

        // Try last-used plugin first
        var saved = ReadSavedPluginPath();
        if (!string.IsNullOrEmpty(saved))
        {
            foreach (var p in pluginFiles)
            {
                if (System.IO.File.Exists(p) && System.IO.File.Exists(saved) &&
                    string.Equals(System.IO.Path.GetFullPath(p),
                                  System.IO.Path.GetFullPath(saved),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    if (LoadPluginFromPath(p)) return;
                    break;
                }
            }
        }

        // Exactly one plugin → auto-load
        if (pluginFiles.Length == 1)
        {
            if (!LoadPluginFromPath(pluginFiles[0]))
                StatusText.Text = $"Failed to load {System.IO.Path.GetFileName(pluginFiles[0])}";
            return;
        }

        // Multiple plugins, no saved match → prompt the user
        var displayNames = BuildPluginDisplayNames(pluginFiles);
        var selectWindow = new PluginSelectWindow(pluginFiles, displayNames, saved)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        if (selectWindow.ShowDialog() != true || selectWindow.SelectedPath == null)
        {
            StatusText.Text = "No plugin selected";
            return;
        }
        if (!LoadPluginFromPath(selectWindow.SelectedPath))
            StatusText.Text = $"Failed to load {System.IO.Path.GetFileName(selectWindow.SelectedPath)}";
    }

    private bool LoadPluginFromPath(string selectedDll)
    {
        _plugin = new PluginInterop();
        if (!_plugin.Load(selectedDll))
        {
            _plugin = null;
            _loadedPluginStem = "";
            return false;
        }
        _loadedPluginStem = System.IO.Path.GetFileNameWithoutExtension(selectedDll) ?? "";

        var nego = new EmfeNegotiateInfo { api_version_major = 1, api_version_minor = 0 };
        if (_plugin.emfe_negotiate(ref nego) != EmfeResult.OK)
        {
            StatusText.Text = "Plugin version mismatch";
            _plugin.Dispose();
            _plugin = null;
            _loadedPluginStem = "";
            return false;
        }

        // Set per-plugin data directory (%LOCALAPPDATA%\emfe_CsWPF\<plugin-stem>).
        // The per-plugin subdir prevents cross-plugin contamination of the
        // shared appsettings.json — e.g. mc68030's BoardType="MVME147" would
        // otherwise leak into mc6809's settings on plugin switch.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDir = System.IO.Path.Combine(localAppData, "emfe_CsWPF", _loadedPluginStem);
        System.IO.Directory.CreateDirectory(dataDir);
        _plugin.emfe_set_data_dir(dataDir);

        if (_plugin.emfe_get_board_info(out var info) != EmfeResult.OK)
        {
            StatusText.Text = "Failed to get board info";
            return false;
        }
        _capabilities = info.capabilities;
        ApplyCapabilityVisibility();

        if (_plugin.emfe_create(out _instance) != EmfeResult.OK)
        {
            StatusText.Text = "Failed to create instance";
            return false;
        }

        // Load persisted settings so subsequent reads (Theme, kernel path, etc.)
        // see user-configured values rather than hardcoded defaults.
        _plugin.emfe_load_settings(_instance);

        _stateChangeCb = (IntPtr userData, ref EmfeStateInfo stateInfo) =>
        {
            var state = stateInfo.state;
            var reason = stateInfo.stop_reason;
            var addr = stateInfo.stop_address;
            Dispatcher.BeginInvoke(() =>
            {
                _lastStopReason = reason;
                _lastStopAddress = (uint)addr;
                if (state != EmfeState.Running)
                {
                    // Clean up any one-shot breakpoint hit by this stop.
                    if (reason == EmfeStopReason.Breakpoint
                        && _tempBreakpoints.Remove((uint)addr)
                        && !_breakpointAddresses.ContainsKey((uint)addr))
                    {
                        _plugin?.emfe_remove_breakpoint(_instance, (uint)addr);
                    }
                    UpdateRegisters();
                    UpdateDisassembly();
                    UpdateMemoryDump(_memoryAddress);
                    _callStackWindow?.Refresh();
                    _breakpointsWindow?.Refresh();
                }
                UpdateToolbarState();
                string af = _addrDigits == 4 ? "X4" : "X8";
                if (reason == EmfeStopReason.Breakpoint)
                    StatusText.Text = $"Breakpoint at ${((uint)addr).ToString(af)}";
                else if (reason == EmfeStopReason.Watchpoint)
                    StatusText.Text = $"Watchpoint at ${((uint)addr).ToString(af)}";
                else if (state == EmfeState.Halted)
                    StatusText.Text = $"CPU halted at ${((uint)addr).ToString(af)} (use Reset to restart)";
            });
        };
        _plugin.emfe_set_state_change_callback(_instance, _stateChangeCb, IntPtr.Zero);

        // Console char callback
        _consoleCharCb = (IntPtr userData, byte ch) =>
        {
            // This callback fires on the plugin's CPU thread — a native thread
            // that the CLR has temporarily attached. Touching any UI state
            // directly (even a thread-safe lock+enqueue in ConsoleWindow) from
            // that thread has been observed to eventually corrupt CLR-managed
            // state during boot-time chatter and crash with
            //   "ExecutionEngineException in unknown module"
            //   at coreclr.dll with AV 0xC0000005
            // Match emfe_WinUI3Cpp's policy: enqueue under a MainWindow-owned
            // lock, kick the Dispatcher, and do every mutation of _consoleWindow
            // (and the chars it receives) from the UI thread.
            lock (_pendingConsoleLock)
                _pendingConsoleChars.Enqueue((char)ch);
            // Avoid posting one Dispatcher work item per char during floods
            // (e.g. kernel dmesg) — CheckAccess short-circuits when we happen
            // to already be on the UI thread, and the drain pulls the whole
            // queue in one go regardless of how many chars piled up.
            if (Dispatcher.CheckAccess())
            {
                DrainPendingConsoleChars();
            }
            else
            {
                Dispatcher.BeginInvoke(DrainPendingConsoleChars);
            }
        };
        _plugin.emfe_set_console_char_callback(_instance, _consoleCharCb, IntPtr.Zero);

        BuildRegisterPanel();
        UpdateRegisters();
        UpdateDisassembly();
        UpdateMemoryDump(0);
        UpdateToolbarState();
        // Theme already applied in App.OnStartup; do not re-apply here.

        StatusText.Text = "Stopped";
        UpdateBoardTypeText();

        // Auto-load kernel if a path is persisted for the active target OS.
        AutoLoadKernelFromSettings();

        SavePluginPath(selectedDll);
        return true;
    }

    private void AutoLoadKernelFromSettings()
    {
        if (_plugin == null || _instance == IntPtr.Zero) return;
        if ((_capabilities & EmfeCap.LoadElf) == 0) return;  // plugin doesn't support ELF

        // Determine which setting key holds the kernel path for the active
        // board / target OS combination. Only MVME147 currently auto-loads.
        string board = Marshal.PtrToStringAnsi(
            _plugin.emfe_get_setting(_instance, "BoardType")) ?? "";
        if (board != "MVME147") return;

        string os = Marshal.PtrToStringAnsi(
            _plugin.emfe_get_setting(_instance, "TargetOS")) ?? "";
        string key = os switch
        {
            "NetBSD" => "NetBsdKernelImagePath",
            "Linux" => "LinuxKernelImagePath",
            _ => ""
        };
        if (string.IsNullOrEmpty(key)) return;

        string kpath = Marshal.PtrToStringAnsi(
            _plugin.emfe_get_setting(_instance, key)) ?? "";
        if (string.IsNullOrEmpty(kpath)) return;

        if (!System.IO.File.Exists(kpath))
        {
            StatusText.Text = "Auto-load skipped: kernel file not found";
            return;
        }

        var result = _plugin.emfe_load_elf(_instance, kpath);
        if (result == EmfeResult.OK)
        {
            _lastLoadedFilePath = kpath;
            _lastLoadedFileType = "elf";
            var fileName = System.IO.Path.GetFileName(kpath);
            StatusText.Text = $"Auto-loaded: {fileName}";
            LoadedFileText.Text = fileName;
            UpdateRegisters();
            UpdateDisassembly();
            UpdateMemoryDump(0);
        }
        else
        {
            var err = Marshal.PtrToStringAnsi(_plugin.emfe_get_last_error(_instance));
            StatusText.Text = $"Auto-load failed: {err}";
        }
    }

    private void UpdateBoardTypeText()
    {
        if (_instance == IntPtr.Zero || _plugin == null) return;
        if (_plugin.emfe_get_board_info(out var info) == EmfeResult.OK)
        {
            var boardType = Marshal.PtrToStringAnsi(_plugin.emfe_get_setting(_instance, "BoardType"));
            string board = !string.IsNullOrEmpty(boardType) ? boardType : info.BoardName;
            string boardCpu = $"{board} / {info.CpuName}";

            // Append the network mode when the selected board actually has a
            // network interface. MVME147 has the LANCE; the Generic board has
            // no network device, so showing "Network: …" there would mislead.
            string netSuffix = "";
            if (board == "MVME147")
            {
                var netMode = Marshal.PtrToStringAnsi(_plugin.emfe_get_setting(_instance, "NetworkMode"));
                if (!string.IsNullOrEmpty(netMode))
                    netSuffix = $" — Network: {netMode}";
            }

            BoardTypeText.Text = !string.IsNullOrEmpty(_loadedPluginStem)
                ? $"[{_loadedPluginStem}] {boardCpu}{netSuffix}"
                : $"{boardCpu}{netSuffix}";
        }
    }

    // ========================================================================
    // Register panel — em68030 compatible 2-column layout
    // ========================================================================

    // Theme-aware brushes — resolved from application resources each time to follow theme
    private Brush FgBrush => (Brush)(Application.Current.TryFindResource("ThemeForeground") ?? Brushes.Black);
    private Brush HeaderBrush => (Brush)(Application.Current.TryFindResource("ThemeRegHeaderFg") ?? Brushes.Teal);
    private Brush PanelHeaderBrush => (Brush)(Application.Current.TryFindResource("ThemeAccent") ?? Brushes.DodgerBlue);
    private Brush InputBgBrush => (Brush)(Application.Current.TryFindResource("ThemeInputBg") ?? Brushes.White);
    private Brush BorderBgBrush => (Brush)(Application.Current.TryFindResource("ThemeBorder") ?? Brushes.Gray);
    private static readonly FontFamily ConsolasFont = new("Consolas");

    private void AddGroupHeader(string text)
    {
        RegisterPanel.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12, Foreground = HeaderBrush,
            Margin = new Thickness(0, 6, 0, 2)
        });
    }

    private void AddRegPairToGrid(Grid grid, int row, int col, string name, uint regId)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 8, 1)
        };
        sp.Children.Add(new TextBlock
        {
            Text = name, FontFamily = ConsolasFont, FontSize = 13,
            Width = 30, VerticalAlignment = VerticalAlignment.Center
            // Foreground inherited from App.xaml TextBlock implicit style (DynamicResource)
        });
        var box = new TextBox
        {
            FontFamily = ConsolasFont, FontSize = 13, IsReadOnly = true,
            Width = 95, Padding = new Thickness(6, 3, 6, 4),
            BorderThickness = new Thickness(1), MinHeight = 0
            // Background/Foreground/BorderBrush inherited from App.xaml TextBox implicit style
        };
        sp.Children.Add(box);
        Grid.SetRow(sp, row);
        Grid.SetColumn(sp, col);
        grid.Children.Add(sp);
        _regEntries.Add(new RegUIEntry(regId, 32, EmfeRegType.Int, box));
    }

    private TextBox AddRegRow(StackPanel parent, string name, uint regId, int width = 95)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1)
        };
        sp.Children.Add(new TextBlock
        {
            Text = name, FontFamily = ConsolasFont, FontSize = 13,
            Width = 35, VerticalAlignment = VerticalAlignment.Center
        });
        var box = new TextBox
        {
            FontFamily = ConsolasFont, FontSize = 13, IsReadOnly = true,
            Width = width, Padding = new Thickness(6, 3, 6, 4),
            BorderThickness = new Thickness(1), MinHeight = 0
        };
        sp.Children.Add(box);
        parent.Children.Add(sp);
        _regEntries.Add(new RegUIEntry(regId, 32, EmfeRegType.Int, box));
        return box;
    }

    private void BuildRegisterPanel()
    {
        RegisterPanel.Children.Clear();
        _regEntries.Clear();
        _flagEntries.Clear();

        if (_instance == IntPtr.Zero) return;

        // Get register definitions from plugin
        int regCount = _plugin.emfe_get_register_defs(_instance, out IntPtr defsPtr);
        if (regCount <= 0) return;

        // Marshal the plugin-owned array
        int structSize = Marshal.SizeOf<EmfeRegisterDef>();
        var defs = new EmfeRegisterDef[regCount];
        for (int i = 0; i < regCount; i++)
            defs[i] = Marshal.PtrToStructure<EmfeRegisterDef>(defsPtr + i * structSize);

        // Detect PC/SP register IDs and address width from memory size
        for (int i = 0; i < regCount; i++)
        {
            if ((defs[i].flags & (uint)EmfeRegFlags.PC) != 0) _pcRegId = defs[i].reg_id;
            if ((defs[i].flags & (uint)EmfeRegFlags.SP) != 0) _spRegId = defs[i].reg_id;
        }
        ulong memSize = _plugin.emfe_get_memory_size(_instance);
        _addrDigits = memSize <= 0x10000 ? 4 : 8;

        // Panel header with Edit/Apply/Cancel
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.Children.Add(new TextBlock
        {
            Text = "Registers", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = PanelHeaderBrush, Margin = new Thickness(4, 2, 0, 4)
        });
        var editBtnPanel = new StackPanel { Orientation = Orientation.Horizontal };
        _btnRegEdit = new Button { Content = "Edit", FontSize = 11, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(2, 0, 2, 0) };
        _btnRegApply = new Button { Content = "Apply", FontSize = 11, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(2, 0, 2, 0), Visibility = Visibility.Collapsed };
        _btnRegCancel = new Button { Content = "Cancel", FontSize = 11, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(2, 0, 2, 0), Visibility = Visibility.Collapsed };
        _btnRegEdit.Click += OnRegEdit;
        _btnRegApply.Click += OnRegApply;
        _btnRegCancel.Click += OnRegCancel;
        editBtnPanel.Children.Add(_btnRegEdit);
        editBtnPanel.Children.Add(_btnRegApply);
        editBtnPanel.Children.Add(_btnRegCancel);
        Grid.SetColumn(editBtnPanel, 1);
        headerGrid.Children.Add(editBtnPanel);
        RegisterPanel.Children.Add(headerGrid);

        // Group registers by their group name
        var groups = new List<(string Group, List<(EmfeRegisterDef Def, int Index)> Regs)>();
        string? currentGroup = null;
        List<(EmfeRegisterDef, int)>? currentList = null;
        for (int i = 0; i < regCount; i++)
        {
            var def = defs[i];
            if ((def.flags & (uint)EmfeRegFlags.Hidden) != 0) continue;
            string group = def.Group.Length > 0 ? def.Group : "General";
            if (group != currentGroup)
            {
                currentGroup = group;
                currentList = new();
                groups.Add((group, currentList));
            }
            currentList!.Add((def, i));
        }

        foreach (var (groupName, regs) in groups)
        {
            AddGroupHeader(groupName);

            // Check if this group has a FLAGS register
            var flagsReg = regs.FirstOrDefault(r => (r.Def.flags & (uint)EmfeRegFlags.Flags) != 0);

            if (regs.Count >= 4 && flagsReg.Def.name == IntPtr.Zero)
            {
                // Multi-register group — use 2-column grid
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int rows = (regs.Count + 1) / 2;
                for (int r = 0; r < rows; r++)
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int j = 0; j < regs.Count; j++)
                {
                    var (def, _) = regs[j];
                    AddRegPairToGrid(grid, j / 2, j % 2, def.Name, def.reg_id);
                    _regEntries[^1] = _regEntries[^1] with { BitWidth = def.bit_width, Type = (EmfeRegType)def.type };
                }
                RegisterPanel.Children.Add(grid);
            }
            else
            {
                // Small group or contains FLAGS — use vertical layout
                var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
                foreach (var (def, _) in regs)
                {
                    bool isFlags = (def.flags & (uint)EmfeRegFlags.Flags) != 0;
                    int width = def.bit_width <= 8 ? 45 : def.bit_width <= 16 ? 65 : def.bit_width <= 32 ? 95 : 130;
                    var box = AddRegRow(panel, def.Name, def.reg_id, width);
                    _regEntries[^1] = _regEntries[^1] with { BitWidth = def.bit_width, Type = (EmfeRegType)def.type };
                    if ((EmfeRegType)def.type == EmfeRegType.Float)
                        box.FontSize = 11;

                    // If this is a flags register and the plugin supports the
                    // optional bit-decomposition export, render a row of
                    // CheckBoxes underneath. Each checkbox click reads the
                    // parent register, flips the indexed bit, and writes it
                    // back via emfe_set_registers.
                    if (isFlags && _plugin.emfe_get_register_flag_defs != null)
                    {
                        int nBits = _plugin.emfe_get_register_flag_defs(_instance, def.reg_id, out IntPtr bitsPtr);
                        if (nBits > 0 && bitsPtr != IntPtr.Zero)
                        {
                            int bitStructSize = Marshal.SizeOf<EmfeRegFlagBitDef>();
                            var flagRow = new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                // Indent so the first checkbox starts where
                                // the value textbox does on the row above
                                // (label width 35, matching AddRegRow).
                                Margin = new Thickness(35, 0, 0, 4)
                            };
                            uint regId = def.reg_id;
                            bool readOnly = (def.flags & (uint)EmfeRegFlags.ReadOnly) != 0;
                            for (int b = 0; b < nBits; b++)
                            {
                                var bitDef = Marshal.PtrToStructure<EmfeRegFlagBitDef>(bitsPtr + b * bitStructSize);
                                byte bitIndex = bitDef.bit_index;
                                var chk = new CheckBox
                                {
                                    Content = bitDef.Label,
                                    IsThreeState = false,
                                    IsEnabled = !readOnly,
                                    Margin = new Thickness(0, 0, 8, 0),
                                    VerticalAlignment = VerticalAlignment.Center
                                };
                                chk.SetResourceReference(CheckBox.ForegroundProperty, "ThemeForeground");
                                chk.Click += (s, e) =>
                                {
                                    if (_plugin.emfe_get_state(_instance) == EmfeState.Running) return;
                                    var rv = new EmfeRegValue[] { new() { reg_id = regId } };
                                    _plugin.emfe_get_registers(_instance, rv, 1);
                                    ulong mask = 1UL << bitIndex;
                                    if (chk.IsChecked == true) rv[0].u64 |= mask;
                                    else                       rv[0].u64 &= ~mask;
                                    _plugin.emfe_set_registers(_instance, rv, 1);
                                    UpdateRegisters();
                                };
                                flagRow.Children.Add(chk);
                                _flagEntries.Add(new FlagCheckEntry(regId, bitIndex, chk));
                            }
                            panel.Children.Add(flagRow);
                        }
                    }
                }
                RegisterPanel.Children.Add(panel);
            }
        }
    }

    // ========================================================================
    // Update registers
    // ========================================================================

    private void UpdateRegisters()
    {
        if (_instance == IntPtr.Zero || _regEntries.Count == 0) return;

        var values = new EmfeRegValue[_regEntries.Count];
        for (int i = 0; i < _regEntries.Count; i++)
            values[i].reg_id = _regEntries[i].RegId;

        _plugin.emfe_get_registers(_instance, values, values.Length);

        for (int i = 0; i < _regEntries.Count; i++)
        {
            var entry = _regEntries[i];
            string text = entry.Type == EmfeRegType.Float
                ? $"{values[i].f64:F6}"
                : entry.BitWidth <= 8
                    ? $"{(byte)values[i].u64:X2}"
                    : entry.BitWidth <= 16
                        ? $"{(ushort)values[i].u64:X4}"
                        : entry.BitWidth <= 32
                            ? $"{(uint)values[i].u64:X8}"
                            : $"{values[i].u64:X16}";
            entry.ValueBox.Text = text;
        }

        // Flag checkboxes — populated by BuildRegisterPanel from the plugin's
        // emfe_get_register_flag_defs. Read each parent register once, then
        // assign each checkbox's IsChecked from the indexed bit.
        if (_flagEntries.Count > 0)
        {
            var flagRegIds = _flagEntries.Select(f => f.RegId).Distinct().ToList();
            var flagReads = new EmfeRegValue[flagRegIds.Count];
            for (int i = 0; i < flagRegIds.Count; i++)
                flagReads[i].reg_id = flagRegIds[i];
            _plugin.emfe_get_registers(_instance, flagReads, flagReads.Length);
            var regValues = new Dictionary<uint, ulong>();
            for (int i = 0; i < flagRegIds.Count; i++)
                regValues[flagRegIds[i]] = flagReads[i].u64;
            foreach (var f in _flagEntries)
            {
                ulong v = regValues[f.RegId];
                f.CheckBox.IsChecked = ((v >> f.BitIndex) & 1UL) != 0;
            }
        }

        // Cycles/MHz/MIPS display is driven by the periodic stats timer and
        // respects the current click-cycled view mode. Invoking it here keeps
        // the toolbar in sync with the just-updated register state on stop
        // or step, without overwriting whichever view the user selected.
        UpdateStatsDisplay();
    }

    // ========================================================================
    // Disassembly
    // ========================================================================

    private void UpdateDisassembly()
    {
        if (_instance == IntPtr.Zero) return;

        var pcVal = new EmfeRegValue[] { new() { reg_id = _pcRegId } };
        _plugin.emfe_get_registers(_instance, pcVal, 1);
        uint pc = (uint)pcVal[0].u64;

        string addrFmt = _addrDigits == 4 ? "X4" : "X8";

        var lines = new EmfeDisasmLine[64];
        uint startAddr = pc;
        if (_plugin.emfe_get_program_range(_instance, out ulong progStart, out _) == EmfeResult.OK
            && progStart > 0 && pc >= (uint)progStart)
        {
            startAddr = Math.Max((uint)progStart, pc >= 0x40 ? pc - 0x40 : 0);
        }
        int count = _plugin.emfe_disassemble_range(_instance, startAddr, startAddr + 0x200, lines, 64);

        DisasmList.Items.Clear();
        _disasmAddresses.Clear();
        _disasmTexts.Clear();

        for (int i = 0; i < count; i++)
        {
            uint addr = (uint)lines[i].address;
            _disasmAddresses.Add(addr);

            bool isBP = _breakpointAddresses.ContainsKey(addr);
            bool bpEnabled = isBP && _breakpointAddresses[addr];
            bool isPC = addr == pc;

            var row = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            if (isPC) row.SetResourceReference(Grid.BackgroundProperty, "ThemeCheckedBg");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bpIndicator = new TextBlock
            {
                FontFamily = ConsolasFont, FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (isBP)
            {
                bpIndicator.Text = bpEnabled ? "\u25CF" : "\u25CB";
                bpIndicator.SetResourceReference(TextBlock.ForegroundProperty,
                    bpEnabled ? "ThemeBreakpointFg" : "ThemeDimFg");
            }
            Grid.SetColumn(bpIndicator, 0);

            string text = $"{addr.ToString(addrFmt)}  {lines[i].RawBytes,-12}  {lines[i].Mnemonic,-8} {lines[i].Operands}";
            _disasmTexts.Add(text);
            var mainText = new TextBlock
            {
                Text = text, FontFamily = ConsolasFont, FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            if (isPC)
                mainText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeHighlightedFg");
            else if (isBP && bpEnabled)
                mainText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeBreakpointFg");
            else if (isBP)
                mainText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeDimFg");
            Grid.SetColumn(mainText, 1);

            row.Children.Add(bpIndicator);
            row.Children.Add(mainText);
            DisasmList.Items.Add(row);
            if (isPC) DisasmList.ScrollIntoView(row);
        }
    }

    private void ToggleBreakpoint(uint address)
    {
        if (_instance == IntPtr.Zero) return;
        if (_breakpointAddresses.TryGetValue(address, out bool enabled))
        {
            if (enabled)
            {
                _plugin.emfe_enable_breakpoint(_instance, address, false);
                _breakpointAddresses[address] = false;
            }
            else
            {
                _plugin.emfe_remove_breakpoint(_instance, address);
                _breakpointAddresses.Remove(address);
            }
        }
        else
        {
            _plugin.emfe_add_breakpoint(_instance, address);
            _breakpointAddresses[address] = true;
        }
        UpdateDisassembly();
        _breakpointsWindow?.Refresh();
    }

    public void SyncBreakpointCacheFromPlugin()
    {
        if (_instance == IntPtr.Zero) return;
        _breakpointAddresses.Clear();
        var buf = new EmfeBreakpointInfo[128];
        int n = _plugin.emfe_get_breakpoints(_instance, buf, buf.Length);
        for (int i = 0; i < n; i++)
            _breakpointAddresses[(uint)buf[i].address] = buf[i].enabled;
        UpdateDisassembly();
        // Force full Memory Dump rebuild to update watchpoint markers
        _memBuiltAddress = uint.MaxValue;
        UpdateMemoryDump(_memoryAddress);
    }

    private void OnDisasmDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        int idx = DisasmList.SelectedIndex;
        if (idx >= 0 && idx < _disasmAddresses.Count)
            ToggleBreakpoint(_disasmAddresses[idx]);
    }

    // ========================================================================
    // Disassembly context menu: Cancel / Run to here / Set PC / Copy
    // ========================================================================

    // Index in _disasmAddresses / _disasmTexts of the row under the cursor
    // when the context menu was opened.  -1 when nothing was targeted.
    private int _disasmMenuTargetIndex = -1;

    private void OnDisasmContextOpened(object sender, RoutedEventArgs e)
    {
        // Resolve the right-clicked row at menu-open time.  We prefer the
        // row under the mouse cursor; fall back to the current selection.
        _disasmMenuTargetIndex = -1;
        var pos = System.Windows.Input.Mouse.GetPosition(DisasmList);
        var hit = System.Windows.Media.VisualTreeHelper.HitTest(DisasmList, pos);
        if (hit?.VisualHit != null)
        {
            var item = FindAncestor<System.Windows.Controls.ListBoxItem>(hit.VisualHit);
            if (item != null)
                _disasmMenuTargetIndex = DisasmList.ItemContainerGenerator.IndexFromContainer(item);
        }
        if (_disasmMenuTargetIndex < 0)
            _disasmMenuTargetIndex = DisasmList.SelectedIndex;

        bool haveTarget = _disasmMenuTargetIndex >= 0
                          && _disasmMenuTargetIndex < _disasmAddresses.Count;
        DisasmMenuRunToHere.IsEnabled = haveTarget && _instance != IntPtr.Zero;
        DisasmMenuSetPc.IsEnabled     = haveTarget && _instance != IntPtr.Zero;
        DisasmMenuCopy.IsEnabled      = DisasmList.SelectedItems.Count > 0;
    }

    private static T? FindAncestor<T>(System.Windows.DependencyObject? start)
        where T : System.Windows.DependencyObject
    {
        while (start != null && start is not T)
            start = System.Windows.Media.VisualTreeHelper.GetParent(start);
        return start as T;
    }

    private void OnDisasmMenuCancel(object sender, RoutedEventArgs e)
    {
        // No-op — right-click / Escape already dismisses the menu.  Kept as a
        // visible entry at the user's request.
    }

    private void OnDisasmMenuRunToHere(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;
        if (_disasmMenuTargetIndex < 0 || _disasmMenuTargetIndex >= _disasmAddresses.Count) return;

        uint addr = _disasmAddresses[_disasmMenuTargetIndex];

        // If the address already carries a user-added breakpoint (enabled or not),
        // don't install a one-shot — just run and let the existing BP stop us.
        // The state-change callback will not remove a user BP because it's
        // not in _tempBreakpoints.
        if (!_breakpointAddresses.ContainsKey(addr))
        {
            if (_plugin!.emfe_add_breakpoint(_instance, addr) != EmfeResult.OK)
            {
                StatusText.Text = $"Failed to add temporary breakpoint at ${addr:X8}";
                return;
            }
            _tempBreakpoints.Add(addr);
        }

        ResetRunStatsBaseline();
        _plugin!.emfe_run(_instance);
        UpdateToolbarState();
        string af = _addrDigits == 4 ? "X4" : "X8";
        StatusText.Text = $"Running to ${addr.ToString(af)}...";
    }

    private void OnDisasmMenuSetPc(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;
        if (_disasmMenuTargetIndex < 0 || _disasmMenuTargetIndex >= _disasmAddresses.Count) return;

        uint addr = _disasmAddresses[_disasmMenuTargetIndex];
        var values = new[] { new EmfeRegValue { reg_id = _pcRegId, u64 = addr } };
        if (_plugin!.emfe_set_registers(_instance, values, values.Length) != EmfeResult.OK)
        {
            StatusText.Text = $"Failed to set PC to ${addr:X8}";
            return;
        }
        UpdateRegisters();
        UpdateDisassembly();
        UpdateMemoryDump(_memoryAddress);
        string af = _addrDigits == 4 ? "X4" : "X8";
        StatusText.Text = $"PC set to ${addr.ToString(af)}";
    }

    private void OnDisasmMenuCopy(object sender, RoutedEventArgs e)
        => CopySelectedDisasmToClipboard();

    private void OnDisasmCopy(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        CopySelectedDisasmToClipboard();
        e.Handled = true;
    }

    private void CopySelectedDisasmToClipboard()
    {
        if (DisasmList.SelectedItems.Count == 0) return;

        // Walk items in visual order so the copied block reads top-to-bottom
        // regardless of the user's click order.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < DisasmList.Items.Count; i++)
        {
            if (!DisasmList.SelectedItems.Contains(DisasmList.Items[i])) continue;
            if (i >= _disasmTexts.Count) continue;
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(_disasmTexts[i]);
        }
        if (sb.Length == 0) return;
        try
        {
            System.Windows.Clipboard.SetText(sb.ToString());
            StatusText.Text = $"Copied {DisasmList.SelectedItems.Count} line(s) to clipboard";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Copy failed: {ex.Message}";
        }
    }

    // ========================================================================
    // Memory dump (TextBox, matching em68030 style)
    // ========================================================================

    private readonly List<(uint Start, uint End, EmfeWatchpointType Type)> _activeWatches = new();

    private void RefreshActiveWatchpoints()
    {
        _activeWatches.Clear();
        if (_instance == IntPtr.Zero) return;
        var buf = new EmfeWatchpointInfo[128];
        int n = _plugin.emfe_get_watchpoints(_instance, buf, buf.Length);
        for (int i = 0; i < n; i++)
        {
            if (!buf[i].enabled) continue;
            uint start = (uint)buf[i].address;
            uint end = start + (uint)buf[i].size - 1;
            _activeWatches.Add((start, end, buf[i].type));
        }
    }

    private string? WatchpointBrushKeyFor(uint addr)
    {
        foreach (var (start, end, type) in _activeWatches)
        {
            if (addr >= start && addr <= end)
            {
                return type switch
                {
                    EmfeWatchpointType.Read => "ThemeConsoleFg",
                    EmfeWatchpointType.Write => "ThemeBreakpointFg",
                    _ => "ThemeHighlightedFg",
                };
            }
        }
        return null;
    }

    private void UpdateMemoryDump(uint address)
    {
        if (_instance == IntPtr.Zero) return;
        _memoryAddress = address;

        int dumpSize = 256;
        if (int.TryParse(MemSizeBox.Text, out int sz) && sz > 0 && sz <= 4096)
            dumpSize = sz;

        var data = new byte[dumpSize];
        _plugin.emfe_peek_range(_instance, address, data, (uint)dumpSize);

        RefreshActiveWatchpoints();

        const int bytesPerRow = 16;

        // Incremental path: if layout matches previous build, just update cell text.
        if (_memCells.Count > 0
            && _memBuiltAddress == address
            && _memBuiltSize == dumpSize
            && _memBuiltEditMode == _memEditMode)
        {
            for (int i = 0; i < _memCells.Count && i < dumpSize; i++)
            {
                var c = _memCells[i];
                byte b = data[i];
                c.Original = b;
                string hex = b.ToString("X2");
                switch (c.Box)
                {
                    case TextBlock tbl: tbl.Text = hex; break;
                    case TextBox tbx: tbx.Text = hex; break;
                }
            }
            // Refresh ASCII columns
            int rowCountI = (dumpSize + bytesPerRow - 1) / bytesPerRow;
            for (int r = 0; r < rowCountI && r < _memAsciiBlocks.Count; r++)
                _memAsciiBlocks[r].Text = BuildAsciiForRow(data, r * bytesPerRow,
                    Math.Min(bytesPerRow, dumpSize - r * bytesPerRow));
            return;
        }

        int rows = (dumpSize + bytesPerRow - 1) / bytesPerRow;

        MemoryDumpPanel.Children.Clear();
        _memCells.Clear();
        _memAsciiBlocks.Clear();
        _memBuiltAddress = address;
        _memBuiltSize = dumpSize;
        _memBuiltEditMode = _memEditMode;

        var font = ConsolasFont;
        var cellBg = (System.Windows.Media.Brush)(Application.Current.TryFindResource("ThemeInputBg")
            ?? System.Windows.Media.Brushes.Transparent);
        var cellFg = (System.Windows.Media.Brush)(Application.Current.TryFindResource("ThemeForeground")
            ?? System.Windows.Media.Brushes.White);
        var cellSelection = (System.Windows.Media.Brush)(Application.Current.TryFindResource("ThemeListSelection")
            ?? System.Windows.Media.Brushes.SteelBlue);

        for (int row = 0; row < rows; row++)
        {
            uint rowAddr = address + (uint)(row * bytesPerRow);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 1, 4, 1) };

            line.Children.Add(new TextBlock
            {
                Text = rowAddr.ToString(_addrDigits == 4 ? "X4" : "X8"), Width = _addrDigits == 4 ? 40 : 72,
                FontFamily = font, FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            });
            line.Children.Add(new TextBlock
            {
                Text = "  ", FontFamily = font, FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            });

            for (int col = 0; col < bytesPerRow; col++)
            {
                int idx = row * bytesPerRow + col;
                if (col == 8)
                {
                    line.Children.Add(new TextBlock
                    {
                        Text = " ", FontFamily = font, FontSize = 13,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                if (idx < dumpSize)
                {
                    byte b = data[idx];
                    uint cellAddr = rowAddr + (uint)col;
                    var cell = new MemCell
                    {
                        Address = cellAddr,
                        Original = b,
                        Row = row,
                        Col = col
                    };
                    string? wpKey = WatchpointBrushKeyFor(cellAddr);
                    FrameworkElement ctl;
                    if (_memEditMode)
                    {
                        ctl = CreateMemEditTextBox(cell, b, font, cellBg, cellFg, cellSelection);
                    }
                    else
                    {
                        ctl = new TextBlock
                        {
                            Text = b.ToString("X2"),
                            Width = 22,
                            Margin = new Thickness(1, 0, 1, 0),
                            FontFamily = font, FontSize = 13,
                            TextAlignment = TextAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = cellFg,
                            Tag = cell
                        };
                    }
                    if (wpKey != null)
                    {
                        // Use Border wrapper for highlight, since TextBlock has no BorderBrush
                        var wrapper = new Border
                        {
                            BorderThickness = new Thickness(1),
                            Margin = new Thickness(0),
                            Child = ctl
                        };
                        ctl.ClearValue(FrameworkElement.MarginProperty);
                        wrapper.SetResourceReference(Border.BorderBrushProperty, wpKey);
                        wrapper.Tag = cell;
                        cell.Box = ctl;
                        _memCells.Add(cell);
                        line.Children.Add(wrapper);
                    }
                    else
                    {
                        cell.Box = ctl;
                        _memCells.Add(cell);
                        line.Children.Add(ctl);
                    }
                }
                else
                {
                    line.Children.Add(new TextBlock
                    {
                        Text = "   ", Width = 22, Margin = new Thickness(1, 0, 1, 0),
                        FontFamily = font, FontSize = 13,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
            }

            line.Children.Add(new TextBlock
            {
                Text = "  ", FontFamily = font, FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            });
            var ascii = new TextBlock
            {
                Text = BuildAsciiForRow(data, row * bytesPerRow, Math.Min(bytesPerRow, dumpSize - row * bytesPerRow)),
                FontFamily = font, FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            line.Children.Add(ascii);
            _memAsciiBlocks.Add(ascii);

            MemoryDumpPanel.Children.Add(line);
        }
    }

    private TextBox CreateMemEditTextBox(MemCell cell, byte b, FontFamily font,
        System.Windows.Media.Brush cellBg, System.Windows.Media.Brush cellFg, System.Windows.Media.Brush cellSelection)
    {
        var tb = new TextBox
        {
            Text = b.ToString("X2"),
            Width = 22, Padding = new Thickness(1),
            Margin = new Thickness(1, 0, 1, 0),
            FontFamily = font, FontSize = 13,
            MaxLength = 2, CharacterCasing = CharacterCasing.Upper,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            Tag = cell,
            Style = (Style)FindResource("MemCellTextBoxStyle"),
            Background = cellBg,
            Foreground = cellFg,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            CaretBrush = cellFg,
            SelectionBrush = cellSelection
        };
        tb.GotFocus += MemCell_GotFocus;
        tb.LostFocus += MemCell_LostFocus;
        tb.TextChanged += MemCell_TextChanged;
        tb.PreviewKeyDown += MemCell_PreviewKeyDown;
        return tb;
    }

    private static string BuildAsciiForRow(byte[] data, int start, int count)
    {
        var chars = new char[count];
        for (int i = 0; i < count; i++)
        {
            byte b = data[start + i];
            chars[i] = (b >= 0x20 && b < 0x7F) ? (char)b : '.';
        }
        return new string(chars);
    }

    private static void SetCellModifiedStyle(TextBox tb, bool modified)
    {
        if (modified) tb.SetResourceReference(TextBox.BackgroundProperty, "ThemeCheckedBg");
        else tb.Background = System.Windows.Media.Brushes.Transparent;
    }

    private void RefreshAsciiForCell(MemCell cell)
    {
        if (cell.Row >= _memAsciiBlocks.Count) return;
        var sb = new StringBuilder();
        foreach (var c in _memCells)
        {
            if (c.Row != cell.Row) continue;
            string? text = c.Box switch
            {
                TextBox tb => tb.Text,
                TextBlock tbl => tbl.Text,
                _ => null
            };
            byte b = byte.TryParse(text ?? "", System.Globalization.NumberStyles.HexNumber, null, out byte v) ? v : c.Original;
            sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
        }
        _memAsciiBlocks[cell.Row].Text = sb.ToString();
    }

    private void MemCell_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) tb.SelectAll();
    }

    private void MemCell_LostFocus(object sender, RoutedEventArgs e) { }

    private void MemCell_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not MemCell cell) return;

        bool modified = false;
        if (byte.TryParse(tb.Text, System.Globalization.NumberStyles.HexNumber, null, out byte v))
            modified = v != cell.Original;
        SetCellModifiedStyle(tb, modified);

        RefreshAsciiForCell(cell);

        if (_memEditMode && tb.Text.Length == 2 && tb.CaretIndex == 2)
            MoveMemFocus(cell, 0, 1);
    }

    private void MemCell_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not MemCell cell) return;

        int dRow = 0, dCol = 0;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Right:
                if (tb.CaretIndex >= tb.Text.Length) { dCol = 1; e.Handled = true; }
                break;
            case System.Windows.Input.Key.Left:
                if (tb.CaretIndex == 0) { dCol = -1; e.Handled = true; }
                break;
            case System.Windows.Input.Key.Up:
                dRow = -1; e.Handled = true; break;
            case System.Windows.Input.Key.Down:
                dRow = 1; e.Handled = true; break;
            case System.Windows.Input.Key.Tab:
                dCol = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0 ? -1 : 1;
                e.Handled = true; break;
            case System.Windows.Input.Key.Enter:
                dCol = 1; e.Handled = true; break;
            default:
                return;
        }
        if (dRow != 0 || dCol != 0) MoveMemFocus(cell, dRow, dCol);
    }

    private void MoveMemFocus(MemCell from, int dRow, int dCol)
    {
        int newRow = from.Row + dRow;
        int newCol = from.Col + dCol;
        if (newCol >= 16) { newCol = 0; newRow++; }
        else if (newCol < 0) { newCol = 15; newRow--; }

        var target = _memCells.FirstOrDefault(c => c.Row == newRow && c.Col == newCol);
        if (target?.Box is TextBox targetBox)
        {
            targetBox.Focus();
            targetBox.SelectAll();
        }
    }

    // ========================================================================
    // Toolbar state
    // ========================================================================

    private void UpdateToolbarState()
    {
        if (_instance == IntPtr.Zero) return;
        var state = _plugin.emfe_get_state(_instance);
        bool running = state == EmfeState.Running;

        BtnStep.IsEnabled = !running;
        BtnStepOver.IsEnabled = !running && (_capabilities & EmfeCap.StepOver) != 0;
        BtnStepOut.IsEnabled = !running && (_capabilities & EmfeCap.StepOut) != 0;
        BtnRun.IsEnabled = !running;
        BtnStop.IsEnabled = running;
        BtnReset.IsEnabled = !running;
        BtnFullReset.IsEnabled = !running;
    }

    // Enable/disable menu items and toolbar buttons based on the plugin's
    // declared capabilities. Called once per plugin load.
    private void ApplyCapabilityVisibility()
    {
        bool hasElf    = (_capabilities & EmfeCap.LoadElf) != 0;
        bool hasSrec   = (_capabilities & EmfeCap.LoadSrec) != 0;
        bool hasBin    = (_capabilities & EmfeCap.LoadBinary) != 0;
        bool hasOver   = (_capabilities & EmfeCap.StepOver) != 0;
        bool hasOut    = (_capabilities & EmfeCap.StepOut) != 0;
        bool hasCS     = (_capabilities & EmfeCap.CallStack) != 0;
        bool hasFB     = (_capabilities & EmfeCap.Framebuffer) != 0;

        MenuLoadElf.IsEnabled      = hasElf;
        MenuLoadSrec.IsEnabled     = hasSrec;
        MenuLoadBinary.IsEnabled   = hasBin;
        MenuStepOver.IsEnabled     = hasOver;
        MenuStepOut.IsEnabled      = hasOut;
        MenuCallStack.IsEnabled    = hasCS;
        MenuFramebuffer.IsEnabled  = hasFB;
    }

    // ========================================================================
    // Event handlers
    // ========================================================================

    private void OnLoadElf(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;
        var dlg = new OpenFileDialog { Filter = "All Files|*.*" };
        if (dlg.ShowDialog() != true) return;

        var result = _plugin.emfe_load_elf(_instance, dlg.FileName);
        if (result == EmfeResult.OK)
        {
            _lastLoadedFilePath = dlg.FileName;
            _lastLoadedFileType = "elf";
            var fileName = System.IO.Path.GetFileName(dlg.FileName);
            StatusText.Text = $"Loaded: {fileName}";
            LoadedFileText.Text = fileName;
            UpdateRegisters();
            UpdateDisassembly();
            UpdateMemoryDump(0);
        }
        else
        {
            var err = Marshal.PtrToStringAnsi(_plugin.emfe_get_last_error(_instance));
            StatusText.Text = $"Load failed: {err}";
        }
    }

    private void OnLoadSrec(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;
        var dlg = new OpenFileDialog { Filter = "S-Record Files (*.s19;*.srec)|*.s19;*.srec|All Files|*.*" };
        if (dlg.ShowDialog() != true) return;

        var result = _plugin.emfe_load_srec(_instance, dlg.FileName);
        if (result == EmfeResult.OK)
        {
            _lastLoadedFilePath = dlg.FileName;
            _lastLoadedFileType = "srec";
            var fileName = System.IO.Path.GetFileName(dlg.FileName);
            StatusText.Text = $"Loaded: {fileName}";
            LoadedFileText.Text = fileName;
            UpdateRegisters();
            UpdateDisassembly();
            UpdateMemoryDump(0);
        }
        else
        {
            var err = Marshal.PtrToStringAnsi(_plugin.emfe_get_last_error(_instance));
            StatusText.Text = $"Load failed: {err}";
        }
    }

    private void OnLoadBinary(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero || _plugin == null) return;
        var dlg = new OpenFileDialog { Filter = "Binary Files (*.bin)|*.bin|All Files|*.*" };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var result = _plugin.emfe_load_binary(_instance, dlg.FileName, 0);
            if (result == EmfeResult.OK)
            {
                _lastLoadedFilePath = dlg.FileName;
                _lastLoadedFileType = "binary";
                var fileName = System.IO.Path.GetFileName(dlg.FileName);
                StatusText.Text = $"Loaded: {fileName}";
                LoadedFileText.Text = fileName;
                UpdateRegisters();
                UpdateDisassembly();
                UpdateMemoryDump(0);
            }
            else
            {
                var err = Marshal.PtrToStringAnsi(_plugin.emfe_get_last_error(_instance));
                StatusText.Text = $"Load failed: {err}";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Load exception: {ex.Message}";
        }
    }

    private void OnStep(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;
        var result = _plugin.emfe_step(_instance);
        if (result != EmfeResult.OK)
        {
            StatusText.Text = $"Step failed: {result}";
            return;
        }
        UpdateRegisters();
        UpdateDisassembly();
        UpdateMemoryDump(_memoryAddress);
        UpdateToolbarState();
        if (_plugin.emfe_get_state(_instance) == EmfeState.Halted)
        {
            var pcVal = new EmfeRegValue[] { new() { reg_id = _pcRegId } };
            _plugin.emfe_get_registers(_instance, pcVal, 1);
            string haltAddr = ((uint)pcVal[0].u64).ToString(_addrDigits == 4 ? "X4" : "X8");
            StatusText.Text = $"CPU halted at ${haltAddr} (use Reset to restart)";
        }
    }

    private void OnStepOver(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;
        _plugin.emfe_step_over(_instance);
        UpdateToolbarState();
    }

    private void OnStepOut(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;
        _plugin.emfe_step_out(_instance);
        UpdateToolbarState();
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;
        ResetRunStatsBaseline();
        _plugin.emfe_run(_instance);
        UpdateToolbarState();
        StatusText.Text = "Running...";
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;
        _plugin.emfe_stop(_instance);
        UpdateRegisters();
        UpdateDisassembly();
        UpdateMemoryDump(_memoryAddress);
        UpdateToolbarState();
        StatusText.Text = "Stopped";
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;
        _plugin.emfe_reset(_instance);
        UpdateRegisters();
        UpdateDisassembly();
        UpdateMemoryDump(0);
        UpdateToolbarState();
        StatusText.Text = "Reset";
    }

    private void OnFullReset(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero || _plugin == null) return;
        _plugin.emfe_destroy(_instance);
        _instance = IntPtr.Zero;

        if (_plugin.emfe_create(out _instance) != EmfeResult.OK)
        {
            StatusText.Text = "Full reset failed";
            return;
        }
        _plugin.emfe_set_state_change_callback(_instance, _stateChangeCb, IntPtr.Zero);
        _plugin.emfe_set_console_char_callback(_instance, _consoleCharCb, IntPtr.Zero);

        // Reload last loaded file
        if (!string.IsNullOrEmpty(_lastLoadedFilePath) && System.IO.File.Exists(_lastLoadedFilePath))
        {
            var result = _lastLoadedFileType switch
            {
                "elf" => _plugin.emfe_load_elf(_instance, _lastLoadedFilePath),
                "srec" => _plugin.emfe_load_srec(_instance, _lastLoadedFilePath),
                "binary" => _plugin.emfe_load_binary(_instance, _lastLoadedFilePath, 0),
                _ => EmfeResult.Unsupported
            };
            if (result != EmfeResult.OK)
                StatusText.Text = $"Full Reset — reload failed";
        }

        UpdateRegisters();
        UpdateDisassembly();
        UpdateMemoryDump(0);
        UpdateToolbarState();
        StatusText.Text = "Full Reset";
    }

    private void OnMemoryGo(object sender, RoutedEventArgs e)
    {
        if (uint.TryParse(TxtMemAddr.Text, System.Globalization.NumberStyles.HexNumber, null, out uint addr))
            UpdateMemoryDump(addr);
    }

    private void OnDisasmGo(object sender, RoutedEventArgs e)
    {
        if (!uint.TryParse(DisasmAddrBox.Text, System.Globalization.NumberStyles.HexNumber, null, out uint addr))
            return;

        var lines = new EmfeDisasmLine[64];
        int count = _plugin.emfe_disassemble_range(_instance, addr, addr + 0x200, lines, 64);

        DisasmList.Items.Clear();
        for (int i = 0; i < count; i++)
        {
            string text = $"  {((uint)lines[i].address).ToString(_addrDigits == 4 ? "X4" : "X8")}  {lines[i].RawBytes,-12}  {lines[i].Mnemonic,-8} {lines[i].Operands}";
            DisasmList.Items.Add(new TextBlock
            {
                Text = text, FontFamily = ConsolasFont, FontSize = 13
            });
        }
    }

    // ========================================================================
    // Register edit mode
    // ========================================================================

    private void OnRegEdit(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _regEntries)
            entry.ValueBox.IsReadOnly = false;
        _btnRegEdit!.Visibility = Visibility.Collapsed;
        _btnRegApply!.Visibility = Visibility.Visible;
        _btnRegCancel!.Visibility = Visibility.Visible;
    }

    private void OnRegApply(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;

        var values = new List<EmfeRegValue>();
        foreach (var entry in _regEntries)
        {
            var v = new EmfeRegValue { reg_id = entry.RegId };
            try
            {
                if (entry.Type == EmfeRegType.Float)
                    v.f64 = double.Parse(entry.ValueBox.Text);
                else
                    v.u64 = ulong.Parse(entry.ValueBox.Text, System.Globalization.NumberStyles.HexNumber);
                values.Add(v);
            }
            catch { }
        }

        if (values.Count > 0)
            _plugin.emfe_set_registers(_instance, values.ToArray(), values.Count);

        foreach (var entry in _regEntries)
            entry.ValueBox.IsReadOnly = true;
        _btnRegEdit!.Visibility = Visibility.Visible;
        _btnRegApply!.Visibility = Visibility.Collapsed;
        _btnRegCancel!.Visibility = Visibility.Collapsed;

        UpdateRegisters();
        UpdateDisassembly();
    }

    private void OnRegCancel(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _regEntries)
            entry.ValueBox.IsReadOnly = true;
        _btnRegEdit!.Visibility = Visibility.Visible;
        _btnRegApply!.Visibility = Visibility.Collapsed;
        _btnRegCancel!.Visibility = Visibility.Collapsed;
        UpdateRegisters();
    }

    // ========================================================================
    // Memory edit mode
    // ========================================================================

    private void OnMemEdit(object sender, RoutedEventArgs e)
    {
        _memEditMode = true;
        BtnMemEdit.Visibility = Visibility.Collapsed;
        BtnMemApply.Visibility = Visibility.Visible;
        BtnMemCancel.Visibility = Visibility.Visible;
        UpdateMemoryDump(_memoryAddress);
    }

    private void OnMemApply(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;

        foreach (var c in _memCells)
        {
            if (c.Box is not TextBox tb) continue;
            if (!byte.TryParse(tb.Text, System.Globalization.NumberStyles.HexNumber, null, out byte v))
                continue;
            if (v != c.Original)
                _plugin.emfe_poke_byte(_instance, c.Address, v);
        }

        ExitMemEdit();
        UpdateMemoryDump(_memoryAddress);
        UpdateDisassembly();
    }

    private void OnMemCancel(object sender, RoutedEventArgs e)
    {
        ExitMemEdit();
        UpdateMemoryDump(_memoryAddress);
    }

    private void ExitMemEdit()
    {
        _memEditMode = false;
        BtnMemEdit.Visibility = Visibility.Visible;
        BtnMemApply.Visibility = Visibility.Collapsed;
        BtnMemCancel.Visibility = Visibility.Collapsed;
    }

    // ========================================================================
    // Console window
    // ========================================================================

    private void DrainPendingConsoleChars()
    {
        // UI thread: pull the buffered chars out of the MainWindow queue into
        // the console window's own terminal. ConsoleWindow.AppendChar is
        // itself a lock+enqueue, which is fine to hit from the UI thread.
        EnsureConsoleWindow();
        if (!_consoleWindow!.IsVisible)
            _consoleWindow.Show();
        lock (_pendingConsoleLock)
        {
            while (_pendingConsoleChars.Count > 0)
                _consoleWindow.AppendChar(_pendingConsoleChars.Dequeue());
        }
    }

    private void EnsureConsoleWindow()
    {
        if (_consoleWindow != null) return;
        _consoleWindow = new ConsoleWindow(
            sendChar: ch =>
            {
                if (_instance != IntPtr.Zero)
                    _plugin.emfe_send_char(_instance, (byte)ch);
            },
            queryTxSpace: () =>
            {
                // Only ask the plugin once per call.  A return of -1 means
                // the plugin doesn't expose buffered console RX and the
                // caller should fall back to its fixed-size burst path.
                if (_instance == IntPtr.Zero) return -1;
                var fn = _plugin?.emfe_console_tx_space;
                return fn?.Invoke(_instance) ?? -1;
            });
        _consoleWindow.Owner = this;
        _consoleWindow.Closed += (_, _) => _consoleWindow = null;
    }

    private void OnToggleConsole(object sender, RoutedEventArgs e)
    {
        if (_consoleWindow != null && _consoleWindow.IsVisible)
        {
            _consoleWindow.Close();
            _consoleWindow = null;
        }
        else
        {
            EnsureConsoleWindow();
            _consoleWindow!.Show();
        }
    }

    private void OnOpenBreakpoints(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;
        if (_breakpointsWindow == null)
        {
            _breakpointsWindow = new BreakpointsWindow(_instance, _plugin!) { Owner = this };
            _breakpointsWindow.Closed += (_, _) => _breakpointsWindow = null;
            _breakpointsWindow.Show();
        }
        else
        {
            _breakpointsWindow.Activate();
        }
    }

    private void OnOpenCallStack(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;
        if (_callStackWindow == null)
        {
            _callStackWindow = new CallStackWindow(_instance, _plugin!) { Owner = this };
            _callStackWindow.Closed += (_, _) => _callStackWindow = null;
            _callStackWindow.Show();
        }
        else
        {
            _callStackWindow.Activate();
        }
    }

    private void OnOpenFramebuffer(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;
        if (_framebufferWindow == null)
        {
            _framebufferWindow = new FramebufferWindow(_instance, _plugin!) { Owner = this };
            _framebufferWindow.Closed += (_, _) => _framebufferWindow = null;
            _framebufferWindow.Show();
        }
        else
        {
            _framebufferWindow.Activate();
        }
    }

    public uint LastStopAddress => _lastStopAddress;
    public EmfeStopReason LastStopReason => _lastStopReason;

    public void ScrollDisassemblyTo(uint address)
    {
        int idx = _disasmAddresses.IndexOf(address);
        if (idx >= 0)
        {
            DisasmList.SelectedIndex = idx;
            DisasmList.ScrollIntoView(DisasmList.Items[idx]);
            return;
        }
        // Address not in current view — re-center disassembly
        if (_instance != IntPtr.Zero &&
            _plugin.emfe_get_program_range(_instance, out ulong progStart, out _) == EmfeResult.OK
            && progStart > 0)
        {
            uint startAddr = address >= 0x40 ? address - 0x40 : 0;
            if (startAddr < (uint)progStart) startAddr = (uint)progStart;

            var lines = new EmfeDisasmLine[64];
            int count = _plugin.emfe_disassemble_range(_instance, startAddr, startAddr + 0x200, lines, 64);

            DisasmList.Items.Clear();
            _disasmAddresses.Clear();

            for (int i = 0; i < count; i++)
            {
                uint addr = (uint)lines[i].address;
                _disasmAddresses.Add(addr);
                bool isBP = _breakpointAddresses.ContainsKey(addr);

                var row = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var bpInd = new TextBlock { FontFamily = ConsolasFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                if (isBP) { bpInd.Text = "\u25CF"; bpInd.SetResourceReference(TextBlock.ForegroundProperty, "ThemeBreakpointFg"); }
                Grid.SetColumn(bpInd, 0);

                string text = $"{addr.ToString(_addrDigits == 4 ? "X4" : "X8")}  {lines[i].RawBytes,-12}  {lines[i].Mnemonic,-8} {lines[i].Operands}";
                var mainText = new TextBlock { Text = text, FontFamily = ConsolasFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
                if (isBP) mainText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeBreakpointFg");
                Grid.SetColumn(mainText, 1);

                row.Children.Add(bpInd);
                row.Children.Add(mainText);
                DisasmList.Items.Add(row);

                if (addr == address)
                {
                    DisasmList.SelectedIndex = i;
                    DisasmList.ScrollIntoView(row);
                }
            }
        }
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (_instance == IntPtr.Zero) return;
        var dlg = new SettingsWindow(_instance, _plugin!) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            ApplyThemeFromSettings();
            UpdateBoardTypeText();
            _callStackWindow?.Refresh();
            StatusText.Text = "Settings applied";
        }
    }

    // ========================================================================
    // Theme
    // ========================================================================

    private void ApplyThemeFromSettings()
    {
        if (_instance == IntPtr.Zero) return;
        var themePtr = _plugin.emfe_get_setting(_instance, "Theme");
        var theme = Marshal.PtrToStringAnsi(themePtr) ?? "Dark";
        ApplyTheme(theme);
    }

    private void ApplyTheme(string themeName)
    {
        bool isDark = ThemeHelper.ResolveDarkMode(themeName ?? "Dark");
        if (isDark == ThemeHelper.IsDarkMode) return;

        App.ApplyTheme(isDark);
        ThemeHelper.SetAppMode(isDark);
        ThemeHelper.ApplyTitleBar(this, isDark);
        if (_consoleWindow != null) ThemeHelper.ApplyTitleBar(_consoleWindow, isDark);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_statsTimer != null)
        {
            _statsTimer.Stop();
            _statsTimer = null;
        }
        _consoleWindow?.Close();
        if (_instance != IntPtr.Zero && _plugin != null)
        {
            _plugin.emfe_stop(_instance);
            _plugin.emfe_destroy(_instance);
            _instance = IntPtr.Zero;
        }
        _plugin?.Dispose();
        _plugin = null;
        base.OnClosed(e);
    }
}
