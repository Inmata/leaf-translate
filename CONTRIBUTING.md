# Contributing to Leaf

[中文说明](#中文说明) · [Project overview](README.en.md)

Leaf is a native Windows WPF application built with .NET Framework 4.8. Development and automated checks run on Windows. The build uses the framework compiler and embedded XAML, without third-party packages or a .NET SDK requirement.

## Build and run

Clone the repository and open Windows PowerShell in its root directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
.\bin\Leaf.exe --demo
```

The executable and runtime configuration are written to `bin`. Demo mode uses labeled sample content and makes no API requests.

## Verification and packaging

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package.ps1
```

Tests cover transport and response handling, real loopback HTTP, Unicode text, context-aware caching, history, cancellation, conversation switching, and WPF views. The UI smoke renderer writes its images and report to `work/ui-smoke`. These fixtures do not establish live provider or desktop compatibility.

Packaging creates a portable ZIP and SHA256 file in `dist`, verifies its four entries (executable, runtime config, and both user guides), and checks the packaged executable against the build. The Windows GitHub Actions workflow also builds, verifies, and packages the app.

For local Windows integration checks, run `.\bin\Leaf.Tests.exe --native` after building tests. This uses isolated fake credentials and a simulated transport, briefly displays a test popup, and verifies credential persistence, Alt+Space handling, focus, retry, and remembered geometry. It deletes its test credentials and never injects physical desktop input. These checks do not establish compatibility with every desktop or a real provider account.

If a running app locks `bin\Leaf.exe`, use `-OutputDirectory bin/verification` with the build or test script. Packaging always builds into its own directory, and checks that the requested ZIP version matches the executable.

## Repository layout

For an isolated resource benchmark, run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/performance.ps1`. It creates a native tray/WPF process with fresh data and a delayed local SSE fixture, briefly opens its own windows, and writes results under `work/performance/run-...`. It uses a separate Ctrl+Alt+Shift+F12 shortcut and never sends real API requests or reads user settings, history, or saved keys. Metrics include working set, private commit, CPU, threads, handles, GDI/USER objects, and process I/O. See [the measured report](docs/PERFORMANCE-v0.4.0.md) for methodology and limits.

| Path | Purpose |
| --- | --- |
| `src/Leaf` | Application, native Windows integration, storage, and LLM client |
| `src/Leaf/Views` | Embedded WPF XAML and shared theme |
| `tests` | Core, transport, and application request tests |
| `scripts` | Build, verification, and packaging utilities |
| `docs/USAGE.md`, `docs/USAGE.en.md` | User guides |
| `docs/images` | App previews using demonstration data |
| `docs/PRODUCT.md`, `AGENTS.md` | Product behavior and repository conventions |

## Changes and documentation

Read `AGENTS.md` and `docs/PRODUCT.md` before changing behavior. Preserve focus by default on opening (activation requires the explicit direct-input preference), explicit clipboard invocation, contextual word learning, and optional local history. Keys belong in Windows Credential Manager.

Keep Chinese and English documentation consistent. Screenshots should show the actual application with sample text and no credentials or private history; label demonstration content. Keep user-facing READMEs focused on download, setup, and usage.

Use checks appropriate to the change. Validate local links and images for documentation edits. For application changes, run the relevant tests and inspect affected WPF views. Changes to selection, focus, tray behavior, or monitor placement also need desktop checks; report what was actually verified.

Do not commit `bin`, `dist`, `work`, API keys, or local settings and history. For a change proposal, explain the resulting behavior, provide reproduction steps when relevant, and report validation. Issue reports should omit API keys.

## 中文说明

开发环境为 Windows 与 .NET Framework 4.8，不需要额外下载第三方包或安装 .NET SDK。上面的命令依次用于构建、离线演示、检查和便携包生成。

修改行为前阅读 `AGENTS.md` 与 `docs/PRODUCT.md`。文档修改要同步中英文，检查链接和截图；应用修改要完成相关检查并查看受影响的窗口。取词、焦点、托盘和显示器兼容性需要实际桌面验证，说明已做的检查即可。

用户首页负责介绍产品、下载和使用；开发信息放在本文件。截图使用演示数据，不包含密钥或私人历史。不要提交构建产物、本地数据或凭据。
