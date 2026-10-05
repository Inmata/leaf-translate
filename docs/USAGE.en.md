# Leaf user guide

[中文](USAGE.md) · [Project overview](https://github.com/Inmata/leaf-translate/blob/main/README.en.md) · [Download](https://github.com/Inmata/leaf-translate/releases/latest)

The current application UI is Chinese. This guide gives the Chinese label alongside the English description where needed.

## First launch

Download the Windows ZIP from Releases, extract everything, and launch `Leaf.exe`. Keep `Leaf.exe.config` in the same folder. Windows 10/11 and .NET Framework 4.8 are required.

Settings opens on first launch. It can also be opened through **Settings (设置)** in the tray menu. Check the taskbar's hidden-icons area if the tray icon is not visible.

## Configure an API

Start with **API Key**, then choose an **API service (API 服务)**. OpenAI, Zhipu (智谱), Qwen (千问), and DeepSeek supply official presets. For another service, choose **Custom compatible service (自定义兼容服务)** and enter its base URL under **Endpoint (接口地址)**. You can also enter the URL before the key.

Once the endpoint is known, Leaf automatically fetches models after you finish typing, with loading and result feedback. Choose a text-chat model or use **Refresh models (刷新模型)** to retry. Keys are never used to guess or probe providers. Catalog ownership is displayed when supplied, otherwise it is not guessed. A manual model ID remains available when a catalog is unsupported. Saving remembers the endpoint for subsequent key changes.

A catalog entry does not establish account access or quota; test the connection after selecting it. A stored key is not displayed in the input. When **Key saved (已保存密钥)** appears below it, leaving the field empty continues using that key. Fetching a catalog authenticates with the configured service without sending your text or conversation.

Expand **Endpoint (接口地址)** to change the URL or request deletion of a saved key. Changing the URL requires entering the key again. HTTPS endpoints are accepted; localhost HTTP is available for development.

**Test connection (测试连接)** sends a short request using the currently edited configuration. It may count toward provider usage. After a successful test, click **Save settings (保存设置)** to apply the configuration. Model availability and pricing are determined by your provider account.

**Advanced options (高级选项)** are collapsed initially. Thinking mode supports Auto, Enabled, and Disabled; Auto is recommended. GLM-5.3 / Flash require thinking, so Disabled is unavailable. They default to light effort, with high and maximum effort available. Unknown GLM models keep the provider default. Thinking requests can wait up to 120 seconds; only answer text is displayed.

OpenAI and custom endpoints keep model-default reasoning without other providers' special parameters. The thinking control is disabled there; the output cap remains adjustable. This version supports Chat Completions, not services offering only Responses, native Anthropic Messages, or other protocols.

Leave the output token cap blank to adapt automatically; GLM-5.3 defaults to 8192. Custom limits range from 256 to 32768 and include reasoning and visible output. A small cap can truncate the answer. The cap is not actual usage; your provider bills the tokens it uses.

## Translate text

New settings have clipboard mode disabled. Select text, then press `Ctrl+Alt+T`. Upgrades preserve your saved capture mode.

To copy first, enable **Clipboard mode (剪贴板模式)** in the tray or **Read clipboard with shortcut (快捷键读取剪贴板)** in settings. Press `Ctrl+C`, then the translation shortcut. The popup identifies clipboard text as **From clipboard (来自剪贴板)**. Copying alone never uploads or translates text. Unsupported or elevated applications may require manual copying. A failed capture shows an error rather than translating stale clipboard text.

Source text is limited to 6000 UTF-16 code units per request. Split long passages when prompted. Network errors, timeouts, invalid keys, exhausted quota, rate limits, and malformed responses receive visible messages. Retry manually or update settings as appropriate.

## Explore words and phrases

Click a word in the original passage to open its contextual card. A matching translation phrase is highlighted only when a valid phrase is available. Use **Back to sentence (返回整句)** to return to discussing the whole passage.

For phrases or scripts without spaces, select text inside the popup and use **Explain selected passage (解释选中片段)** in the context menu. Clicking individual words depends on local text segmentation and is not a specialist tokenizer for every language.

## Ask follow-up questions

**Ask (追问)** reveals the question input. It does not pin the popup automatically. Questions concern either the current word or the passage; the topic is displayed above the input.

Use `Enter` to send or `Shift+Enter` for a new line. A new source opens a new conversation. Completed conversations can be restored from history.

## Set context and learning preferences

Contexts are General (通用), Books (书籍), Film and TV (影视), Technical documents (技术文档), Games (游戏), and Custom (自定义). Source language is detected by the model; **Target language (目标语言)** defaults to Chinese and is editable.

Only fields relevant to the selected context appear. Books show an optional title; games show a game name and text type. Switching contexts clears the previous context's detail field.

Choose base form and inflection (原形与词形), synonym comparison (近义词比较), word formation and roots (构词与词根), collocations (常见搭配), or short examples (短例句).

Expand **Add a learning preference (添加自己的学习偏好)** to provide a name and instruction. The new preference is selectable; its adjacent × removes it. **Preference presets (偏好预设)** save the language, context, and learning options together. Save settings after editing to apply the changes.

## Window and desktop preferences

The popup does not activate when opened. Click it to interact. Clicking outside hides an unpinned window; pinning keeps it visible. The hide button remains available when pinned.

The first popup appears on the right of the primary display. Moving and resizing automatically saves the last position and size for subsequent shortcuts and restarts. Retry keeps a visible popup in place. Drag to a secondary monitor directly; if it is removed or the work area shrinks, Leaf brings the popup into an available area.

Shrinking the window moderately reduces body text, line spacing, and padding, with readable minimum sizes. Buttons retain their size and long content remains scrollable. Larger windows do not enlarge text.

Click the shortcut field and press a combination containing Ctrl, Alt, or Win plus another key. `Esc` cancels; save settings to apply it. The live shortcut is temporarily released during recording and restored afterward.

A visible popup has a taskbar icon; hiding it keeps Leaf in the tray. The shortcut also supports **Alt+Space**. While configured, Leaf uses that combination for translation instead of the Windows system menu. Holding it triggers once; exiting releases it. If another shortcut utility uses it, change the combination in one of the applications.

Hiding the popup keeps the current conversation and draft. Use **Exit (退出)** in the tray menu to stop Leaf. Starting with Windows is disabled by default. If you enable it, keep the app in a stable folder; after moving it, turn startup off and on again.

## History and stored data

Successful translations are saved locally by default. The original passage, translation, word cards, and follow-up conversation form one record. Copied text that was never translated is not saved.

Open **History (历史记录)** from the tray, search the source or translation, and double-click a record or choose **View and continue (查看与继续追问)**. Reopening makes no API call. New word lookups and follow-ups use the current API endpoint and model while preserving the record's learning context.

History keeps the latest 200 entries by default. Retention can be set to 20–1000, with an additional content-size limit. Delete or clear records as needed. **Disabling history saving clears existing records and prevents new ones.**

Settings and history are stored in `%LOCALAPPDATA%\LeafTranslate`. If a file cannot be read, Leaf attempts to preserve a `.corrupt-...` backup. API keys are stored separately in Windows Credential Manager under `LeafTranslate/provider-id`.

Translation sends source text and context to the configured endpoint. Word lookups and follow-ups also send relevant translations, preferences, or recent conversation. Leaf has no project-operated relay server. Stopping a request ends the local wait but does not guarantee the provider will waive usage charges.

## Diagnostic logs

Use **Open logs (打开日志)** in the tray to open `%LOCALAPPDATA%\LeafTranslate\logs`. The current file is `leaf.log`, with backups `leaf.1.log` through `leaf.4.log`. Each file is limited to 1 MiB and rotates automatically.

Each line is a JSON record containing time, version, request ID, provider/model, endpoint host, duration, HTTP status, provider code/related parameter, exception type, and application method locations. API keys, source text, translations, learning context, questions, and raw error bodies are excluded. Nothing is automatically uploaded. A `started` record with no ending record can indicate a forced shutdown.

For a bug report, provide reproduction steps and the error time, and optionally the relevant log. Review it before sharing; do not attach credentials or private history. An unwritable log directory does not stop translation.

## Common problems

| Problem | Try this |
| --- | --- |
| Tray icon is missing | Check the taskbar's hidden-icons area |
| Selection cannot be captured | Switch to clipboard mode and copy manually |
| Shortcut is occupied | Choose another Ctrl, Alt, or Win combination in settings |
| Model is unavailable | Use a model supported by your provider account |
| Key input is empty after saving | Stored keys are hidden; leaving the input blank keeps the saved key |
| Another launch does not open a second instance | Leaf runs as one instance and attempts to reveal the existing popup |
| Windows reports an unsigned app | The app is unsigned; inspect the published source and build instructions if needed |

This version does not include OCR, screenshot translation, or exclusive-fullscreen support, and has no macOS/Linux build. LLM explanations may be incorrect. For help, [report an issue](https://github.com/Inmata/leaf-translate/issues) with the app version, Windows version, capture mode, and reproduction steps; omit credentials.
