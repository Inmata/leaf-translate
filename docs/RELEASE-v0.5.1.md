# Leaf v0.5.1

本版集中修复学习区收起恢复和窗口缩放期间的更新问题。

- **收起后可以再次展开学习**：恢复“展开学习”入口，再次展开复用已有词卡缓存，不重复请求。
- **保留学习文档**：调整缩放期间的更新，避免不必要地重建学习文档，保留文本选区。
- **拖动缩放时暂停高亮计算**：拖动窗口边缘期间暂时隐藏原文词语高亮，冻结字号和间距，文本仍随宽度换行，学习区保持展开。松手后应用最终排版并恢复当前高亮。
- **完整清理中断状态**：拖动中收起后，再次打开按最终尺寸恢复排版；退出后阻止迟到回调重新计算高亮。

## 验证与限制

修复代码通过构建、385 项应用断言和119 项现有离屏 WPF smoke 检查；发布前另行检查文档、公开源码和安装包完整性。收起后再次展开已由用户确认正常。拖动缩放的实际流畅度，尤其是125%与225%缩放及跨屏表现，仍需桌面实测；离屏检查不代表性能已验收。

人性化学习讲解规则本版没有修改。0.5.0 已有功能、设置、历史和 Windows Credential Manager 凭据继续沿用，无需数据迁移。

## 升级

从托盘退出旧版，完整解压 **Leaf-v0.5.1-windows.zip**，运行 `Leaf.exe`，保留旁边的 `Leaf.exe.config`。程序尚未代码签名。既有后续需求继续由 Issues 跟踪。

---

# Leaf v0.5.1 — English

This patch focuses on restoring collapsed learning content and reducing unnecessary updates during window resizing.

- **Expand learning again after collapsing:** the Expand learning entry returns; reopening reuses the cached card without another request.
- **Preserve the learning document:** resize updates avoid unnecessary document reconstruction and preserve text selection.
- **Pause highlights during interactive resizing:** while dragging a window edge, source-word highlights are temporarily hidden and typography and spacing stay fixed. Text continues to wrap with the width and learning remains expanded. Releasing the edge applies the final typography and restores the current highlight.
- **Clean up interrupted resizing:** reopening after hiding mid-drag applies the final size; shutdown prevents late callbacks from restarting highlight calculations.

## Validation and limitations

The fixes passed the build, 385 application assertions and 119 existing offscreen WPF smoke checks. Documentation, public-source and package-integrity checks are performed before publication. The user confirmed collapse and re-expansion work. Actual resize smoothness, particularly at 125% and 225% scaling and across monitors, still requires desktop testing; offscreen checks do not establish performance acceptance.

Natural learning explanation rules are unchanged in this release. Existing 0.5.0 features, settings, history and Windows Credential Manager credentials are retained; no data migration is required.

## Upgrade

Exit the old version from the tray, fully extract **Leaf-v0.5.1-windows.zip**, and run `Leaf.exe` with `Leaf.exe.config` beside it. The executable is not code-signed. Existing follow-up requests remain tracked in Issues.
