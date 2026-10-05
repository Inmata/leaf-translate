# Leaf v0.4.1

本次更新将界面统一为「清晰纸面」，改善文字和操作的辨识度。

- 白色背景、深色正文、中性控件与细分隔线；浮窗、设置和历史页使用一致的颜色层级。
- 原文、译文、词卡和追问的字号与间距更统一，追问改为左下角图标文字入口。窗口缩小时继续保持可读下限。
- 主要操作使用深色按钮，顶栏图标更清楚；置顶后显示明确的选中状态。
- 已保存的 API Key 在框内只显示深色实心圆点，已保存状态放在框下。点击或输入时占位让开，留空继续使用。真实密钥不回填，圆点数量与密钥长度无关。
- 双语说明和九张实际 WPF 渲染截图同步更新。

145 项核心与应用检查、32 项 WPF 界面检查和 26 项 Windows 原生检查通过，共 203 项。原生检查验证了圆点占位与真实密钥分离、替换输入隐藏占位、留空保留凭据，以及原有快捷键和焦点行为。API 联调使用隔离的测试响应，未调用真实供应商接口。

从托盘退出旧版，完整解压 **Leaf-v0.4.1-windows.zip** 后运行 `Leaf.exe`，保留旁边的 `Leaf.exe.config`。已有密钥、设置与历史继续使用。

---

This update gives Leaf a consistent **Clear paper** interface with stronger text and control contrast.

- White surfaces, dark reading text, neutral controls, and fine separators across the popup, settings, and history.
- More consistent typography and spacing for the source, translation, word explanations, and follow-up conversation. Compact windows retain readable type sizes.
- Dark primary actions, clearer toolbar icons, and a visible pinned state. Follow-up uses a quiet icon-and-text entry in the lower left.
- A stored API key is represented only by dark, solid password dots inside the field, with its saved status below. The placeholder gives way on focus or typing; leaving the editor empty preserves the existing credential. Real keys are never filled back into the editor, and the mask does not reveal key length.
- Updated bilingual guides and nine screenshots rendered from the WPF application.

Validation passed: 145 core/application assertions, 32 WPF checks, and 26 Windows native assertions (203 total). Native tests cover separation of the dot placeholder from real credentials, hiding the placeholder during replacement, preserving a key when left blank, and existing shortcut/focus behavior. API scenarios use isolated fixtures, without live provider calls.

Exit the old app through the tray, fully extract **Leaf-v0.4.1-windows.zip**, and launch `Leaf.exe` with `Leaf.exe.config` beside it. Existing keys, settings, and history are retained.
