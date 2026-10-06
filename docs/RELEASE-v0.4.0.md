# Leaf v0.4.0

Leaf now supports typing and editing source text, selectable translations, and settings that stay open while you work.

## 中文

- 服务设置顺序改为 **服务 → API Key → 模型**。每个服务恢复自己的模型和密钥状态；已保存密钥用独立占位文字表示，点击可替换，真实密钥不回填。
- 已配置服务的模型切换自动应用；阅读场景、语言、学习项、预设、快捷键与使用习惯自动保存并反馈。新的密钥、接口与高级参数通过「应用服务配置」提交，应用后窗口保持打开。关闭会提交待应用的普通偏好。
- 书名、影视名、游戏名等分别记住，切换场景与重启后恢复；保留旧设置中的当前名称。
- 原文可编辑，**Enter /「翻译 ↵」** 创建新会话，Shift+Enter 换行，Esc 退出编辑。默认保留原软件焦点；可开启「快捷键唤起后直接输入」，取词后聚焦并全选原文。
- 译文可选中，Ctrl+C / 右键复制，Ctrl+A 全选。移除独立复制译文按钮；剪贴板写入与取词放到独立 STA 线程，有限等待与忙碌提示，避免阻塞界面。
- 顶栏加入设置图标；追问放在左下角，展开后可以收起。缩窄字号差距，使用细滚动条和统一留白；只读文档关闭撤销历史，避免保留无用的流式编辑记录。
- 快捷键先显示窗口，再异步取得文字；没文字时可直接输入，不使用旧剪贴板代替失败选区。恢复系统最小化、隐藏或 DWM 遮蔽后的浮窗，不仅依赖 WPF 可见状态。
- 双语使用说明与九张界面截图更新；新增可重复运行的独立进程性能测量和[资源占用报告](https://github.com/Inmata/leaf-translate/blob/main/docs/PERFORMANCE-v0.4.0.md)。

## English

- API settings now follow **service → key → model**. Service changes restore the corresponding model and saved-key state. A masked placeholder represents a stored key without inserting the real key into the input.
- Models on configured services, reading preferences, and desktop habits apply automatically with feedback. New keys, endpoints, and advanced parameters use **Apply service configuration**, keeping settings open. Each reading context remembers its own title or detail.
- Edit source text and submit with **Enter / Translate ↵** to start a new conversation. Shift+Enter inserts a line; Escape exits editing. Opening preserves focus by default; the optional direct-input preference focuses and selects the source after capture.
- Select translated text and copy with Ctrl+C or its context menu; Ctrl+A selects all. The standalone copy button is removed. Clipboard work runs on independent STA threads with bounded UI waits and visible contention errors.
- A settings icon joins the popup header. Ask moves to the lower left and its input can be collapsed. Typography, slim scrollbars, and spacing are more consistent; read-only documents retain no undo history.
- Shortcut invocation reveals the popup before asynchronous capture. Empty input is editable immediately. Native restoration handles minimized, hidden, or cloaked windows rather than relying only on WPF visibility.
- Updated bilingual guides and nine screenshots, with an isolated process benchmark and [resource report](https://github.com/Inmata/leaf-translate/blob/main/docs/PERFORMANCE-v0.4.0.md).

## Validation

- 145 core/application assertions, 32 embedded WPF checks, and 23 Windows native assertions passed: **200 total**.
- Native checks cover credential persistence and placeholder safety, model auto-apply, Alt+Space, non-activating restoration after OS minimization/hiding, explicit input focus, clipboard contention without UI blocking, and remembered geometry.
- Native window states were exercised directly; this does not establish every Explorer Show Desktop transition, third-party shortcut utility, elevated application, or desktop-selection implementation. There were no synthetic physical desktop keystrokes.
- Network requests use isolated local fixtures for this release. Provider request parameters from v0.3.0 remain unchanged; no new live provider calls were made for v0.4.0.
- Performance measurements use a native tray/WPF process, isolated data, Windows counters, and a delayed local SSE response. They exclude real API latency and do not prove long-term absence of memory leaks.

## Upgrade / 升级

Exit the old app through the tray, fully extract **Leaf-v0.4.0-windows.zip**, and launch `Leaf.exe`. Keep `Leaf.exe.config` alongside it. Existing keys, settings, and history are retained. Direct input remains off until enabled.

从托盘退出旧版，完整解压新版，再启动 `Leaf.exe`。设置、凭据与历史沿用原路径，无需重填；直接输入选项默认关闭。
