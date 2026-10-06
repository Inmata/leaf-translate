# Leaf v0.5.0

这次更新围绕可靠取词、流畅阅读和更自然的学习体验，整合了自 0.4.1 以来的改进。

## 重点变化

- **Codeg / WebView2 取词兼容**：允许读取经当前窗口归属验证的跨进程 WebView 文本，不再因渲染进程 PID 不同而漏掉选区。Codeg 实际选中文字取词已由用户确认可用。
- **长原文拖选与滚动更流畅**：原文改用只读 TextBox 呈现，词语命中及高亮通过文本位置映射保留，避免原先阅读呈现路径的额外开销。长文拖选卡顿已由用户确认解决。
- **学习内容更自然**：按偏好解释当前词形、易混区别、可靠构词、实用搭配与短例句；优先放回原句，避免生硬罗列和无依据的词源推断。
- **学习区连续选择**：标题与正文组成同一可选文档，可连续拖选并复制；译文正确呈现 Markdown 强调，不再露出格式标记。
- **同浮窗设置页**：设置与阅读共用窗口；设置按钮再次点击或「返回」恢复原文、译文、学习内容和追问草稿。改善下拉框、空预设、密钥占位及按钮状态。
- **会话恢复与内部取词**：Leaf 内选中的不同文本直接进入翻译，不模拟复制；可返回根会话。相同原文恢复已有结果，清空未提交草稿后也不重复请求。
- **安静唤起与准确重试**：空选区或暂时无法取得文字时保留当前状态，不自动进入输入；取消与重试只针对对应操作，迟到响应不覆盖当前会话。
- **纸面阅读界面**：调整字号、留白、文本对齐、图标和悬停滚轮行为；内嵌思源黑体 Regular / Bold，无需系统安装字体。

## 正确性与数据安全

改善连续书写语言判断和 Unicode 截断；设置按顺序提交并支持故障恢复，密钥只保存在 Windows Credential Manager 且绑定接口地址。历史按条恢复异常数据，按 UTF-8 字节限制大小，后台保存并在退出时冲刷；删除和关闭保存后，迟到结果不会复活。取词日志区分选区、复制回退和剪贴板模式，仅记录结构化元数据。

发布源码镜像同步新增、修改和删除文件；程序集、manifest 与包版本由同一常量生成。

## 验证

1082 项核心与应用断言、119 项离屏 WPF smoke 检查通过。原生检查通过前 25 项后，因测试窗口未获得系统前台而中止，不宣称原生全套通过；测试凭据已清理。Codeg 实际取词、长文拖选及整体观感由用户实测确认。

## 升级与后续

从托盘退出旧版，完整解压 **Leaf-v0.5.0-windows.zip**，运行 `Leaf.exe` 并保留旁边的 `Leaf.exe.config`。沿用现有设置、历史和凭据，旧凭据按已保存接口迁移。内嵌字体使程序体积增加。程序尚未代码签名。

[模型用量统计](https://github.com/Inmata/leaf-translate/issues/1)、[语音](https://github.com/Inmata/leaf-translate/issues/2)、[设置页进一步优化](https://github.com/Inmata/leaf-translate/issues/3)和[部分缩放下轻微字体锯齿](https://github.com/Inmata/leaf-translate/issues/4)将通过 Issues 跟踪，不包含在本版。不同应用的取词能力仍有差异，不承诺所有自绘或受限窗口均可读取。API 自动检查使用隔离模拟响应，不代表所有真实供应商接口均已验证。

---

# Leaf v0.5.0 — English

This release brings the improvements since 0.4.1 together: more reliable capture, smoother reading, and more natural learning.

- **Codeg / WebView2 capture:** selections may come from a different renderer process when their ownership by the captured window has been verified. The user confirmed real Codeg selection capture works.
- **Smoother long-source selection and scrolling:** a read-only TextBox renders the source, with mapped word hits and highlights preserving learning interactions. The user confirmed the long-text selection slowdown is resolved.
- **Natural explanations:** describe the current word form, practical synonym distinctions, supported word formation, collocations, and short examples according to selected preferences. Use sentence context and avoid unsupported etymology claims.
- **Continuous learning selection:** headings and body text share one selectable document. Translation emphasis renders correctly instead of showing Markdown markers.
- **Settings in the same popup:** toggle settings again or use Return to restore the source, translation, learning content, and follow-up draft. Dropdowns, empty presets, saved-key placeholders, and button states are improved.
- **Internal capture and session restoration:** selected Leaf text is read directly without simulated copying. Different text opens a child conversation with a return to its root; matching text restores existing results without another request.
- **Quiet reopening and focused retries:** empty or unavailable selections preserve the current state without automatically entering editing. Cancellation and retry keep operation identity; late responses cannot replace the current conversation.
- **Clear paper UI:** refined typography, spacing, alignment, icons, and hover scrolling. Source Han Sans SC Regular and Bold are embedded; no font installation is required.

Correctness and storage improvements include continuous-script language handling, Unicode-safe truncation, ordered settings commits and recovery, endpoint-bound Windows Credential Manager keys, per-record history recovery, UTF-8 byte limits, background saves, and exit flushing. Deleted history cannot be resurrected by late results. Diagnostic logs contain metadata only. Source publication mirrors additions, changes, and deletions; one version constant drives assembly, manifest, and package versions.

Validation passed 1,082 core/application assertions and 119 offscreen WPF smoke checks. Native checks passed the first 25 assertions, then stopped because the fixture window could not obtain foreground ownership; the native suite is not reported as fully passed. Test credentials were cleaned up. The user confirmed real Codeg capture, long-text selection performance, and overall appearance.

Exit the old app through the tray, fully extract **Leaf-v0.5.0-windows.zip**, and keep `Leaf.exe.config` beside `Leaf.exe`. Existing settings, history, and credentials are retained; legacy keys migrate against their saved endpoint. Embedded fonts increase the app size. The app is not code-signed.

[Usage statistics](https://github.com/Inmata/leaf-translate/issues/1), [voice features](https://github.com/Inmata/leaf-translate/issues/2), [settings layout](https://github.com/Inmata/leaf-translate/issues/3), and [slight font jaggedness](https://github.com/Inmata/leaf-translate/issues/4) are tracked in Issues, outside this release. Capture support varies by application. Automated API scenarios use isolated fixtures and do not establish compatibility with every live provider.
