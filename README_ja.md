# emfe_CsWPF

[![Build and Release](https://github.com/hha0x617/emfe_CsWPF/actions/workflows/build.yml/badge.svg)](https://github.com/hha0x617/emfe_CsWPF/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/hha0x617/emfe_CsWPF?include_prereleases&sort=semver)](https://github.com/hha0x617/emfe_CsWPF/releases)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

[English documentation (README.md)](README.md)

emfe プラグインアーキテクチャの **C# WPF** フロントエンド。

[emfe_plugin_mc68030](https://github.com/hha0x617/emfe_plugins/tree/master/mc68030) のようなプラグイン DLL を P/Invoke で動的ロードし、レジスタ・逆アセンブリ・メモリダンプ・コンソールを表示します。

## 機能

- em68030 互換レイアウト ([emfe_WinUI3Cpp](../emfe_WinUI3Cpp/) と同等)
- レジスタパネル: データ駆動で動的生成 (D0-D7 / A0-A7 の 2列グリッド + Flags + Special/FPU/MMU)
- 逆アセンブリ: PC行背景ハイライト + ブレークポイントインジケータ
- メモリダンプ: TextBox 形式 (WPF) + 編集モード
- 実行制御: Step (F10), Step Over (F11), Step Out (Shift+F11), Run (F5), Stop (Shift+F5), Reset, Full Reset
- コンソールウィンドウ: 別ウィンドウ、緑/黒配色、自動表示、キー入力
- 設定ダイアログ: プラグインから取得した setting defs を動的 UI 化

## ディレクトリ構造

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
    ├── PluginInterop.cs    P/Invoke 宣言
    └── bin/Release/        ビルド成果物
```

## 依存関係

[emfe_plugins](https://github.com/hha0x617/emfe_plugins) を本リポジトリの
兄弟ディレクトリに配置してプラグインをビルドすると、csproj が build
成果物を自動でコピーします。csproj 内の `<None Include>` エントリは、
**csproj 自身 (`emfe/emfe.csproj`) からの相対パス** でプラグイン DLL を
参照しています。

| 依存先 | 想定パス (`emfe/emfe.csproj` から見て) | 用途 |
|-------|----------------------------------------|------|
| `emfe_plugin_mc68030.dll` | `..\..\emfe_plugins\mc68030\build\bin\Release\` | 実行時プラグイン (ビルドされていれば自動コピー) |
| `emfe_plugin_em8.dll` | `..\..\emfe_plugins\em8\build\bin\Release\` | 実行時プラグイン (ビルドされていれば自動コピー) |
| `emfe_plugin_z8000.dll` | `..\..\emfe_plugins\z8000\build\bin\Release\` | 実行時プラグイン (ビルドされていれば自動コピー) |
| `emfe_plugin_mc6809.dll` | `..\..\emfe_plugins\mc6809\target\release\` | 実行時プラグイン (ビルドされていれば自動コピー) |

各 `<None Include>` は `Condition="Exists(...)"` で守られているため、
プラグイン DLL が欠けても build は止まらず、ホスト側の Switch Plugin ダイアログに
該当項目が現れないだけです。DLL を同梱したい場合は、
`../emfe_plugins/<name>/` 側を先にビルドしてください。

csproj はビルド時に上記から `emfe/bin/Release/net10.0-windows/plugins/` へ
`emfe_plugin_*.dll` をコピーする。フロントエンドは起動時に
`<exe_dir>\plugins\emfe_plugin_*.dll` をスキャンして "Switch Plugin"
ダイアログに列挙する。

### システム要件

- Windows 10 / 11
- .NET 10 SDK
- Visual Studio 2026 (任意, コマンドラインでも可)

## ビルド

```bash
# 復元 + ビルド
dotnet build emfe_CsWPF.sln -c Release
```

Visual Studio 2026 から `emfe_CsWPF.sln` を開いてビルド / F5 デバッグ実行も可能。

出力: `emfe/bin/Release/net10.0-windows/emfe.exe`

## 実行方法

### プラグイン DLL の配置

csproj の自動コピーで通常は不要。開発中に手動配置したい場合 (以下は
`emfe_plugins` が兄弟ディレクトリにある前提で、`emfe_CsWPF/` リポジトリ
ルートから実行):

```bash
mkdir -p emfe/bin/Release/net10.0-windows/plugins/
cp ../emfe_plugins/mc68030/build/bin/Release/emfe_plugin_mc68030.dll \
   emfe/bin/Release/net10.0-windows/plugins/
```

他プラグイン (em8 / z8000 / mc6809) も同様に `plugins/` へ配置すれば
Switch Plugin ダイアログから選択できる。

### 基本操作

1. `emfe.exe` を実行
2. **File → Open ELF...** (Ctrl+E) または **Open S-Record...** (Ctrl+S) でプログラムをロード
3. **View → Console** でコンソールウィンドウを開く
4. **Run (F5)** / **Step (F10)** で実行
5. **逆アセンブリ行のダブルクリック** でブレークポイントをトグル
6. **Settings → Emulator Settings...** で設定ダイアログを開く

## P/Invoke パターン

`PluginInterop.cs` で全 emfe API を P/Invoke 宣言。主要な型変換:

| C ABI | C# |
|-------|-----|
| `EmfeInstance` | `IntPtr` |
| `const char*` | `IntPtr` (呼び出し側で `Marshal.PtrToStringAnsi` で読む) |
| `const char*` 引数 | `string` + `CharSet = CharSet.Ansi` |
| 構造体配列 | `[In, Out] T[]` |
| コールバック | `[UnmanagedFunctionPointer(CallingConvention.Cdecl)]` delegate |

## 関連プロジェクト

- [emfe_plugins/api](https://github.com/hha0x617/emfe_plugins/tree/master/api) — 共通 C ABI ヘッダ + 開発者ドキュメント
- [emfe_plugins/mc68030](https://github.com/hha0x617/emfe_plugins/tree/master/mc68030) — MC68030 プラグイン DLL
- [emfe_plugins/em8](https://github.com/hha0x617/emfe_plugins/tree/master/em8) — EM8 (ABI 検証用の自作最小 8bit CPU) プラグイン
- [emfe_plugins/z8000](https://github.com/hha0x617/emfe_plugins/tree/master/z8000) — Zilog Z8000 ファミリープラグイン
- [emfe_plugins/mc6809](https://github.com/hha0x617/emfe_plugins/tree/master/mc6809) — Motorola MC6809 プラグイン (Rust)
- [emfe_WinUI3Cpp](https://github.com/hha0x617/emfe_WinUI3Cpp) — C++ WinUI 3 フロントエンド (同等機能)

## ライセンス

Apache License 2.0 — 詳細は [LICENSE](LICENSE) を参照
