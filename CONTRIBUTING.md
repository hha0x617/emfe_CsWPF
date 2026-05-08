# Contributing to emfe_CsWPF

Thanks for your interest!  This is the **C# WPF** host for the emfe plugin
architecture.  Plugin DLLs (`emfe_plugin_*.dll`) are loaded dynamically at
runtime via `[DllImport]`, so this project has **no build-time dependency**
on the plugins themselves.

## Getting the source

```bash
git clone https://github.com/hha0x617/emfe_CsWPF.git
```

## Build prerequisites

- **.NET 10 SDK** ([download](https://dotnet.microsoft.com/download/dotnet/10.0))
- Windows 10 1809 or later (for running the app; builds work cross-platform
  but the runtime target is Windows)

## Building

```bash
dotnet restore emfe_CsWPF.sln
dotnet build emfe/emfe.csproj -c Release
```

Run the app:

```bash
dotnet run --project emfe/emfe.csproj -c Release
```

At runtime, the host looks for plugins under `plugins/` next to `emfe.exe`.
Grab the latest plugin release zip from
[emfe_plugins releases](https://github.com/hha0x617/emfe_plugins/releases)
and extract it next to the built `emfe.exe`.

## Making a change

1. Fork the repository and create a feature branch off `master`.
2. Keep commits focused; write commit messages that explain the *why*.
3. Open a pull request against `master`.  CI must pass before merge.

## Commit style

- Subject line ≤ 72 chars, imperative mood (`fix: ...`, `feat(ui): ...`,
  `docs: ...`, `ci: ...`, `chore: ...`).
- Body wrapped to 72 chars, focused on motivation and trade-offs.

## Reporting bugs / requesting features

Use the issue templates in [`.github/ISSUE_TEMPLATE/`](.github/ISSUE_TEMPLATE/).
Security vulnerabilities go through [`SECURITY.md`](SECURITY.md) instead.

## Code of Conduct

This project follows the [Contributor Covenant 2.1](CODE_OF_CONDUCT.md).
By participating you are expected to uphold those standards.  Reports
of unacceptable behaviour go to the contact address listed in the
Code of Conduct.

## License

By submitting a contribution you agree it will be licensed under the
**Apache-2.0** terms as the rest of the repository.
