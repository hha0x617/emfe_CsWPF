using System;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace emfe;

public partial class CallStackWindow : Window
{
    private readonly IntPtr _instance;
    private readonly PluginInterop _plugin;
    public ObservableCollection<CallStackRow> Rows { get; } = new();

    public CallStackWindow(IntPtr instance, PluginInterop plugin)
    {
        _instance = instance;
        _plugin = plugin;
        InitializeComponent();
        StackList.ItemsSource = Rows;
        Loaded += (_, _) => ThemeHelper.ApplyTitleBar(this, ThemeHelper.IsDarkMode);
        Refresh();
    }

    private void UpdateTitle()
    {
        var modePtr = _plugin.emfe_get_setting(_instance, "CallStackMode");
        var mode = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(modePtr) ?? "ShadowStack";
        Title = $"Call Stack ({mode})";
    }

    public void Refresh()
    {
        Rows.Clear();
        if (_instance == IntPtr.Zero) { StatusText.Text = ""; return; }
        UpdateTitle();
        var buf = new EmfeCallStackEntry[64];
        int n = _plugin.emfe_get_call_stack(_instance, buf, buf.Length);
        for (int i = 0; i < n; i++)
        {
            Rows.Add(new CallStackRow
            {
                Index = i,
                CallPc = (uint)buf[i].call_pc,
                TargetPc = (uint)buf[i].target_pc,
                ReturnPc = (uint)buf[i].return_pc,
                FramePointer = (uint)buf[i].frame_pointer,
                Kind = buf[i].kind,
                Label = Marshal.PtrToStringAnsi(buf[i].label) ?? ""
            });
        }
        StatusText.Text = $"{n} frame(s)";
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (StackList.SelectedItem is CallStackRow row)
            (Owner as MainWindow)?.ScrollDisassemblyTo(row.CallPc);
    }
}

public class CallStackRow
{
    public int Index { get; set; }
    public uint CallPc { get; set; }
    public uint TargetPc { get; set; }
    public uint ReturnPc { get; set; }
    public uint FramePointer { get; set; }
    public EmfeCallStackKind Kind { get; set; }
    public string Label { get; set; } = "";

    public string CallPcText => $"${CallPc:X8}";
    public string TargetPcText => $"${TargetPc:X8}";
    public string ReturnPcText => $"${ReturnPc:X8}";
    public string FrameText => $"${FramePointer:X8}";
    public string KindText => Kind switch
    {
        EmfeCallStackKind.Call => "CALL",
        EmfeCallStackKind.Exception => "EXCEPTION",
        EmfeCallStackKind.Interrupt => "INTERRUPT",
        _ => Kind.ToString()
    };
}
