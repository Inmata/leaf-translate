# Leaf / 叶译 v0.2.0

修复快捷键、服务商配置和任务栏显示，改进浮窗记忆与模型选择。

- **Alt+Space**：支持 Alt+空格，包括大小写及空格输入；拦截 Windows 窗口菜单，长按只触发一次，退出后解除。
- **API 密钥与服务商**：保存当前选中的服务商，重新打开设置和翻译时使用同一家服务。密钥存入 Windows 凭据管理器后读回验证；已有密钥输入框留空，下方显示已保存状态。
- **任务栏图标**：浮窗可见时提供任务栏入口，托盘、窗口和程序使用统一的多尺寸叶子图标。
- **浮窗位置与尺寸**：移除显示器选择，首次在主屏右侧显示。记住最后的位置和尺寸，重新唤起与重启后恢复；重试保持原位。旧版拖动位置自动迁移，显示器移除或 DPI、分辨率改变时避免窗口落在屏幕外。
- **模型选择**：新设置不预填固定模型。点击「获取模型」查询当前服务的目录，再明确选择；支持 DeepSeek/兼容接口与千问官方文本目录分页。不支持列表或获取失败时可直接填写 ID，不自动选择模型。旧版官方 DeepSeek 默认 `deepseek-chat` 迁移为当前的 `deepseek-flash`，自定义模型保留。
- **文档与安装包**：同步中英文说明和六个窗口预览，便携包同时包含中文、英文使用指南。

升级时先从托盘退出旧版，再完整解压 ZIP，运行其中的 `Leaf.exe`。设置、历史和凭据仍使用原有本机存储，无需搬移。若此前选择了其他服务商但没有正确保存，请重新选中对应服务并保存；已经写入该服务的密钥会显示已保存。

模型是否已开通、额度和费用仍取决于服务商账号。「获取模型」失败不影响手动配置。可参考 [DeepSeek 模型目录接口](https://api-docs.deepseek.com/api/list-models/) 和 [千问模型目录接口](https://help.aliyun.com/zh/model-studio/list-models)。

验证：93 项核心与应用检查、19 项 WPF 窗口检查、14 项本机 Windows 集成检查通过。集成检查仅使用独立假凭据、模拟网络响应及按键事件，不注入真实桌面按键；未使用真实服务商账号验证 API。

## English

This release fixes shortcut and provider persistence issues and improves desktop behavior.

- Supports Alt+Space without opening the Windows system menu; one action per press.
- Saves the selected provider alongside its credential. Keys remain in Windows Credential Manager, with write/read verification and a clear saved-state hint.
- Adds a taskbar entry for the visible popup and a shared application icon.
- Removes the monitor selector. Remembers the last popup position and size across shortcuts and restarts; retry stays in place. Existing positions migrate, and display changes keep the window accessible.
- Replaces fixed model presets with authenticated catalog fetching and editable IDs. Supports compatible model lists and paginated Bailian text models; manual configuration remains available. No automatic model selection. The old official DeepSeek default is migrated; custom models are preserved.
- Updates both languages of documentation and includes both user guides in the portable ZIP.

Exit the previous version through its tray menu, extract the complete ZIP, and launch `Leaf.exe`. Existing local settings, history, and stored keys remain available. If the previous version failed to save your provider selection, select it again and save.

Validation passed: 93 core/application assertions, 19 WPF checks, and 14 local Windows integration assertions. Integration checks use isolated fake credentials, mocked requests, and simulated key events; they do not inject desktop input or establish live provider-account compatibility.
