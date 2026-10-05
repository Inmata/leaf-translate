# Leaf v0.3.0

Leaf now supports a broader API setup flow, clearer diagnostics, and a more comfortable compact popup.

## 中文

- 名称统一为 **Leaf**；已有设置、Windows 凭据和历史继续沿用。
- **先填 API Key，再确定接口**：新增 OpenAI 官方预设及自定义兼容服务，自动获取模型并反馈状态，显示接口提供的模型归属。不按密钥猜厂商，不把同一把 Key 发给多家试探。
- 修复 **GLM-5.3 / 5.3-Flash** 因强制关闭思考而报错。默认轻量思考，提供足够的输出余量；高级选项可调整受支持模型的思考模式、GLM 思考强度及输出上限。
- 快捷键改为**直接按键录入**，支持 Alt+Space；Esc 取消，录入时暂时释放当前热键，结束或关闭设置后恢复。
- 新用户的**剪贴板模式默认关闭**；升级保留已有选择。
- 缩小浮窗时适度缩小正文、行距和留白，保留可读下限和按钮尺寸，长内容可滚动。
- 增加**本地诊断日志**和托盘「打开日志」入口，记录请求耗时、模型、HTTP 状态、错误码和异常位置；最多约 5 MiB 自动轮换，不记录 Key、原文、译文或追问，不自动上传。
- 双语 README 更新品牌横幅、下载入口、版本/构建徽章和八个界面预览；用户指南同步更新。

## English

- The application is now named **Leaf**. Existing settings, Windows credentials, and history are retained.
- Start with an API key, then specify its service or endpoint. OpenAI and custom Chat Completions-compatible services join the existing presets. Model discovery shows progress and catalog-provided ownership without guessing providers or probing them with your key.
- GLM-5.3 and 5.3-Flash now use supported light reasoning instead of an invalid disabled-thinking parameter. Advanced options expose supported thinking settings and output limits.
- Record shortcut combinations directly. Alt+Space remains supported; Escape cancels, and the live shortcut is released during recording and restored afterward.
- Clipboard mode is off for new settings; upgrades retain saved preferences.
- Compact windows reduce body typography and spacing within readable bounds while preserving button sizes and scrolling.
- Local rotating diagnostics are available through the tray's Open logs command. Logs contain request metadata and error locations, excluding keys and reading/conversation content. Nothing is uploaded automatically.
- Both READMEs now have a brand banner, download links, real release/build badges, and eight application views.

## Validation

- 138 core/application assertions, 28 embedded WPF rendering checks, and 17 Windows native assertions passed.
- Live short requests to GLM-5.3 and GLM-5.3-Flash confirmed that the old disabled-thinking parameters return HTTP 400/code 1210; the corrected ordinary and streaming requests succeeded for both.
- OpenAI and custom endpoint discovery/request parameters were validated with isolated fixtures and official API documentation, without live OpenAI requests.
- Checked bilingual links, anchors, banner, PNG previews, public-source scope, and portable ZIP contents/checksum.

Provider references: [GLM-5.3](https://docs.bigmodel.cn/cn/guide/models/text/glm-5.3), [GLM-5.3-Flash](https://docs.bigmodel.cn/cn/guide/models/vlm/glm-5.3-flash), [OpenAI Chat Completions](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create), [OpenAI model catalog](https://developers.openai.com/api/reference/resources/models/methods/list).

## Upgrade

Exit the old application from the tray, extract **Leaf-v0.3.0-windows.zip** completely, and launch `Leaf.exe` with `Leaf.exe.config` beside it. Data remains in `%LOCALAPPDATA%\LeafTranslate`. Keep the old folder until the new version is running. No automatic updater is included.

退出托盘中的旧程序，完整解压 **Leaf-v0.3.0-windows.zip**，启动 `Leaf.exe`；保持配置文件与程序在同一目录。设置、密钥和历史保留。此次包含 API 配置和窗口交互变化，因此使用 **0.3.0**。
