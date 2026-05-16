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

*Developed through vibe coding with
[Claude Code](https://docs.anthropic.com/en/docs/claude-code).*

## Features

- Em68030-style layout (parity with [emfe_WinUI3Cpp](https://github.com/hha0x617/emfe_WinUI3Cpp))
- Register panel: data-driven dynamic layout (D0–D7 / A0–A7 two-column grid
  + Flags + Special / FPU / MMU)
- Disassembly: PC-line background highlight + breakpoint indicator
- Memory dump: TextBox (WPF) + edit mode
- Execution control: Step (F10), Step Over (F11), Step Out (Shift+F11),
  Run (F5), Stop (Shift+F5), Reset, Full Reset
- Serial Console window: separate window, green/black colour scheme,
  auto-show, keyboard input
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

Place [emfe_plugins](https://github.com/hha0x617/emfe_plugins) as a sibling
directory of this repository and build the plugins — the csproj references
the resulting DLLs **relative to itself** (`emfe/emfe.csproj`):

| Depends on | Expected path (from `emfe/emfe.csproj`) | Purpose |
|-----------|------------------------------------------|---------|
| `emfe_plugin_mc68030.dll` | `..\..\emfe_plugins\mc68030\build\bin\Release\` | Runtime plugin (copied if built) |
| `emfe_plugin_em8.dll` | `..\..\emfe_plugins\em8\build\bin\Release\` | Runtime plugin (copied if built) |
| `emfe_plugin_z8000.dll` | `..\..\emfe_plugins\z8000\build\bin\Release\` | Runtime plugin (copied if built) |
| `emfe_plugin_mc6809.dll` | `..\..\emfe_plugins\mc6809\target\release\` | Runtime plugin (copied if built) |

Each `<None Include>` guards with `Condition="Exists(...)"`, so missing
plugin DLLs are silently skipped — the host simply won't see them in the
Switch Plugin dialog.  Build the plugins inside
`../emfe_plugins/<name>/` first if you want them bundled.

At build time the csproj copies the DLLs into
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
development you can drop them in manually (paths below assume the
`emfe_plugins` sibling-directory layout, run from the `emfe_CsWPF/`
repo root):

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
3. **View → Serial Console** to open the serial console window
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

## Contributing and Policies

- Contribution workflow: [`CONTRIBUTING.md`](CONTRIBUTING.md)
- Code of Conduct: [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md) (Contributor Covenant 2.1)
- Security: [`SECURITY.md`](SECURITY.md)

## License

Apache License 2.0 — see [LICENSE](LICENSE).
