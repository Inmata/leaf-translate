# Leaf v0.5.2

本版聚焦设置页的语言与字体，并让原文与译文可以直接交换。

- **目标语言改为选择**：下拉提供中文、繁體中文、English、日本語、한국어、Français、Deutsch、Español、Português、Русский、Italiano、العربية、हिन्दी 与「自定义」。只有选「自定义」才在下一行显示对齐的输入框，其他语言时该行折叠；输入留空不保存，保留已应用的语言；旧版本保存的自由文本语言按自定义原样保留。设置中依然没有源语言控件：原文语言只在交换时按需识别。
- **字体可选并立即统一**：默认使用内置思源黑体 SC。可选已安装的文本字体；选择后浮窗、设置页、历史窗口以及已经建立的阅读文档（译文与学习词卡）立即一起更新，图标仍使用 Segoe MDL2 Assets。所选字体在机器上不可用时回退到内置字体，并在设置状态处给出一句轻量提示。
- **原文与译文可以直接交换**：两段正文之间的浅灰小图标与两侧短文线就是交换入口，只有两段正文都已完成的页面可用，其余情况入口保持不可点。按下后按需识别一次原文语言：这次识别用当前已保存的服务、模型与密钥，不需要额外密钥，也不要求供应商支持特有参数；识别结果作为新的目标语言，沿设置页同一个保存路径写入并留在设置中，写入成功后才对调两段正文，不重新翻译。同一对正文再次按下即可换回，识别结果与被替换掉的目标语言一并记住，既不重复识别，也不重复翻译。没有识别出语言、原文已经是当前目标语言、识别失败或目标语言保存失败时，页面、会话与已保存的目标语言都保持原样，只用一行短提示说明原因，重试只重复这次识别与保存。交换出来的反向视图不会被保存：这一对正文在历史里仍是一条记录，原有的原文与译文不变。
- **版本号落在设置末尾**：设置内容底部显示「Leaf 0.5.2」，位于最后一组之后、固定状态行之前，滚动到底留有空白。

## 验证与限制

本版通过构建与 1337 项测试断言，比语言与字体改动完成时的 1274 项新增 63 项，覆盖交换入口的可用条件、按需识别一次、换向与再换回、拒绝与失败保留页面、页面隐藏后到达的迟到回答，以及历史不新增条目。整组测试的守护上限由 30 秒改为 120 秒，这只是一个测试预算而不是产品性能指标（各计时段实测约 30.8、16.8 和 1.1 秒）；交换逻辑没有改动界面标记，因此离屏 WPF smoke 沿用既有 119 项结果。

上面这些数字对应交换实现完成时的集成快照；其后为新开历史窗口继续调整字体的修改不在该次运行内，发布前需要重新整组验证。交换用的是当前已保存的服务与模型，自动化只用本地兼容接口联调：真实供应商下的识别与交换手感、字体切换后的实际观感、缺失字体回退提示的呈现，以及交换入口的外观仍需桌面人工验收。原有会话、草稿、选区、词卡缓存与窗口尺寸和位置均未改动，无需数据迁移。

## 升级

从托盘退出旧版，完整解压 **Leaf-v0.5.2-windows.zip**，运行 `Leaf.exe`，保留旁边的 `Leaf.exe.config`。程序尚未代码签名。既有后续需求继续由 Issues 跟踪。

---

# Leaf v0.5.2 — English

This release focuses on language and typeface in settings, and lets the original and the translation be swapped directly.

- **Target language is now a picker:** the dropdown offers 中文, 繁體中文, English, 日本語, 한국어, Français, Deutsch, Español, Português, Русский, Italiano, العربية, हिन्दी and 自定义 (custom). Only 自定义 reveals an aligned input on the next line; every other language collapses that row. An empty input is not saved and the applied language is kept, and a free-form language saved by an older version is preserved as a custom value. There is still no source-language control anywhere: the original's language is identified on demand, when the swap asks for it.
- **Selectable typeface applied at once:** the embedded Source Han Sans SC is the default. Any installed text font may be chosen; the popup, the settings page, the history window and already-built reading documents (translation and learning card) all switch together immediately, while icons stay Segoe MDL2 Assets. A chosen font that is not available on the machine falls back to the embedded face with one light notice in the settings status.
- **The original and the translation can be swapped directly:** the quiet seam between the two bodies — a small pale icon with two short lines — is the swap entry, offered only where both bodies are complete and left inert everywhere else. Pressing it identifies the original's language once, through the service, model and key already saved: no extra key is needed, and no provider-specific parameter is required of the endpoint. What was identified becomes the new target language, written through the same save path the settings page uses and kept in settings, and only once that write succeeds are the two bodies exchanged, with no second translation. Pressing it again on the same pair flips back, remembering both the identified language and the target language it replaced, so neither an identification nor a translation is repeated. When no language can be identified, when the original is already in the current target language, or when the identification or the settings write fails, the page, the conversation and the saved target language all stay as they were, one short line says why, and a retry repeats only that identification and that write. The reversed view is never saved: the pair keeps its one history entry, with its own original and translation unchanged.
- **Version footnote at the end of settings:** the content ends with "Leaf 0.5.2", after the last group and before the fixed status line, with resting space when scrolled to the bottom.

## Validation and limitations

The release passed the build and 1337 test assertions, 63 more than the 1274 recorded when the language and typeface work was complete, covering when the swap entry is offered, the single on-demand identification, flipping back and forward again, refusals and failures that leave the page alone, an identification arriving after the page was hidden, and the unchanged history. The whole-group test guard was raised from 30 to 120 seconds — a test budget, not a product performance target, with the timed segments measuring about 30.8, 16.8 and 1.1 seconds. The swap logic changed no UI markers, so the 119 existing offscreen WPF smoke checks are carried over.

Those numbers belong to the integration snapshot recorded when the swap implementation was complete; later work on the typeface in a newly opened history window is not part of that run, and the whole group has to be verified again before release. The swap uses the service and model already saved, and the automated scenarios run against a local compatible endpoint only: how identification and swapping feel with a live provider, how a switched font really looks, the presentation of the missing-font fallback notice, and the appearance of the swap entry still require manual desktop acceptance. Sessions, drafts, selections, word-card caching, and window size and position are unchanged, and no data migration is required.

## Upgrade

Exit the old version from the tray, fully extract **Leaf-v0.5.2-windows.zip**, and run `Leaf.exe` with `Leaf.exe.config` beside it. The executable is not code-signed. Existing follow-up requests remain tracked in Issues.
