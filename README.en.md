# Leaf · 叶译

[简体中文](README.md) | **English**

**A shortcut from unfamiliar text to understanding.**

Leaf is a Windows desktop translator. Copy or select text, press a shortcut, and read the translation in a small floating window. Click a word for a contextual explanation, then ask follow-up questions without leaving the original passage.

Bring your own **Zhipu, Qwen, or DeepSeek API key**. The model detects the source language, while you choose the target language and learning preferences. Use it with books, websites, technical documents, or text copied from a game.

[Download for Windows](https://github.com/Inmata/leaf-translate/releases/latest) · [Quick start](#quick-start) · [Screenshots](#screenshots) · [Report an issue](https://github.com/Inmata/leaf-translate/issues)

## Features

- **Translate with a shortcut.** Copy text and press `Ctrl+Alt+T`, or switch to reading the current selection directly.
- **Learn words in context.** Click a word for its meaning, part of speech, and base form. Expand synonyms, word formation, collocations, or examples according to your preferences.
- **Continue the conversation.** Ask about the passage or a selected word in the same window, with the original text and translation still visible.
- **Choose how you learn.** Select a reading context and learning options. Add a custom option when needed, and save frequent combinations as presets.
- **Return to local history.** Search originals or translations, reopen a record with its cards and conversation, and keep asking. The default limit is 200 records; saving can be disabled.
- **Fit your desktop.** Opening the popup keeps focus in your current app. Pin, move, resize, choose a monitor, or change the global shortcut.

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

In **Settings (设置)**, choose a provider and enter your **API key (API 密钥)**. Default model names and endpoints are provided; you can edit them if needed.

| Provider | Default model | Official documentation |
| --- | --- | --- |
| Zhipu (智谱) | `glm-4.7-flash` | [Zhipu documentation](https://docs.bigmodel.cn/) |
| Qwen (千问) | `qwen-flash` | [Alibaba Cloud Model Studio](https://www.alibabacloud.com/help/en/model-studio/) |
| DeepSeek | `deepseek-chat` | [DeepSeek API documentation](https://api-docs.deepseek.com/) |

Click **Test connection (测试连接)**, then **Save settings (保存设置)** after it succeeds. Testing sends one short request and may count toward API usage. Model availability, quotas, and pricing depend on your provider account.

To explore the interface before supplying a key, open PowerShell in the extracted folder and run `.\Leaf.exe --demo`. The demonstration is labeled and makes no API calls.

### 2. Translate some text

**Clipboard mode is enabled by default:**

1. Select text in a browser, reader, or another application.
2. Copy it with `Ctrl+C`.
3. Press **`Ctrl+Alt+T`** and wait for the translation.

For selection-only use, turn off **Clipboard mode (剪贴板模式)** in the tray menu, or uncheck **Read clipboard with shortcut (快捷键读取剪贴板)** in settings. Some applications do not expose selected text; copying manually is the more reliable option there.

Clipboard text is read when you invoke the shortcut. Copying something by itself does not trigger translation.

### 3. Explore words and ask questions

Click a word in the original text to open a word card. For example, when reading `prowess`, the card can explain its meaning in that sentence and compare it with `power` if you enable the synonym option.

For a phrase or text that is awkward to select word by word, drag across the original text and choose **Explain selected passage (解释选中片段)** from its context menu.

Click **Ask (追问)** to reveal the input. Questions concern the selected word or passage; **Back to sentence (返回整句)** switches back to the passage. Press `Enter` to send or `Shift+Enter` for a new line.

## Screenshots

These views are rendered from the v0.1.0 windows. Text, conversations, and history are demonstration content; no live API was called. The app UI is currently Chinese. Click an image to view it at full size.

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
      Set clipboard behavior, the shortcut, monitor, startup, and history retention.<br><br>
      <a href="docs/images/behavior.png"><img src="docs/images/behavior.png" alt="Clipboard, shortcut, monitor, startup, and history preferences" width="380"></a>
    </td>
    <td valign="top">
      <strong>Local history</strong><br>
      Search source text or translations, reopen a record, and resume learning.<br><br>
      <a href="docs/images/history.png"><img src="docs/images/history.png" alt="Searchable local translation history with reopen and delete controls" width="380"></a>
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

The new option becomes selectable alongside the built-in options. **Preference presets (偏好预设)** save the target language, context, and learning options together. Click **Save settings (保存设置)** after editing.

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

Opening the popup keeps focus in your current application. Click the popup to interact. Settings and **History (历史记录)** are available from the tray menu; check the taskbar's hidden-icons area if you cannot find Leaf.

## Data and privacy

**API keys are stored in Windows Credential Manager.** Settings and history live in `%LOCALAPPDATA%\LeafTranslate`; keys are not written to history files.

Translation, word lookup, follow-up questions, and connection testing send requests to your configured API endpoint. Translation includes the source text and context; word lookup and questions also include relevant translations, learning preferences, or recent conversation. Leaf has no project-operated relay server.

History stays on your computer. Opening it makes no API request. The default limit is 200 records; you can change retention or disable saving. **Disabling saving clears existing history.** Deleting records or clearing history also removes the associated cache.

## Frequently asked questions

### Why does reading a selection fail?

Some readers, custom interfaces, or elevated applications do not expose selected text. Use clipboard mode and copy manually before invoking the shortcut. Text contained only in an image needs recognition by another tool; Leaf currently has no OCR.

### Why does the shortcut do nothing?

Check that Leaf is running in the tray. If the shortcut is already in use, choose a different combination containing `Ctrl`, `Alt`, or `Win` in settings.

### Why does my API request fail after entering a key?

Check the provider, model, endpoint, and account quota. Leaf displays errors for network failures, timeouts, invalid keys, rate limits, and malformed responses. Save settings after a successful connection test. A stored key is not filled back into the input; leaving it blank keeps the saved key. Changing the endpoint requires entering the key again.

### Can I use another language or compatible service?

You can change the target language; the model detects the source language. Model names and HTTPS endpoints are editable as well. Other compatible services may differ in supported parameters and require your own verification. The current application UI remains Chinese.

### Why is Leaf still running after I hide the popup?

Hiding keeps the current conversation ready for the next lookup. Use **Exit (退出)** in the tray menu to stop the application. Starting with Windows is optional and off by default.

### What are the current limitations?

This version is Windows-only and does not include OCR, screenshot translation, or exclusive-fullscreen game support. Word clicking depends on text segmentation; selecting a passage provides another way to look up text. Selection capture and multi-monitor behavior can vary across desktop environments. LLM translations, definitions, and etymology may be inaccurate; check important details.

## Documentation and feedback

- [English user guide](docs/USAGE.en.md)
- [中文使用说明](docs/USAGE.md)
- [Development and contribution guide](CONTRIBUTING.md)
- [Report a problem or suggest a feature](https://github.com/Inmata/leaf-translate/issues)

For a bug report, include the app version, Windows version, capture mode, and steps to reproduce it. Please do not include API keys.
