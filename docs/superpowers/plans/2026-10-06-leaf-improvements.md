# Leaf 改进总计划 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将项目审查的缺陷与用户新增的 Codeg 取词、同文本恢复和纸面 UI 问题合并成可分批实施、独立验收的计划。

**Architecture:** 第一批修取词、结果恢复、交互与轻量样式；第二批修正确性和存储提交；第三批完善发布及证据留存。先给现有流程增加明确边界与回归检查，再拆出必要的小模块，不进行与这些问题无关的重写。

**Tech Stack:** Windows 10/11、.NET Framework 4.8、C# 5、WPF、Windows Credential Manager、Windows PowerShell 5.1、现有无第三方包构建。

**Spec:** [设计约定](../specs/2026-10-06-leaf-improvements-design.md)。

## Global Constraints

- 当前状态：只编写计划，所有实施步骤未执行。用户最新“先不要执行计划”覆盖之前实施授权。
- 默认唤起不抢焦点；可开启「快捷键唤起后直接输入」，取词完成后聚焦原文并选中全文。
- 剪贴板模式读取仅发生在快捷键调用时，不持续翻译每次复制。
- 追问不自动置顶。
- 密钥仅存 Windows Credential Manager。修改接口地址需重新填密钥。
- 不记录密钥、请求/响应正文、学习语境或对话；无自动上传。
- 原文输入保留 Enter 提交、Shift+Enter 换行；显式提交建立新会话。
- 场景、服务、偏好和历史继续放在托盘窗口。
- 视觉采用「清晰纸面」：白色背景、深色正文、清楚的次级文字和中性控件，靠留白与细分隔线组织内容。
- 外部发布不属于实施这些修复的默认授权；不自动 push/release。
- 保留当前0.4.1版本标识，待用户明确发布新版本时再改版本常量。

## 事实与判断

本次计划基于已读产品约定、实际代码、隔离回归探测、用户的 selection 日志与两张输入截图。原先的 145 项核心/应用断言及 32 项 WPF 检查通过，但不覆盖所有新增问题。

- Codeg 日志已经走到 CaptureAndTranslate/SelectedTextAsync/STA 回退，证明至少这些操作触发到了 Leaf；不能简单归因为“Leaf 没收到热键”。
- Alt 方框可能是菜单/焦点提示，也可能改变被选元素。日志缺少阶段信息，当前不能确认具体根因或承诺所有页面都能读取。
- 同文本恢复缺陷明确：命中已有请求/结果时仅显示浮窗，没有恢复阅读状态；Leaf 前台分支也直接进编辑。
- 输入框会随窗口缩放，占位字号固定16，且输入继承单行框居中、占位单独顶对齐；必须统一布局参数。
- 原先确认的半提交、Unicode、句段判断、历史恢复/性能和发布删除问题继续纳入，不被 UI 修改替代。

## 批次与执行顺序

### 第一批：取词与浮窗

详细步骤：[leaf-capture-popup.md](2026-10-06-leaf-capture-popup.md)。

- [ ] Task 1：分型结果与内容无关的阶段诊断。
- [ ] Task 2：同窗口 UIA 有限查找、一次安全复制回退、空选区/未知/系统失败的不同反馈。
- [ ] Task 3：清空草稿后，同文本恢复原结果与会话，不增加 API 调用。
- [ ] Task 4：占位与光标对齐；移除常驻教学行；原文和追问统一轻量矢量按钮。
- [ ] Task 5：停止什么就重试什么，保留已完成会话与输入草稿。

出口：自动回归、逐图检查、隔离原生检查和 Codeg 手动矩阵。可独立交付。

### 第二批：正确性与存储

详细步骤：[leaf-correctness-storage.md](2026-10-06-leaf-correctness-storage.md)。

- [ ] Task 1：连续书写语言走句段翻译，词卡截断保持 Unicode。
- [ ] Task 2：设置 patch、有序后台队列、可恢复提交及地址绑定 Key。
- [ ] Task 3：结构异常的有效 JSON 不阻止启动，合法历史按条恢复。
- [ ] Task 4：精简学习语境快照，按 UTF-8 字节线性裁剪，后台保存与退出冲刷。

出口：提交故障矩阵、旧数据/Key 迁移、200/1000 条压力、删除/关闭保存不复活数据。

### 第三批：发布与验证

详细步骤：[leaf-release-quality.md](2026-10-06-leaf-release-quality.md)。

- [ ] Task 1：隔离公开源镜像同步新增/修改/删除，离线 Git 夹具验证边界。
- [ ] Task 2：单一版本常量驱动程序集、manifest 和打包参数。
- [ ] Task 3：CI 留存报告/PNG，检查公开源与文档，整理双语说明。

出口：离线同步检查、打包一致性、CI 失败产物与文档检查。不会因此自动发布。

## 新增问题到任务的对应

| 用户问题 | 实施任务 | 核心验收 |
| --- | --- | --- |
| Codeg/其他页面取词失败，Alt 出方框 | 第一批1–2 | 阶段原因可辨，比较Alt与非Alt组合；不猜冲突，不屏蔽所有Alt |
| 没选中文字也报错 | 第一批1–2 | 确认空选区不报selection；未知能力不足不伪装为空 |
| 清空后重复选区无法恢复结果 | 第一批3 | 原会话ID、译文、词卡、追问草稿保留；API计数不增加 |
| 光标/占位错位 | 第一批4 | 同字体字号起点，默认/紧凑、DPI检查及实际caret检查 |
| 原文区按钮笨重、提示太多 | 第一批4 | 常驻教学行消失，空输入隐藏提交，鼠标和键盘仍可提交 |
| 追问按钮不精致 | 第一批4 | 同系列矢量、透明底、轻量状态，键盘/辅助名称完整 |
| 原先取消重试、正确性、存储、发布问题 | 第一批5、第二批、第三批 | 每项保留对应的故障回归与验收证据 |

## 验证命令与证据规则

以下仅用于未来实施：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1 -OutputDirectory work/verification
.\work\verification\Leaf.Tests.exe --native
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/performance.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-publish.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-docs.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish.ps1 -CheckOnly
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package.ps1
```

每任务先写能证明问题的检查、确认失败，再改实现、确认通过，最后做该任务的局部提交。UI 修改必须调用实际嵌入 XAML 的 smoke renderer，并查看受影响的 PNG；离屏图不能替代实际 caret、Codeg UIA 或跨屏焦点验证。API fixture 不能称为真实供应商测试。

每批记录代码差异、检查数量/结果、界面证据、桌面实际观察及局限。源文/对话/Key 不写进日志或公开测试输出；压力报告注明样本与机器，不能把目标当实测数字。

## 本次文档状态

本次只新增这份总计划、三份实施计划和一份设计约定。源码、产品基线、运行配置、实际快捷键、凭据和历史未修改。新增问题已经全部映射到具体任务；当前没有执行或外部发布步骤。
