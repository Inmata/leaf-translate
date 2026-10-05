<p align="center">
  <img src="docs/images/banner.svg" alt="Leaf — Translate. Understand. Keep reading." width="100%">
</p>

<p align="center">
  <a href="https://github.com/Inmata/leaf-translate/releases/latest"><strong>Download for Windows</strong></a> ·
  <a href="#quick-start">Quick start</a> · <a href="#screenshots">Screenshots</a> ·
  <a href="https://github.com/Inmata/leaf-translate/issues">Feedback</a>
</p>

<p align="center">
  <a href="https://github.com/Inmata/leaf-translate/releases/latest"><img src="https://img.shields.io/github/v/release/Inmata/leaf-translate?style=flat-square&amp;color=54785e" alt="Latest release"></a>
  <a href="https://github.com/Inmata/leaf-translate/actions/workflows/windows.yml"><img src="https://github.com/Inmata/leaf-translate/actions/workflows/windows.yml/badge.svg?branch=main" alt="Windows build status"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%2F%2011-54785e?style=flat-square" alt="Windows 10 and 11">
</p>

<p align="center"><a href="README.md">简体中文</a> · <strong>English</strong></p>

**A shortcut from unfamiliar text to understanding.**

Leaf is a Windows desktop translator. Copy or select text, press a shortcut, and read the translation in a small floating window. Click a word for a contextual explanation, then ask follow-up questions without leaving the original passage.

Bring your own **OpenAI, Zhipu, Qwen, DeepSeek, or another compatible API**. The model detects the source language, while you choose the target language and learning preferences. Use it with books, websites, technical documents, or text copied from a game.

## Features

- **Translate with a shortcut.** Select text and press `Ctrl+Alt+T`, or enable clipboard mode to copy first.
- **Learn words in context.** Click a word for its meaning, part of speech, and base form. Expand synonyms, word formation, collocations, or examples according to your preferences.
- **Continue the conversation.** Ask about the passage or a selected word in the same window, with the original text and translation still visible.
- **Choose how you learn.** Select a reading context and learning options. Add a custom option when needed, and save frequent combinations as presets.
- **Return to local history.** Search originals or translations, reopen a record with its cards and conversation, and keep asking. The default limit is 200 records; saving can be disabled.
- **Fit your desktop.** Opening the popup keeps focus in your current app. Pin, move, or resize it; the last position and size are remembered. The global shortcut supports `Alt+Space`.

## Download and setup

Open [the latest release](https://github.com/Inmata/leaf-translate/releases/latest) and download **`Leaf-vVERSION-windows.zip`**. GitHub's automatically generated `Source code` archives contain source files rather than the runnable app.

1. **Extract the entire ZIP** into a folder you intend to keep.
2. Keep `Leaf.exe` and `Leaf.exe.config` together, then launch `Leaf.exe`.
3. The settings window opens on first launch. Configure an API to begin translating.

| Item | Requirement or behavior |
| --- | --- |
| Operating system | Windows 10 / 11 |
| Runtime | [.NET Framework 4.8](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48); usually included with recent Windows versions |
| Interface language | The current app UI is Chinese; this documentation is available in English |
| Translation languages | Source language detected automatically; target defaults to Chinese and is editable |
| API access | Your own API key and an available model; usage is billed by the provider |

Leaf is portable and runs in the system tray. Starting with Windows is optional and disabled by default.

## Quick start

### 1. Connect a translation provider

Choose an **API service (API 服务)**, then enter its **API Key**. Official presets supply the endpoint; choose **Custom compatible service (自定义兼容服务)** for another provider and enter its URL. Once the endpoint is known, Leaf automatically fetches models with loading, success, or failure feedback. Select a text-chat model or use **Refresh models (刷新模型)** to retry. Saving remembers the endpoint for future key changes.

Keys do not reliably identify their service, so Leaf never probes multiple providers with the same key. Model ownership is shown when supplied by the catalog; otherwise it is not guessed. Leaf does not automatically choose a paid model.

| Provider | Official documentation |
| --- | --- |
| OpenAI | [OpenAI API documentation](https://developers.openai.com/api/docs/) |
| Zhipu (智谱) | [Zhipu documentation](https://docs.bigmodel.cn/) |
| Qwen (千问) | [Alibaba Cloud Model Studio](https://www.alibabacloud.com/help/en/model-studio/) |
| DeepSeek | [DeepSeek API documentation](https://api-docs.deepseek.com/) |
| Other compatible services | Use the provider's HTTPS endpoint; OpenAI Chat Completions compatibility is required |

The model field also accepts a manually entered ID. Some services do not expose a model catalog; use their documentation in that case. Default endpoints are provided and can be edited. A listed model may still need activation or quota in your account, so test the connection.

Click **Test connection (测试连接)**, then **Apply service configuration (应用服务配置)** after it succeeds. Testing sends one short request and may count toward API usage. Model availability, quotas, and pricing depend on your provider account.

**Advanced options (高级选项)** are collapsed by default. Keep thinking mode on **Auto (自动适配)** for normal use. GLM-5.3 / Flash require thinking and default to light effort, with higher effort available. Leave the output token cap blank for automatic limits. It includes thinking and visible output; a small cap can truncate a response, and actual usage is billed by the provider.

OpenAI and custom endpoints use the model's default reasoning settings without Zhipu or Qwen-specific parameters. Catalogs can include image and audio models; choose a text-chat model. Leaf currently uses Chat Completions, so services offering only Responses or other native protocols are outside this version's compatibility.

To explore the interface before supplying a key, open PowerShell in the extracted folder and run `.\Leaf.exe --demo`. The demonstration is labeled and makes no API calls.

### 2. Translate some text

**New settings default to selection mode:**

1. Select text in a browser, reader, or another application.
2. Press **`Ctrl+Alt+T`** and wait for the translation.

If you prefer copying first, enable **Clipboard mode (剪贴板模式)** in the tray or **Read clipboard with shortcut (快捷键读取剪贴板)** in settings. Copy with `Ctrl+C`, then press the translation shortcut. Some applications do not expose selected text; copying manually is more reliable there. Upgrades preserve your saved capture mode.

Clipboard text is read when you invoke the shortcut. Copying something by itself does not trigger translation.

### 3. Explore words and ask questions

Click a word in the original text to open a word card. For example, when reading `prowess`, the card can explain its meaning in that sentence and compare it with `power` if you enable the synonym option.

For a phrase or text that is awkward to select word by word, drag across the original text and choose **Explain selected passage (解释选中片段)** from its context menu.

Click **Ask (追问)** to reveal the input. Questions concern the selected word or passage; **Back to sentence (返回整句)** switches back to the passage. Press `Enter` to send or `Shift+Enter` for a new line.

### 4. Type or edit your own text

The shortcut immediately opens an editable popup when no text is captured. With existing text, click the pencil beside the source, or enable **Direct input on shortcut (快捷键唤起后直接输入)** to focus and select it automatically. `Enter` or **Translate ↵ (翻译 ↵)** submits a new conversation; `Shift+Enter` inserts a new line. Drag to select translated text, then use `Ctrl+C` or the copy context menu. There is no separate copy-translation button.

[![Manual source editing and translation](docs/images/manual-input.png)](docs/images/manual-input.png)

## Screenshots

These views are rendered from the v0.4.0 windows. Text, model choices, conversations, and history are demonstration content; no live API was called. The app UI is currently Chinese. Click an image to view it at full size.

<table>
  <tr>
    <td valign="top" width="50%">
      <strong>Translation and word cards</strong><br>
      Keep the original passage visible, explore a word, and highlight a matching translation phrase when available.<br><br>
      <a href="docs/images/popup.png"><img src="docs/images/popup.png" alt="Leaf translation popup with a contextual word card and synonym comparison" width="380"></a>
    </td>
    <td valign="top" width="50%">
      <strong>Follow-up questions</strong><br>
      Continue discussing the same passage, review the conversation, and reveal the input when needed.<br><br>
      <a href="docs/images/follow-up.png"><img src="docs/images/follow-up.png" alt="Follow-up conversation and question input in the Leaf popup" width="380"></a>
    </td>
  </tr>
  <tr>
    <td valign="top">
      <strong>API and reading context</strong><br>
      Choose a provider, model, context, and target language, with details specific to the selected context.<br><br>
      <a href="docs/images/settings.png"><img src="docs/images/settings.png" alt="Provider, API key, book context, and target language settings" width="380"></a>
    </td>
    <td valign="top">
      <strong>Learning options and presets</strong><br>
      Select what to learn, add a custom option, and save combinations for later.<br><br>
      <a href="docs/images/learning.png"><img src="docs/images/learning.png" alt="Learning preferences, custom options, and preset management" width="380"></a>
    </td>
  </tr>
  <tr>
    <td valign="top">
      <strong>Desktop preferences</strong><br>
      Set clipboard behavior, the shortcut, startup, and history retention.<br><br>
      <a href="docs/images/behavior.png"><img src="docs/images/behavior.png" alt="Clipboard, shortcut, startup, and history preferences" width="380"></a>
    </td>
    <td valign="top">
      <strong>Local history</strong><br>
      Search source text or translations, reopen a record, and resume learning.<br><br>
      <a href="docs/images/history.png"><img src="docs/images/history.png" alt="Searchable local translation history with reopen and delete controls" width="380"></a>
    </td>
  </tr>
  <tr>
    <td valign="top">
      <strong>Advanced model options</strong><br>
      Automatic defaults, with thinking effort and output limits available when needed.<br><br>
      <a href="docs/images/advanced.png"><img src="docs/images/advanced.png" alt="GLM-5.3 Flash automatic thinking, light effort, and output cap" width="380"></a>
    </td>
    <td valign="top">
      <strong>A compact popup</strong><br>
      Smaller text and spacing, full-size buttons, and scrollable longer content.<br><br>
      <a href="docs/images/compact.png"><img src="docs/images/compact.png" alt="A smaller Leaf popup showing source text, translation, and a word card" width="300"></a>
    </td>
  </tr>
</table>

## Make it work for your learning

Contexts include **General (通用), Books (书籍), Film and TV (影视), Technical documents (技术文档), Games (游戏), and Custom (自定义)**. A context is independent of language: a book might be French and a technical document German. Additional fields appear for the relevant context, such as a book title or game name.

Word cards follow the options you select:

| Learning option | What it covers |
| --- | --- |
| Base form and inflection (原形与词形) | Base form, part of speech, and how the current form relates to it |
| Synonym comparison (近义词比较) | Differences in meaning and use between easily confused words |
| Word formation and roots (构词与词根) | Supported word formation explanations; etymology distinguished from memory associations |
| Collocations (常见搭配) | Common combinations appropriate to the context |
| Short examples (短例句) | An example related to the context, with a translation |

If something is missing, expand **Add a learning preference (添加自己的学习偏好)** and supply a name and a short instruction. For example:

> **Name:** Common mistakes
>
> **Instruction:** Explain a commonly confused usage and give one short example.

The new option becomes selectable alongside the built-in options. **Preference presets (偏好预设)** save the target language, context, and learning options together. Reading preferences and desktop habits apply automatically with feedback. Each context remembers its own title or detail.

## Shortcuts and window behavior

| Action | Result |
| --- | --- |
| `Ctrl+Alt+T` | Translate the selection or clipboard; configurable in settings |
| Click a word in the original | Open its contextual word card |
| `Enter` / `Shift+Enter` in the question input | Send / insert a new line |
| Click outside an unpinned popup | Hide it while keeping the conversation and draft |
| Click the pin button | Keep it visible; asking a question does not pin automatically |
| Click the hide button | Hide the popup while keeping Leaf in the tray |
| Tray menu → Exit (退出) | Close the application |

The first popup appears on the right of the primary display. Move and resize it once to choose where it belongs; subsequent shortcuts and application restarts restore that position and size. Retry keeps a visible window in place. Drag it to another monitor directly; if that display is removed, Leaf keeps the window inside the available work area.

Smaller windows use slightly smaller body text, line spacing, and padding, with readable minimum sizes. Buttons keep their size; long content remains scrollable. Click the shortcut field and press a combination to record it. `Esc` cancels; accepted combinations apply automatically.

The visible popup has a taskbar entry; hiding it keeps the tray icon. You can set `Alt+Space` in settings. While Leaf runs, that combination translates instead of opening the Windows system menu. If another shortcut utility uses the same combination, change it in one of the apps.

Opening preserves focus by default. Enable **Direct input on shortcut (快捷键唤起后直接输入)** to focus and select the source automatically. The gear beside the pin opens settings; **History (历史记录)** is available from the tray menu. Check the hidden-icons area if you cannot find Leaf.

## Data and privacy

**API keys are stored in Windows Credential Manager.** Settings and history live in `%LOCALAPPDATA%\LeafTranslate`; keys are not written to history files.

Translation, word lookup, follow-up questions, and connection testing send requests to your configured API endpoint. Translation includes the source text and context; word lookup and questions also include relevant translations, learning preferences, or recent conversation. Fetching models queries that service's catalog without sending reading content. Leaf has no project-operated relay server.

History stays on your computer. Opening it makes no API request. The default limit is 200 records; you can change retention or disable saving. **Disabling saving clears existing history.** Deleting records or clearing history also removes the associated cache.

Local diagnostic logs are in `%LOCALAPPDATA%\LeafTranslate\logs`, accessible through **Open logs (打开日志)** in the tray. They contain request timing, provider/model, HTTP status and error codes, with no API keys, source text, translations, or questions. Logs rotate automatically across at most five files, totaling about 5 MiB, and are never uploaded automatically.

## Frequently asked questions

### Why does reading a selection fail?

Some readers, custom interfaces, or elevated applications do not expose selected text. Use clipboard mode and copy manually before invoking the shortcut. Text contained only in an image needs recognition by another tool; Leaf currently has no OCR.

### Why does the shortcut do nothing?

Check that Leaf is running in the tray. If the shortcut is already in use, choose a different combination containing `Ctrl`, `Alt`, or `Win` in settings.

### Why does my API request fail after entering a key?

Check the provider, model, endpoint, and account quota. Leaf displays errors for network failures, timeouts, invalid keys, rate limits, and malformed responses. Apply service configuration after a successful connection test. A masked saved-key placeholder is normal: stored keys are not filled back into the input, and leaving it blank keeps the saved key. Changing the endpoint requires entering the key again. Applying keeps settings open; changes to a previously configured model apply automatically.

### Can I use another language or compatible service?

You can change the target language; the model detects the source language. Model names and HTTPS endpoints are editable as well. Other compatible services may differ in supported parameters and require your own verification. The current application UI remains Chinese.

### Why is Leaf still running after I hide the popup?

Hiding keeps the current conversation ready for the next lookup. Use **Exit (退出)** in the tray menu to stop the application. Starting with Windows is optional and off by default.

### What are the current limitations?

This version is Windows-only and does not include OCR, screenshot translation, or exclusive-fullscreen game support. Word clicking depends on text segmentation; selecting a passage provides another way to look up text. Selection capture and multi-monitor behavior can vary across desktop environments. LLM translations, definitions, and etymology may be inaccurate; check important details.

## Documentation and feedback

- [Measured resource usage](docs/PERFORMANCE-v0.4.0.md)

- [English user guide](docs/USAGE.en.md)
- [中文使用说明](docs/USAGE.md)
- [Development and contribution guide](CONTRIBUTING.md)
- [Report a problem or suggest a feature](https://github.com/Inmata/leaf-translate/issues)

For a bug report, include the app version, Windows version, capture mode, and steps to reproduce it. You may attach `leaf.log` covering the relevant time from **Open logs (打开日志)**. Review it first; do not include credentials or private history.
