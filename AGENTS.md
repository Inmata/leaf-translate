# Leaf

Windows desktop selection-and-clipboard translation with user-provided LLM APIs.

- Read docs/PRODUCT.md before changing product behavior.
- Keep the popup minimal. Translation service settings and reading preferences use a settings page inside the same popup; preserve the translation page and session when switching. History retains its existing separate entry.
- Opening preserves focus by default; only the explicit direct-input preference activates it. Follow-up reveals the input and never automatically pins.
- Clipboard mode reads only on shortcut invocation; never upload every copied item.
- Preserve Unicode and context. Similar spelling is not evidence of common etymology.
- Keys belong in Windows Credential Manager, never in settings, history or source.
- Diagnostic logs contain structured metadata only; never log keys or reading/conversation content.
- Leaf is currently a small exploratory application. Keep validation proportional to the changed behavior; do not introduce a new test framework, broad test infrastructure or mandatory TDD without an explicit need.
- Build with scripts/build.ps1. Use existing test entry points for necessary logic regression, including scripts/test.ps1 when appropriate. Validate a completed group together; rerun only after a relevant change or actual failure.
- Human-check visible UI and ordinary interactions. Agents verify builds and logic that is hard to reproduce or observe manually. Reuse smoke only for cheap XAML loading checks; do not add visual pixel checks, screenshot matrices or visual-only test seams unless explicitly requested.
- For WPF layout exceptions, regress the failing operation with a minimal valid layout; do not expand the fix into precise drawing geometry tests or add public geometry accessors only for tests. If the host cannot exercise the failing path, report that limitation rather than treating a zero-layout pass as coverage.
- Before joint validation, update affected control casts, interface references and expectations to match the confirmed behavior. After a failure, rerun the affected existing test entry; distinguish a failed full run from a later targeted pass.
- Leaf targets .NET Framework 4.8. In-process WPF test helpers must use a compatible STA runtime; PowerShell 7 assembly loading is not equivalent to Windows PowerShell 5.1. Prefer the existing test executable over creating new helper scripts. If Leaf.exe is in use, use build.ps1 -OutputDirectory instead of terminating the user's process.
- Exclude binaries, local data, credentials and work/ from public source.
