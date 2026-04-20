# emfe_CsWPF

[![Build and Release](https://github.com/hha0x617/emfe_CsWPF/actions/workflows/build.yml/badge.svg)](https://github.com/hha0x617/emfe_CsWPF/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/hha0x617/emfe_CsWPF?include_prereleases&sort=semver)](https://github.com/hha0x617/emfe_CsWPF/releases)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

[日本語 (README_ja.md)](README_ja.md)

**C# / WPF** front-end for the emfe plugin architecture.

The host dynamically loads plugin DLLs such as
[emfe_plugin_mc68030](https://github.com/hha0x617/emfe_plugins/tree/master/mc68030)
via P/Invoke and surfaces a register panel, disassembly view, memory dump,
and console window.

## Features

- Em68030-style layout (parity with [emfe_WinUI3Cpp](https://github.com/hha0x617/emfe_WinUI3Cpp))
- Register panel: data-driven dynamic layout (D0–D7 / A0–A7 two-column grid
  + Flags + Special / FPU / MMU)
- Disassembly: PC-line background highlight + breakpoint indicator
- Memory dump: TextBox (WPF) + edit mode
- Execution control: Step (F10), Step Over (F11), Step Out (Shift+F11),
  Run (F5), Stop (Shift+F5), Reset, Full Reset
- Console window: separate window, green/black colour scheme, auto-show,
  keyboard input
- Settings dialog: plugin-supplied setting defs rendered as dynamic UI

## Directory layout

```
emfe_CsWPF/
├── emfe_CsWPF.sln
├── README.md
└── emfe/
    ├── emfe.csproj
    ├── App.xaml / App.xaml.cs
    ├── MainWindow.xaml / MainWindow.xaml.cs
    ├── ConsoleWindow.xaml / ConsoleWindow.xaml.cs
    ├── SettingsWindow.xaml / SettingsWindow.xaml.cs
    ├── PluginInterop.cs    P/Invoke declarations
    └── bin/Release/        build output
```

## Dependencies

| Depends on | Expected path | Purpose |
|-----------|---------------|---------|
| Plugin DLLs (`emfe_plugin_*.dll`) | `../emfe_plugins/{mc68030,em8,z8000}/build/bin/Release/` (mc6809 at `target/release/`) | The csproj's `<None Include>` entries auto-copy them |

At build time the csproj copies `emfe_plugin_*.dll` from the paths above into
`emfe/bin/Release/net10.0-windows/plugins/`.  At startup the front-end scans
`<exe_dir>\plugins\emfe_plugin_*.dll` and lists the results in the
"Switch Plugin" dialog.

### System requirements

- Windows 10 / 11
- .NET 10 SDK
- Visual Studio 2026 (optional — the command line is sufficient)

## Build

```bash
# Restore + build
dotnet build emfe_CsWPF.sln -c Release
```

Open `emfe_CsWPF.sln` in Visual Studio 2026 to build / debug (F5) as well.

Output: `emfe/bin/Release/net10.0-windows/emfe.exe`

## Running

### Placing plugin DLLs

Usually not necessary — the csproj copies them automatically.  During
development you can drop them in manually:

```bash
mkdir -p emfe/bin/Release/net10.0-windows/plugins/
cp ../emfe_plugins/mc68030/build/bin/Release/emfe_plugin_mc68030.dll \
   emfe/bin/Release/net10.0-windows/plugins/
```

Other plugins (em8 / z8000 / mc6809) can be placed under `plugins/` the same
way and will show up in the Switch Plugin dialog.

### Basic usage

1. Run `emfe.exe`
2. **File → Open ELF...** (Ctrl+E) or **Open S-Record...** (Ctrl+S) to load
   a program
3. **View → Console** to open the console window
4. **Run (F5)** / **Step (F10)** to execute
5. **Double-click a disassembly line** to toggle a breakpoint
6. **Settings → Emulator Settings...** to open the settings dialog

## P/Invoke pattern

`PluginInterop.cs` declares every emfe API as P/Invoke.  Key type mappings:

| C ABI | C# |
|-------|-----|
| `EmfeInstance` | `IntPtr` |
| `const char*` return | `IntPtr` (read with `Marshal.PtrToStringAnsi`) |
| `const char*` argument | `string` + `CharSet = CharSet.Ansi` |
| Struct array | `[In, Out] T[]` |
| Callback | `[UnmanagedFunctionPointer(CallingConvention.Cdecl)]` delegate |

## Related projects

- [emfe_plugins/api](https://github.com/hha0x617/emfe_plugins/tree/master/api) — shared C ABI headers + developer docs
- [emfe_plugins/mc68030](https://github.com/hha0x617/emfe_plugins/tree/master/mc68030) — MC68030 plugin DLL
- [emfe_plugins/em8](https://github.com/hha0x617/emfe_plugins/tree/master/em8) — EM8 (custom 8-bit teaching CPU) plugin
- [emfe_plugins/z8000](https://github.com/hha0x617/emfe_plugins/tree/master/z8000) — Zilog Z8000 family plugin
- [emfe_plugins/mc6809](https://github.com/hha0x617/emfe_plugins/tree/master/mc6809) — Motorola MC6809 plugin (Rust)
- [emfe_WinUI3Cpp](https://github.com/hha0x617/emfe_WinUI3Cpp) — C++ WinUI 3 front-end (feature-parity)

## License

Apache License 2.0 — see [LICENSE](LICENSE).
