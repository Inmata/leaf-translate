# Leaf

Windows desktop selection-and-clipboard translation with user-provided LLM APIs.

- Read docs/PRODUCT.md before changing product behavior.
- Keep the popup minimal; providers, scenes, preferences and history belong in tray windows.
- Opening preserves focus by default; only the explicit direct-input preference activates it. Follow-up reveals the input and never automatically pins.
- Clipboard mode reads only on shortcut invocation; never upload every copied item.
- Preserve Unicode and context. Similar spelling is not evidence of common etymology.
- Keys belong in Windows Credential Manager, never in settings, history or source.
- Diagnostic logs contain structured metadata only; never log keys or reading/conversation content.
- Build with scripts/build.ps1; test with scripts/test.ps1.
- Verify embedded WPF XAML with the application's UI smoke renderer.
- Exclude binaries, local data, credentials and work/ from public source.
