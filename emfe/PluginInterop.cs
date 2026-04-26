using System;
using System.Runtime.InteropServices;

namespace emfe;

// Enumerations matching emfe_plugin.h
public enum EmfeRegType { Int = 0, Float = 1, Float80 = 2 }
public enum EmfeState { Stopped = 0, Running = 1, Halted = 2, Stepping = 3 }
public enum EmfeStopReason { None = 0, User = 1, Breakpoint = 2, Watchpoint = 3, Step = 4, Halt = 5, Exception = 6 }
public enum EmfeResult { OK = 0, Invalid = -1, State = -2, NotFound = -3, IO = -4, Memory = -5, Unsupported = -6 }
public enum EmfeSettingType { Int = 0, String = 1, Bool = 2, Combo = 3, File = 4, List = 5 }
public enum EmfeWatchpointSize { Byte = 1, Word = 2, Long = 4 }
public enum EmfeWatchpointType { Read = 0, Write = 1, ReadWrite = 2 }
public enum EmfeCallStackKind { Call = 0, Exception = 1, Interrupt = 2 }
public enum EmfeFramebufferFormat { Indexed8 = 8, Rgb565 = 16, Rgb888 = 24, Rgba8888 = 32 }

[Flags]
public enum EmfeRegFlags : uint
{
    None     = 0,
    ReadOnly = 1 << 0,
    PC       = 1 << 1,
    SP       = 1 << 2,
    Flags    = 1 << 3,
    FPU      = 1 << 4,
    MMU      = 1 << 5,
    Hidden   = 1 << 6
}

public static class EmfeSettingFlags
{
    public const uint REQUIRES_RESET = 1u << 0;
}

[StructLayout(LayoutKind.Sequential)]
public struct EmfeSettingDef
{
    public IntPtr key;
    public IntPtr label;
    public IntPtr group;
    public EmfeSettingType type;
    public IntPtr default_value;
    public IntPtr constraints;
    public IntPtr depends_on;
    public IntPtr depends_value;
    public uint flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct EmfeListItemDef
{
    public IntPtr key;
    public IntPtr label;
    public EmfeSettingType type;
    public IntPtr constraints;
}

// Structures matching emfe_plugin.h
[StructLayout(LayoutKind.Sequential)]
public struct EmfeNegotiateInfo
{
    public uint api_version_major;
    public uint api_version_minor;
    public uint flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct EmfeRegisterDef
{
    public uint reg_id;
    public IntPtr name;      // const char*
    public IntPtr group;     // const char*
    public EmfeRegType type;
    public uint bit_width;
    public uint flags;

    public string Name => Marshal.PtrToStringAnsi(name) ?? "";
    public string Group => Marshal.PtrToStringAnsi(group) ?? "";
}

[StructLayout(LayoutKind.Sequential)]
public struct EmfeRegFlagBitDef
{
    public byte bit_index;     // 0 = LSB
    public IntPtr label;       // const char*

    public string Label => Marshal.PtrToStringAnsi(label) ?? "";
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct EmfeRegValue
{
    [FieldOffset(0)] public uint reg_id;
    [FieldOffset(8)] public ulong u64;
    [FieldOffset(8)] public double f64;
}

[StructLayout(LayoutKind.Sequential)]
public struct EmfeDisasmLine
{
    public ulong address;
    public IntPtr raw_bytes;   // const char*
    public IntPtr mnemonic;    // const char*
    public IntPtr operands;    // const char*
    public uint length;

    public string RawBytes => Marshal.PtrToStringAnsi(raw_bytes) ?? "";
    public string Mnemonic => Marshal.PtrToStringAnsi(mnemonic) ?? "";
    public string Operands => Marshal.PtrToStringAnsi(operands) ?? "";
}

public static class EmfeCap
{
    public const ulong LoadElf        = 1UL <<  0;
    public const ulong LoadSrec       = 1UL <<  1;
    public const ulong LoadBinary     = 1UL <<  2;
    public const ulong StepOver       = 1UL <<  3;
    public const ulong StepOut        = 1UL <<  4;
    public const ulong CallStack      = 1UL <<  5;
    public const ulong Watchpoints    = 1UL <<  6;
    public const ulong Framebuffer    = 1UL <<  7;
    public const ulong InputKeyboard  = 1UL <<  8;
    public const ulong InputMouse     = 1UL <<  9;
}

[StructLayout(LayoutKind.Sequential)]
public struct EmfeBoardInfo
{
    public IntPtr board_name;
    public IntPtr cpu_name;
    public IntPtr description;
    public IntPtr version;
    public ulong  capabilities;

    public string BoardName => Marshal.PtrToStringAnsi(board_name) ?? "";
    public string CpuName => Marshal.PtrToStringAnsi(cpu_name) ?? "";
}

[StructLayout(LayoutKind.Sequential)]
public struct EmfeBreakpointInfo
{
    public ulong address;
    [MarshalAs(UnmanagedType.I1)] public bool enabled;
    public IntPtr condition;
}

[StructLayout(LayoutKind.Sequential)]
public struct EmfeWatchpointInfo
{
    public ulong address;
    public EmfeWatchpointSize size;
    public EmfeWatchpointType type;
    [MarshalAs(UnmanagedType.I1)] public bool enabled;
    public IntPtr condition;
}

[StructLayout(LayoutKind.Sequential)]
public struct EmfeFramebufferInfo
{
    public uint width;
    public uint height;
    public uint bpp;
    public uint stride;
    // 4 bytes padding here on x64 for ulong alignment
    public ulong base_address;
    public IntPtr pixels;
    public uint flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct EmfeCallStackEntry
{
    public ulong call_pc;
    public ulong target_pc;
    public ulong return_pc;
    public ulong frame_pointer;
    public EmfeCallStackKind kind;
    // padding: 4 bytes for x64 pointer alignment
    public IntPtr label;
}

[StructLayout(LayoutKind.Sequential)]
public struct EmfeStateInfo
{
    public EmfeState state;
    public EmfeStopReason stop_reason;
    public ulong stop_address;
    public IntPtr stop_message;
}

// Callback delegates
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void EmfeConsoleCharCallback(IntPtr userData, byte ch);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void EmfeStateChangeCallback(IntPtr userData, ref EmfeStateInfo info);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void EmfeDiagnosticCallback(IntPtr userData, IntPtr message);

// Dynamic plugin loader — replaces static P/Invoke
public class PluginInterop : IDisposable
{
    private IntPtr _handle;

    public string DllPath { get; private set; } = "";

    // ====================================================================
    // Function delegate types
    // ====================================================================

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult NegotiateDelegate(ref EmfeNegotiateInfo info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult GetBoardInfoDelegate(out EmfeBoardInfo info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult CreateDelegate(out IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult DestroyDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult SetConsoleCharCallbackDelegate(IntPtr instance, EmfeConsoleCharCallback? cb, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult SetStateChangeCallbackDelegate(IntPtr instance, EmfeStateChangeCallback? cb, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult SetDiagnosticCallbackDelegate(IntPtr instance, EmfeDiagnosticCallback? cb, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetRegisterDefsDelegate(IntPtr instance, out IntPtr defs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetRegisterFlagDefsDelegate(IntPtr instance, uint reg_id, out IntPtr defs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult GetRegistersDelegate(IntPtr instance, [In, Out] EmfeRegValue[] values, int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult SetRegistersDelegate(IntPtr instance, EmfeRegValue[] values, int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate byte PeekByteDelegate(IntPtr instance, ulong address);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate ushort PeekWordDelegate(IntPtr instance, ulong address);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint PeekLongDelegate(IntPtr instance, ulong address);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult PokeByteDelegate(IntPtr instance, ulong address, byte value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult PokeWordDelegate(IntPtr instance, ulong address, ushort value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult PokeLongDelegate(IntPtr instance, ulong address, uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult PeekRangeDelegate(IntPtr instance, ulong address, byte[] outData, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate ulong GetMemorySizeDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult DisassembleOneDelegate(IntPtr instance, ulong address, out EmfeDisasmLine line);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int DisassembleRangeDelegate(IntPtr instance, ulong start, ulong end,
        [In, Out] EmfeDisasmLine[] lines, int maxLines);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult StepDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult StepOverDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult StepOutDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult RunDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult StopDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult ResetDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeState GetStateDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate long GetInstructionCountDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate long GetCycleCountDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult AddBreakpointDelegate(IntPtr instance, ulong address);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult RemoveBreakpointDelegate(IntPtr instance, ulong address);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult EnableBreakpointDelegate(IntPtr instance, ulong address,
        [MarshalAs(UnmanagedType.I1)] bool enabled);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult ClearBreakpointsDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate EmfeResult SetBreakpointConditionDelegate(IntPtr instance, ulong address, string? condition);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetBreakpointsDelegate(IntPtr instance,
        [In, Out] EmfeBreakpointInfo[] breakpoints, int maxCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate EmfeResult LoadElfDelegate(IntPtr instance, string filePath);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate EmfeResult LoadBinaryDelegate(IntPtr instance, string filePath, ulong loadAddress);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate EmfeResult LoadSrecDelegate(IntPtr instance, string filePath);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GetLastErrorDelegate(IntPtr instance);

    // Settings
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetSettingDefsDelegate(IntPtr instance, out IntPtr defs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate IntPtr GetSettingDelegate(IntPtr instance, string key);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate EmfeResult SetSettingDelegate(IntPtr instance, string key, string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult ApplySettingsDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate IntPtr GetAppliedSettingDelegate(IntPtr instance, string key);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult SaveSettingsDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult LoadSettingsDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate EmfeResult SetDataDirDelegate(string path);

    // List settings
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate int GetListItemDefsDelegate(IntPtr instance, string listKey, out IntPtr defs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate int GetListItemCountDelegate(IntPtr instance, string listKey);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate IntPtr GetListItemFieldDelegate(IntPtr instance, string listKey, int itemIndex, string fieldKey);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate EmfeResult SetListItemFieldDelegate(IntPtr instance, string listKey, int itemIndex, string fieldKey, string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate int AddListItemDelegate(IntPtr instance, string listKey);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate EmfeResult RemoveListItemDelegate(IntPtr instance, string listKey, int itemIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate int IsListPendingDelegate(IntPtr instance, string listKey);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult SendCharDelegate(IntPtr instance, byte ch);

    // Returns the number of characters the console RX buffer can accept
    // right now.  0 means full (host should wait), > 0 means that many chars
    // are safe to push, and -1 means the plugin doesn't expose this.  See
    // emfe_plugin.h `emfe_console_tx_space`.  Optional — not every plugin
    // exports it, so PluginInterop leaves it null on older DLLs.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int ConsoleTxSpaceDelegate(IntPtr instance);

    // Watchpoints
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult AddWatchpointDelegate(IntPtr instance, ulong address,
        EmfeWatchpointSize size, EmfeWatchpointType type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult RemoveWatchpointDelegate(IntPtr instance, ulong address);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult EnableWatchpointDelegate(IntPtr instance, ulong address,
        [MarshalAs(UnmanagedType.I1)] bool enabled);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate EmfeResult SetWatchpointConditionDelegate(IntPtr instance, ulong address, string? condition);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult ClearWatchpointsDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetWatchpointsDelegate(IntPtr instance,
        [In, Out] EmfeWatchpointInfo[] watchpoints, int maxCount);

    // Program range
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult GetProgramRangeDelegate(IntPtr instance,
        out ulong startAddr, out ulong endAddr);

    // Call Stack
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetCallStackDelegate(IntPtr instance,
        [In, Out] EmfeCallStackEntry[] entries, int maxCount);

    // Framebuffer
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult GetFramebufferInfoDelegate(IntPtr instance,
        out EmfeFramebufferInfo info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint GetPaletteEntryDelegate(IntPtr instance, uint index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetPaletteDelegate(IntPtr instance,
        [In, Out] uint[] colors, int maxCount);

    // Input
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult PushKeyDelegate(IntPtr instance, uint scancode,
        [MarshalAs(UnmanagedType.I1)] bool pressed);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult PushMouseMoveDelegate(IntPtr instance, int dx, int dy);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult PushMouseAbsoluteDelegate(IntPtr instance, int x, int y);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate EmfeResult PushMouseButtonDelegate(IntPtr instance, int button,
        [MarshalAs(UnmanagedType.I1)] bool pressed);

    // ====================================================================
    // Public function fields (loaded from DLL)
    // ====================================================================

    public NegotiateDelegate emfe_negotiate = null!;
    public GetBoardInfoDelegate emfe_get_board_info = null!;
    public CreateDelegate emfe_create = null!;
    public DestroyDelegate emfe_destroy = null!;
    public SetConsoleCharCallbackDelegate emfe_set_console_char_callback = null!;
    public SetStateChangeCallbackDelegate emfe_set_state_change_callback = null!;
    public SetDiagnosticCallbackDelegate emfe_set_diagnostic_callback = null!;
    public GetRegisterDefsDelegate emfe_get_register_defs = null!;
    public GetRegisterFlagDefsDelegate? emfe_get_register_flag_defs;  // optional
    public GetRegistersDelegate emfe_get_registers = null!;
    public SetRegistersDelegate emfe_set_registers = null!;
    public PeekByteDelegate emfe_peek_byte = null!;
    public PeekWordDelegate emfe_peek_word = null!;
    public PeekLongDelegate emfe_peek_long = null!;
    public PokeByteDelegate emfe_poke_byte = null!;
    public PokeWordDelegate emfe_poke_word = null!;
    public PokeLongDelegate emfe_poke_long = null!;
    public PeekRangeDelegate emfe_peek_range = null!;
    public GetMemorySizeDelegate emfe_get_memory_size = null!;
    public DisassembleOneDelegate emfe_disassemble_one = null!;
    public DisassembleRangeDelegate emfe_disassemble_range = null!;
    public StepDelegate emfe_step = null!;
    public StepOverDelegate emfe_step_over = null!;
    public StepOutDelegate emfe_step_out = null!;
    public RunDelegate emfe_run = null!;
    public StopDelegate emfe_stop = null!;
    public ResetDelegate emfe_reset = null!;
    public GetStateDelegate emfe_get_state = null!;
    public GetInstructionCountDelegate emfe_get_instruction_count = null!;
    public GetCycleCountDelegate emfe_get_cycle_count = null!;
    public AddBreakpointDelegate emfe_add_breakpoint = null!;
    public RemoveBreakpointDelegate emfe_remove_breakpoint = null!;
    public EnableBreakpointDelegate emfe_enable_breakpoint = null!;
    public ClearBreakpointsDelegate emfe_clear_breakpoints = null!;
    public SetBreakpointConditionDelegate emfe_set_breakpoint_condition = null!;
    public GetBreakpointsDelegate emfe_get_breakpoints = null!;
    public LoadElfDelegate emfe_load_elf = null!;
    public LoadBinaryDelegate emfe_load_binary = null!;
    public LoadSrecDelegate emfe_load_srec = null!;
    public GetLastErrorDelegate emfe_get_last_error = null!;
    public GetSettingDefsDelegate emfe_get_setting_defs = null!;
    public GetSettingDelegate emfe_get_setting = null!;
    public SetSettingDelegate emfe_set_setting = null!;
    public ApplySettingsDelegate emfe_apply_settings = null!;
    public GetAppliedSettingDelegate emfe_get_applied_setting = null!;
    public SaveSettingsDelegate emfe_save_settings = null!;
    public LoadSettingsDelegate emfe_load_settings = null!;
    public SetDataDirDelegate emfe_set_data_dir = null!;
    public GetListItemDefsDelegate emfe_get_list_item_defs = null!;
    public GetListItemCountDelegate emfe_get_list_item_count = null!;
    public GetListItemFieldDelegate emfe_get_list_item_field = null!;
    public SetListItemFieldDelegate emfe_set_list_item_field = null!;
    public AddListItemDelegate emfe_add_list_item = null!;
    public RemoveListItemDelegate emfe_remove_list_item = null!;
    public IsListPendingDelegate? emfe_is_list_pending;  // nullable — optional export
    public SendCharDelegate emfe_send_char = null!;
    public ConsoleTxSpaceDelegate? emfe_console_tx_space;  // nullable — optional export
    public AddWatchpointDelegate emfe_add_watchpoint = null!;
    public RemoveWatchpointDelegate emfe_remove_watchpoint = null!;
    public EnableWatchpointDelegate emfe_enable_watchpoint = null!;
    public SetWatchpointConditionDelegate emfe_set_watchpoint_condition = null!;
    public ClearWatchpointsDelegate emfe_clear_watchpoints = null!;
    public GetWatchpointsDelegate emfe_get_watchpoints = null!;
    public GetProgramRangeDelegate emfe_get_program_range = null!;
    public GetCallStackDelegate emfe_get_call_stack = null!;
    public GetFramebufferInfoDelegate emfe_get_framebuffer_info = null!;
    public GetPaletteEntryDelegate emfe_get_palette_entry = null!;
    public GetPaletteDelegate emfe_get_palette = null!;
    public PushKeyDelegate emfe_push_key = null!;
    public PushMouseMoveDelegate emfe_push_mouse_move = null!;
    public PushMouseAbsoluteDelegate emfe_push_mouse_absolute = null!;
    public PushMouseButtonDelegate emfe_push_mouse_button = null!;

    // ====================================================================
    // Load / Dispose
    // ====================================================================

    public bool Load(string dllPath)
    {
        _handle = NativeLibrary.Load(dllPath);
        if (_handle == IntPtr.Zero) return false;
        DllPath = dllPath;

        emfe_negotiate = LoadFunc<NegotiateDelegate>("emfe_negotiate");
        emfe_get_board_info = LoadFunc<GetBoardInfoDelegate>("emfe_get_board_info");
        emfe_create = LoadFunc<CreateDelegate>("emfe_create");
        emfe_destroy = LoadFunc<DestroyDelegate>("emfe_destroy");
        emfe_set_console_char_callback = LoadFunc<SetConsoleCharCallbackDelegate>("emfe_set_console_char_callback");
        emfe_set_state_change_callback = LoadFunc<SetStateChangeCallbackDelegate>("emfe_set_state_change_callback");
        emfe_set_diagnostic_callback = LoadFunc<SetDiagnosticCallbackDelegate>("emfe_set_diagnostic_callback");
        emfe_get_register_defs = LoadFunc<GetRegisterDefsDelegate>("emfe_get_register_defs");
        emfe_get_register_flag_defs = TryLoadFunc<GetRegisterFlagDefsDelegate>("emfe_get_register_flag_defs");
        emfe_get_registers = LoadFunc<GetRegistersDelegate>("emfe_get_registers");
        emfe_set_registers = LoadFunc<SetRegistersDelegate>("emfe_set_registers");
        emfe_peek_byte = LoadFunc<PeekByteDelegate>("emfe_peek_byte");
        emfe_peek_word = LoadFunc<PeekWordDelegate>("emfe_peek_word");
        emfe_peek_long = LoadFunc<PeekLongDelegate>("emfe_peek_long");
        emfe_poke_byte = LoadFunc<PokeByteDelegate>("emfe_poke_byte");
        emfe_poke_word = LoadFunc<PokeWordDelegate>("emfe_poke_word");
        emfe_poke_long = LoadFunc<PokeLongDelegate>("emfe_poke_long");
        emfe_peek_range = LoadFunc<PeekRangeDelegate>("emfe_peek_range");
        emfe_get_memory_size = LoadFunc<GetMemorySizeDelegate>("emfe_get_memory_size");
        emfe_disassemble_one = LoadFunc<DisassembleOneDelegate>("emfe_disassemble_one");
        emfe_disassemble_range = LoadFunc<DisassembleRangeDelegate>("emfe_disassemble_range");
        emfe_step = LoadFunc<StepDelegate>("emfe_step");
        emfe_step_over = LoadFunc<StepOverDelegate>("emfe_step_over");
        emfe_step_out = LoadFunc<StepOutDelegate>("emfe_step_out");
        emfe_run = LoadFunc<RunDelegate>("emfe_run");
        emfe_stop = LoadFunc<StopDelegate>("emfe_stop");
        emfe_reset = LoadFunc<ResetDelegate>("emfe_reset");
        emfe_get_state = LoadFunc<GetStateDelegate>("emfe_get_state");
        emfe_get_instruction_count = LoadFunc<GetInstructionCountDelegate>("emfe_get_instruction_count");
        emfe_get_cycle_count = LoadFunc<GetCycleCountDelegate>("emfe_get_cycle_count");
        emfe_add_breakpoint = LoadFunc<AddBreakpointDelegate>("emfe_add_breakpoint");
        emfe_remove_breakpoint = LoadFunc<RemoveBreakpointDelegate>("emfe_remove_breakpoint");
        emfe_enable_breakpoint = LoadFunc<EnableBreakpointDelegate>("emfe_enable_breakpoint");
        emfe_clear_breakpoints = LoadFunc<ClearBreakpointsDelegate>("emfe_clear_breakpoints");
        emfe_set_breakpoint_condition = LoadFunc<SetBreakpointConditionDelegate>("emfe_set_breakpoint_condition");
        emfe_get_breakpoints = LoadFunc<GetBreakpointsDelegate>("emfe_get_breakpoints");
        emfe_load_elf = LoadFunc<LoadElfDelegate>("emfe_load_elf");
        emfe_load_binary = LoadFunc<LoadBinaryDelegate>("emfe_load_binary");
        emfe_load_srec = LoadFunc<LoadSrecDelegate>("emfe_load_srec");
        emfe_get_last_error = LoadFunc<GetLastErrorDelegate>("emfe_get_last_error");
        emfe_get_setting_defs = LoadFunc<GetSettingDefsDelegate>("emfe_get_setting_defs");
        emfe_get_setting = LoadFunc<GetSettingDelegate>("emfe_get_setting");
        emfe_set_setting = LoadFunc<SetSettingDelegate>("emfe_set_setting");
        emfe_apply_settings = LoadFunc<ApplySettingsDelegate>("emfe_apply_settings");
        emfe_get_applied_setting = LoadFunc<GetAppliedSettingDelegate>("emfe_get_applied_setting");
        emfe_save_settings = LoadFunc<SaveSettingsDelegate>("emfe_save_settings");
        emfe_load_settings = LoadFunc<LoadSettingsDelegate>("emfe_load_settings");
        emfe_set_data_dir = LoadFunc<SetDataDirDelegate>("emfe_set_data_dir");
        emfe_get_list_item_defs = LoadFunc<GetListItemDefsDelegate>("emfe_get_list_item_defs");
        emfe_get_list_item_count = LoadFunc<GetListItemCountDelegate>("emfe_get_list_item_count");
        emfe_get_list_item_field = LoadFunc<GetListItemFieldDelegate>("emfe_get_list_item_field");
        emfe_set_list_item_field = LoadFunc<SetListItemFieldDelegate>("emfe_set_list_item_field");
        emfe_add_list_item = LoadFunc<AddListItemDelegate>("emfe_add_list_item");
        emfe_remove_list_item = LoadFunc<RemoveListItemDelegate>("emfe_remove_list_item");
        emfe_is_list_pending = TryLoadFunc<IsListPendingDelegate>("emfe_is_list_pending");
        emfe_send_char = LoadFunc<SendCharDelegate>("emfe_send_char");
        emfe_console_tx_space = TryLoadFunc<ConsoleTxSpaceDelegate>("emfe_console_tx_space");
        emfe_add_watchpoint = LoadFunc<AddWatchpointDelegate>("emfe_add_watchpoint");
        emfe_remove_watchpoint = LoadFunc<RemoveWatchpointDelegate>("emfe_remove_watchpoint");
        emfe_enable_watchpoint = LoadFunc<EnableWatchpointDelegate>("emfe_enable_watchpoint");
        emfe_set_watchpoint_condition = LoadFunc<SetWatchpointConditionDelegate>("emfe_set_watchpoint_condition");
        emfe_clear_watchpoints = LoadFunc<ClearWatchpointsDelegate>("emfe_clear_watchpoints");
        emfe_get_watchpoints = LoadFunc<GetWatchpointsDelegate>("emfe_get_watchpoints");
        emfe_get_program_range = LoadFunc<GetProgramRangeDelegate>("emfe_get_program_range");
        emfe_get_call_stack = LoadFunc<GetCallStackDelegate>("emfe_get_call_stack");
        emfe_get_framebuffer_info = LoadFunc<GetFramebufferInfoDelegate>("emfe_get_framebuffer_info");
        emfe_get_palette_entry = LoadFunc<GetPaletteEntryDelegate>("emfe_get_palette_entry");
        emfe_get_palette = LoadFunc<GetPaletteDelegate>("emfe_get_palette");
        emfe_push_key = LoadFunc<PushKeyDelegate>("emfe_push_key");
        emfe_push_mouse_move = LoadFunc<PushMouseMoveDelegate>("emfe_push_mouse_move");
        emfe_push_mouse_absolute = LoadFunc<PushMouseAbsoluteDelegate>("emfe_push_mouse_absolute");
        emfe_push_mouse_button = LoadFunc<PushMouseButtonDelegate>("emfe_push_mouse_button");

        return true;
    }

    private T LoadFunc<T>(string name) where T : Delegate
    {
        var ptr = NativeLibrary.GetExport(_handle, name);
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    private T? TryLoadFunc<T>(string name) where T : Delegate
    {
        return NativeLibrary.TryGetExport(_handle, name, out var ptr)
            ? Marshal.GetDelegateForFunctionPointer<T>(ptr)
            : null;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeLibrary.Free(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
