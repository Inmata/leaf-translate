# Leaf 取词与浮窗 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 Codeg 取词的可诊断性、空选区错误、同文本恢复及取消重试，并按用户截图简化原文编辑和追问。

**Architecture:** 在 Native 与 AppShell 之间加入可替换的取词协调层，返回分型结果和内容无关的诊断。把显示恢复与新会话创建分开；只为当前浮窗增加编辑/动作样式，不改变设置页样式。

**Tech Stack:** Windows 10/11、.NET Framework 4.8、C# 5、WPF、UIAutomationClient、现有无第三方包编译脚本。

**Spec:** [设计约定](../specs/2026-10-06-leaf-improvements-design.md)，需求 C1–C3、U1–U3、R1。

## Global Constraints

- 默认唤起不抢焦点；可开启「快捷键唤起后直接输入」，取词完成后聚焦原文并选中全文。
- 剪贴板模式读取仅发生在快捷键调用时，不持续翻译每次复制。
- 追问不自动置顶。
- 不记录密钥、请求/响应正文、学习语境或对话；无自动上传。
- 原文输入保留 Enter 提交、Shift+Enter 换行；显式提交建立新会话。
- 场景、服务、偏好和历史继续放在托盘窗口。
- 视觉采用「清晰纸面」：白色背景、深色正文、清楚的次级文字和中性控件，靠留白与细分隔线组织内容。
- 新源码放在 src/Leaf 顶层；build.ps1 只枚举该层 .cs。
- 本计划描述未来实施步骤。当前用户只授权编写计划；禁止现在执行代码、桌面输入、提交或发布。
- 验证命令使用 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1 -OutputDirectory work/verification`。不与正在运行的 bin/Leaf.exe 争用文件。

## File Structure

| 文件 | 职责 |
| --- | --- |
| 新增 src/Leaf/Capture.cs | 取词状态、原因、目标快照、诊断类型、协调器与探针接口 |
| 新增 src/Leaf/WindowsSelectionProbe.cs | UIA 候选查找、STA 复制、前台及剪贴板安全检查 |
| 修改 Native.cs、AppShell.cs、Diagnostics.cs | 兼容包装、快捷键协调和结构化日志 |
| 修改 Popup.cs、Views/Popup.xaml、Views/Theme.xaml | 同会话恢复、草稿、操作重试、编辑与追问样式 |
| 新增 tests/SelectionTests.cs、tests/PopupRegressionTests.cs | 可替换探针与同会话/取消回归 |
| 修改 CoreTests.cs、ApplicationTests.cs、Smoke.cs、WindowsNativeTests.cs | 注册新增检查及渲染/原生验收 |
| 修改 PRODUCT.md、双语 USAGE/README、相关演示截图 | 实施成功后的行为和截图同步 |

## Task 1: 分型取词结果与内容无关的诊断（C1、C2）

**Files:**
- Create: `src/Leaf/Capture.cs`
- Modify: `src/Leaf/Diagnostics.cs:19`，`tests/CoreTests.cs:33`
- Test: `tests/SelectionTests.cs`

**Interfaces:**
- Consumes: `DiagnosticLog` 的轮换写入及现有 `HotkeySpec.Parse(string)`。
- Produces: 以下模型、`ISelectionProbe`、`SelectionAcquirer`、`DiagnosticLog.Capture(CaptureTelemetry)` 和 `SelectionTests.Run(): Task<int>`。

- [ ] **Step 1: 写结果路由、阶段日志及取消的失败测试。**

新增独立测试入口，不创建 WPF Application；CoreTests.Main 在 Transport 之前合并返回的断言数。测试文件使用自己的 Check 计数器。

```csharp
public static async Task<int> Run()
{
    int count = 0;
    var probe = new FakeSelectionProbe();
    var reader = new SelectionAcquirer(probe);
    probe.Automation = SelectionCaptureResult.NoText(
        CaptureStatus.Empty, CaptureReason.EmptySelection);
    var empty = await reader.CaptureAsync(probe.Snapshot(), false,
        "Alt+Space", CancellationToken.None);
    if (empty.Status != CaptureStatus.Empty || probe.CopyCalls != 0)
        throw new Exception("Confirmed empty document must skip copy fallback.");
    count++;

    probe.Automation = SelectionCaptureResult.NoText(
        CaptureStatus.Unavailable, CaptureReason.UnsupportedPattern);
    probe.Copy = SelectionCaptureResult.Success("fresh fixture");
    var fresh = await reader.CaptureAsync(probe.Snapshot(), false,
        "Ctrl+Alt+T", CancellationToken.None);
    if (fresh.Status != CaptureStatus.Text || fresh.Text != "fresh fixture")
        throw new Exception("Unsupported UIA must attempt one fresh copy.");
    count++;

    probe.Copy = SelectionCaptureResult.NoText(
        CaptureStatus.Unavailable, CaptureReason.CopyNoUpdate);
    var unavailable = await reader.CaptureAsync(probe.Snapshot(), false,
        "Ctrl+Shift+T", CancellationToken.None);
    if (unavailable.Status != CaptureStatus.Unavailable)
        throw new Exception("No copy update is not proof of an empty selection.");
    count++;

    probe.Current = false;
    int before = probe.CopyCalls;
    var moved = await reader.CaptureAsync(probe.Snapshot(), false,
        "Alt+Space", CancellationToken.None);
    if (moved.Reason != CaptureReason.ForegroundChanged ||
        probe.CopyCalls != before)
        throw new Exception("Foreground change must prevent injected copy.");
    count++;
    return count;
}
```

在 `tests/SelectionTests.cs` 定义以下辅助类，实现五个接口方法。应用检查需要复用该类，因此声明为 internal，不嵌入私有测试类。迟到 UIA 检查使用 `AutomationPending` 控制完成时点。

```csharp
internal sealed class FakeSelectionProbe : ISelectionProbe
{
    public CaptureTarget Target = new CaptureTarget {
        Window = new IntPtr(123), ProcessId = 456
    };
    public SelectionCaptureResult Automation = SelectionCaptureResult.NoText(
        CaptureStatus.Unavailable, CaptureReason.UnsupportedPattern);
    public SelectionCaptureResult Copy = SelectionCaptureResult.NoText(
        CaptureStatus.Unavailable, CaptureReason.CopyNoUpdate);
    public SelectionCaptureResult Clipboard = SelectionCaptureResult.Success("clipboard fixture");
    public TaskCompletionSource<SelectionCaptureResult> AutomationPending;
    public CancellationToken AutomationToken;
    public bool Current = true;
    public int AutomationCalls, CopyCalls, ClipboardCalls;
    public CaptureTarget Snapshot() { return Target; }
    public bool IsCurrent(CaptureTarget target) { return Current; }
    public Task<SelectionCaptureResult> ReadAutomationAsync(
        CaptureTarget target, CancellationToken cancellation)
    {
        AutomationCalls++; AutomationToken = cancellation;
        return AutomationPending == null ? Task.FromResult(Automation) : AutomationPending.Task;
    }
    public Task<SelectionCaptureResult> CopySelectionAsync(
        CaptureTarget target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        CopyCalls++; return Task.FromResult(Copy);
    }
    public Task<SelectionCaptureResult> ReadClipboardAsync(
        CaptureTarget target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ClipboardCalls++; return Task.FromResult(Clipboard);
    }
}
```

SelectionTests 内的额外用例使用下面的断言入口；Run 开头重置 assertions，最后返回 count + assertions，不能遗漏上述局部 count。

```csharp
private static int assertions;
private static void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAILED: " + label);
    assertions++;
}
```

日志测试输入 "private-selection-fixture" 作为 Text；断言日志仅包含 capture_id/status/reason 等白名单，绝不包含该字符串、标题或内容哈希。再增加预取消令牌、250 ms 后迟到的 UIA、剪贴板模式完全跳过 UIA/Copy 的用例。取消状态统一返回 Cancelled，不要求调用者捕捉 OperationCanceledException。

- [ ] **Step 2: 运行失败测试并确认失败点为缺少新类型/入口。**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1 -OutputDirectory work/verification
```

- [ ] **Step 3: 实现稳定模型与协调器。**

```csharp
public enum CaptureStatus { Text, Empty, Unavailable, Failed, Cancelled }
public enum CaptureReason
{
    None, Desktop, EmptySelection, UnsupportedPattern, AutomationTimeout,
    AutomationBusy, AutomationBudget, ForegroundChanged, ModifiersHeld, CopyNoUpdate,
    ClipboardOriginUnknown,
    ClipboardBusy, CaptureBusy, InputDenied, SystemFailure, Cancelled
}
public sealed class CaptureTarget
{
    public IntPtr Window { get; set; }
    public uint ProcessId { get; set; }
    public bool IsDesktop { get; set; }
    public bool IsLeaf { get; set; }
}
public sealed class CaptureStep
{
    public string Stage { get; set; } // Only fixed names emitted by the probe.
    public CaptureReason Reason { get; set; }
    public int CandidateCount { get; set; }
    public int ControlTypeId { get; set; }
    public bool PatternSupported { get; set; }
    public bool ForegroundSame { get; set; }
    public bool ClipboardChanged { get; set; }
    public bool ModifiersReleased { get; set; }
    public int NativeError { get; set; }
    public int HResult { get; set; }
    public long ElapsedMs { get; set; }
}
public sealed class CaptureTelemetry
{
    public string Id { get; set; }
    public string Shortcut { get; set; }
    public bool ClipboardMode { get; set; }
    public uint ForegroundProcessId { get; set; }
    public CaptureStatus Status { get; set; }
    public CaptureReason Reason { get; set; }
    public int TextLength { get; set; }
    public List<CaptureStep> Steps { get; set; }
}
public sealed class SelectionCaptureResult
{
    public CaptureStatus Status { get; private set; }
    public CaptureReason Reason { get; private set; }
    public string Text { get; private set; }
    public List<CaptureStep> Steps { get; private set; }
    public static SelectionCaptureResult Success(string text)
    {
        return new SelectionCaptureResult { Status = CaptureStatus.Text,
            Reason = CaptureReason.None, Text = text,
            Steps = new List<CaptureStep>() };
    }
    public static SelectionCaptureResult NoText(CaptureStatus status,
        CaptureReason reason)
    {
        if (status == CaptureStatus.Text) throw new ArgumentException("status");
        return new SelectionCaptureResult { Status = status, Reason = reason,
            Text = "", Steps = new List<CaptureStep>() };
    }
}
public interface ISelectionProbe
{
    CaptureTarget Snapshot();
    bool IsCurrent(CaptureTarget target);
    Task<SelectionCaptureResult> ReadAutomationAsync(
        CaptureTarget target, CancellationToken cancellation);
    Task<SelectionCaptureResult> CopySelectionAsync(
        CaptureTarget target, CancellationToken cancellation);
    Task<SelectionCaptureResult> ReadClipboardAsync(
        CaptureTarget target, CancellationToken cancellation);
}
```

SelectionAcquirer 构造函数接收 ISelectionProbe，并暴露 Probe 的 Snapshot。以下成员放进该类；开始时校验快捷键，不将未经校验的自由文本写入日志：

```csharp
private readonly ISelectionProbe probe;
public SelectionAcquirer(ISelectionProbe selectionProbe)
{
    if (selectionProbe == null) throw new ArgumentNullException("selectionProbe");
    probe = selectionProbe;
}
public CaptureTarget Snapshot() { return probe.Snapshot(); }
public async Task<SelectionCaptureResult> CaptureAsync(CaptureTarget target,
    bool clipboardMode, string shortcut, CancellationToken cancellation)
{
    try { return await CaptureCoreAsync(target, clipboardMode, shortcut, cancellation); }
    catch (OperationCanceledException) {
        return SelectionCaptureResult.NoText(CaptureStatus.Cancelled, CaptureReason.Cancelled);
    }
    catch (Exception) {
        return SelectionCaptureResult.NoText(CaptureStatus.Failed, CaptureReason.SystemFailure);
    }
}
private async Task<SelectionCaptureResult> CaptureCoreAsync(CaptureTarget target,
    bool clipboardMode, string shortcut, CancellationToken cancellation)
{
    cancellation.ThrowIfCancellationRequested();
    HotkeySpec.Parse(shortcut);
    if (clipboardMode)
        return await probe.ReadClipboardAsync(target, cancellation);
    if (target.IsDesktop)
        return SelectionCaptureResult.NoText(CaptureStatus.Empty, CaptureReason.Desktop);
    if (!probe.IsCurrent(target))
        return SelectionCaptureResult.NoText(
            CaptureStatus.Unavailable, CaptureReason.ForegroundChanged);
    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
        var automation = probe.ReadAutomationAsync(target, deadline.Token);
        var first = await Task.WhenAny(automation, Task.Delay(250, cancellation));
        cancellation.ThrowIfCancellationRequested();
        if (!probe.IsCurrent(target)) {
            deadline.Cancel();
            return SelectionCaptureResult.NoText(
                CaptureStatus.Unavailable, CaptureReason.ForegroundChanged);
        }
        if (first == automation) {
            var result = await automation;
            if (result.Status == CaptureStatus.Text || result.Status == CaptureStatus.Empty)
                return result;
            if (result.Status == CaptureStatus.Cancelled) return result;
        } else deadline.Cancel();
        cancellation.ThrowIfCancellationRequested();
        return await probe.CopySelectionAsync(target, cancellation);
    }
}
```

实际协调器收集每阶段 Steps；超时增加固定 uia/AutomationTimeout 阶段；即使忽略迟到 UIA，也观察其 fault，避免未观察异常。捕获预期 OperationCanceledException 映射 Cancelled，其余系统异常映射 Failed/SystemFailure，仅记录 HResult/NativeError，不附加 Exception.Message。应用层最后统一构造 CaptureTelemetry 并调用日志接口。

DiagnosticLog.Capture 对 Stage 使用固定白名单 `snapshot/uia/copy_wait/copy_send/clipboard_read/clipboard_restore/result`；原因/状态来自枚举。每个阶段一条、最终一条结果，共用 capture_id。仿照现有 Request 使用 Base、Write，不能调用 Json.Write(result) 或接收 Text 参数。

- [ ] **Step 4: 跑全部检查，确认新断言及日志私密性检查通过。**
- [ ] **Step 5: 单独提交这一可测试基础。**

```powershell
git add -- src/Leaf/Capture.cs src/Leaf/Diagnostics.cs tests/SelectionTests.cs tests/CoreTests.cs
git commit -m "feat: classify selection capture and record safe stage diagnostics"
```

## Task 2: Windows UIA 与安全复制回退（C1、C2）

**Files:**
- Create: `src/Leaf/WindowsSelectionProbe.cs`
- Modify: `src/Leaf/Native.cs:150`、`src/Leaf/AppShell.cs:139`
- Test: `tests/SelectionTests.cs`、`tests/WindowsNativeTests.cs`

**Interfaces:**
- Consumes: Task 1 的 ISelectionProbe 与结果类型，Native 的 HWND/剪贴板/SendInput API。
- Produces: `WindowsSelectionProbe : ISelectionProbe`；`Native.CopySelectionAsync(CaptureTarget, CancellationToken): Task<SelectionCaptureResult>`（只执行安全 STA 复制，不再内部调用 UIA）；AppShell 的 `public SelectionAcquirer Selection { get; set; }` 和 `public Task InvokeShortcutAsync()`。旧 SelectedTextAsync 兼容包装保留，复用同一复制 worker；运行时以 SelectionAcquirer 为唯一完整取词协调层。

- [ ] **Step 1: 写复制状态机的夹具检查与应用层空选区检查。**

通过独立的、无 OS 副作用的决策函数测试新序列/旧序列、前台改变和取消。接口定义：

```csharp
public static bool CanReadFreshCopy(uint before, uint observed,
    bool foregroundSame, bool cancelled)
{
    return !cancelled && foregroundSame && observed != before;
}
public static bool CanRestoreCopy(uint captured, uint current,
    bool foregroundSame)
{
    return foregroundSame && captured != 0 && captured == current;
}
```

测试代码必须覆盖：

```csharp
Check(!WindowsSelectionProbe.CanReadFreshCopy(10, 10, true, false),
    "Old clipboard sequence cannot become selection text");
Check(!WindowsSelectionProbe.CanReadFreshCopy(10, 11, false, false),
    "Another foreground window cannot supply selection text");
Check(!WindowsSelectionProbe.CanReadFreshCopy(10, 11, true, true),
    "Cancellation prevents delayed capture side effects");
Check(WindowsSelectionProbe.CanReadFreshCopy(10, 11, true, false),
    "A current copy can be read");
Check(!WindowsSelectionProbe.CanRestoreCopy(11, 12, true),
    "A newer user copy must survive restoration");
```

应用层用 FakeSelectionProbe 返回 Empty/Unavailable/Failed。Empty 无 ErrorPanel；Unavailable 打开手动输入且来源徽标为“未取得选区”，没有 capture_failed；Failed 有具体短错误。迟到捕获在用户编辑或收起后不能覆盖输入；调用数仍为零。

- [ ] **Step 2: 运行上述失败测试。**
- [ ] **Step 3: 实现 Windows 探针并连接快捷键入口。**

当前机器 .NET Framework UIAutomationClient 中没有托管 TextPattern2。使用现有 TextPattern，不引用缺失类型，不添加 UIA 包。Candidate 查找限定 root HWND/PID，用 TreeWalker 逐节点遍历，最多访问 128 个节点、检查 32 个候选；每个节点前检查取消和 elapsed 时间。禁止无界 FindAll(Descendants)。不读取 Name/Value。焦点空选区不是最终结论；关联 Document 和同窗口候选没有非空结果后，只有可信 Document 且查询完整结束才返回 Empty。查询 timeout/节点预算耗尽/模式不支持返回 Unavailable。

把 Native 当前 worker 内部 CaptureResult 重命名为 StaCaptureResult，避免与新模型混淆。复制前、SendInput 前、剪贴板读取前检查令牌/前台。以 CaptureReason 区分 modifiers-held、copy-no-update、input-denied、clipboard-busy；保留一次 Ctrl+C 与规定时限。Ctrl+C 未产生新文本不能抛一个没有原因的 selection 异常。新剪贴板序列是必要条件；同时检查 GetClipboardOwner 对应的原前台进程，无法确认来源或观察到另一进程写入时返回 Unavailable，不把序列变化本身当作本次复制的充分证明。

STA 使用与调用令牌链接的局部 CancellationTokenSource；1600 ms 超时时先 Cancel，再返回 Unavailable，迟到 worker 在等待修饰键后必须再次检查令牌，绝不能继续 SendInput。工作线程完成前保持该局部 source 存活，由完成 continuation 观察异常并 Dispose。UIA 与 STA 各保留一个在途槽，调用超时不能释放仍被系统阻塞的槽或继续新建线程；线程真正结束才释放槽。

AppShell 构造时初始化 Selection。原 private async void CaptureAndTranslate 只包装 await InvokeShortcutAsync；主体移到可测试方法。增加字段与取消入口：

```csharp
private CancellationTokenSource captureCancellation;
private void CancelCapture()
{
    if (captureCancellation != null) captureCancellation.Cancel();
}
```

SourceInput 的用户 TextChanged（!fillingSource）、显式 BeginSourceEdit(true)、SubmitSourceAsync、HidePopup 和退出/Dispose 都调用 CancelCapture；ShowPopup 和程序补回原文不取消。每次调用创建独立 source，不复用已取消实例，finally 清理 owner 并释放 captureBusy。获取结束就释放忙状态，不把整段翻译期间也锁成 captureBusy。流程固定如下：

```csharp
var target = Selection.Snapshot();
var invocation = Json.Copy(Store.Settings);
if (captureBusy || shortcutRecording || exiting) return;
if (!invocation.ClipboardMode && target.IsLeaf) {
    HandleLeafSelectionShortcut(invocation.FocusInputOnShortcut);
    return;
}
long revision = sourceRevision;
var owner = new CancellationTokenSource();
captureCancellation = owner; captureBusy = true;
SelectionCaptureResult result;
try {
    var resultTask = Selection.CaptureAsync(target, invocation.ClipboardMode,
        invocation.Shortcut, owner.Token);
    ShowPopup();
    result = await resultTask;
} finally {
    if (ReferenceEquals(captureCancellation, owner)) captureCancellation = null;
    captureBusy = false; owner.Dispose();
}
if (sourceRevision != revision || exiting || result.Status == CaptureStatus.Cancelled) return;
if (result.Status == CaptureStatus.Text) {
    var translation = TranslateAsync(result.Text,
        invocation.ClipboardMode ? "剪贴板" : "选中文字", false);
    if (invocation.FocusInputOnShortcut) BeginSourceEdit(true);
    await translation;
} else if (result.Status == CaptureStatus.Empty ||
           result.Status == CaptureStatus.Unavailable) {
    PrepareManualInput(result.Status == CaptureStatus.Unavailable);
} else if (result.Status == CaptureStatus.Failed) {
    PrepareManualInput(true);
    ShowError(CaptureMessages.For(result.Reason), null);
}
```

同一任务定义 `private void HandleLeafSelectionShortcut(bool directInput)`：选中模式且自身前台时先沿用现有编辑行为，Task 3 将其替换为明确恢复规则；剪贴板模式始终走 ReadClipboardAsync，不进入此分支。定义 `private void PrepareManualInput(bool unavailable)` 与 `CaptureMessages.For(CaptureReason): string`：不清空 Current；空或尚未开始的编辑页准备输入，已有非空草稿保留；未取得选区的建议仅在 SourceBadge.ToolTip 中按需显示。桌面/空选区不弹错误。进入时 snapshot 所有行为设置，不在结束时读已经变化的模式。CaptureMessages 固定映射：InputDenied→“当前程序不允许读取选区，请手动复制后使用剪贴板模式。”；ClipboardBusy→“剪贴板暂时不可用，可稍后再试。”；其他 Failed→“取词未完成，可重试或手动输入。”。

- [ ] **Step 4: 跑自动检查，再执行隔离原生检查及 Codeg 手动矩阵。**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1 -OutputDirectory work/verification
.\work\verification\Leaf.Tests.exe --native
```

Codeg 验收只在未来实施期间做：三个快捷键分别测试只按 Alt、有选区、无选区、手动 Ctrl+C 对照。记录哪个阶段失败及是否前台/选区变化。测试配置保存在隔离目录；恢复用户原快捷键。只有桌面结果支持时才把 Codeg 根因写进修复报告；不自动改全局默认键，不屏蔽单独 Alt，不向未配置 API 发阅读文字。

- [ ] **Step 5: 提交探针和对应说明。**

```powershell
git add -- src/Leaf/WindowsSelectionProbe.cs src/Leaf/Native.cs src/Leaf/AppShell.cs tests/SelectionTests.cs tests/WindowsNativeTests.cs docs/PRODUCT.md docs/USAGE.md docs/USAGE.en.md
git commit -m "fix: acquire fresh selections with bounded Windows fallbacks"
```

## Task 3: 同文本恢复与编辑草稿分离（C3）

**Files:**
- Modify: `src/Leaf/Popup.cs:160`、`src/Leaf/Popup.cs:310`、`src/Leaf/AppShell.cs:139`
- Create: `tests/PopupRegressionTests.cs`
- Modify: `tests/ApplicationTests.cs:40`

**Interfaces:**
- Consumes: Current、sourceRevision、已有 SelectedCard/Word 和 Task 2 的可等待快捷键入口。
- Produces: `private void RestoreCurrentPresentation(bool directInput)`；`PopupRegressionTests.Run(string folder): Task<int>`，在已有 WPF Application 的 Scenarios 内调用。

- [ ] **Step 1: 写精确复现测试。**

PopupRegressionTests 定义 FixtureHandler：SendAsync 计数，返回合法 content 与 finish_reason=stop 的 JSON；默认配置 Model 为 fixture-model、NativeEnabled=false。测试不显示桌面窗口。

```csharp
await shell.TranslateAsync("A fixture sentence.", "选中文字", false);
var record = shell.Current;
record.Chat.Add(new ChatTurn { Role = "user", Content = "older fixture", Topic = "原句" });
record.Chat.Add(new ChatTurn { Role = "assistant", Content = "older answer", Topic = "原句" });
Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text = "unsent question";
shell.BeginSourceEdit(false);
Ui.Get<TextBox>(shell.Popup, "SourceInput").Clear();
int calls = handler.Calls;
await shell.TranslateAsync(record.Source, "选中文字", false);
Check(ReferenceEquals(shell.Current, record) && handler.Calls == calls,
    "Same-source recovery keeps the record and makes no API call");
Check(Ui.Get<Grid>(shell.Popup, "SourceEditor").Visibility == Visibility.Collapsed,
    "Same-source recovery returns to the reading page");
Check(Ui.Get<TextBox>(shell.Popup, "QuestionInput").Text == "unsent question" &&
    record.Chat.Count == 2, "Recovery keeps conversation and draft");
```

再以 FakeSelectionProbe.Target.IsLeaf=true 调用 InvokeShortcutAsync，复现“焦点仍在 Leaf + 空草稿”；检查同样恢复。另设 ClipboardMode=true、Clipboard="new clipboard fixture"，确认自身前台也读取本次剪贴板并按新内容翻译，不恢复旧会话。补充非空不同草稿不丢失、同文本在途复用、显式 SubmitSourceAsync 创建新 ID、直接输入偏好恢复后填回全文并聚焦的检查。

- [ ] **Step 2: 跑失败检查，确认目前会停留 SourceEditor。**
- [ ] **Step 3: 实现恢复方法，避免调用 DisplayRecord 重置词卡和草稿。**

```csharp
private void RestoreCurrentPresentation(bool directInput)
{
    if (Current == null) return;
    fillingSource = true;
    try { Ui.Get<TextBox>(Popup, "SourceInput").Text = Current.Source; }
    finally { fillingSource = false; }
    ShowSourceReadOnly();
    DrawTranslation(selectedCard == null ? "" : selectedCard.target_phrase);
    Topic();
    Busy();
    ShowPopup();
    if (directInput) BeginSourceEdit(true);
}
```

TranslateAsync 的同缓存键 Completed/translating 分支调用 RestoreCurrentPresentation(false)，不使用 DisplayRecord。HandleLeafSelectionShortcut 只处理选中模式：Current 存在、编辑状态为空、与原文相同或根本没编辑时恢复；编辑状态为非空不同草稿时保留；无 Current 时继续准备输入。直接输入选项仍由快捷键协调层明确应用。清空 SourceInput 的 TextChanged 只增修订号并取消未完成捕获，不删 Current。PrepareManualInput 不把未提交草稿当新会话。

- [ ] **Step 4: 跑完整应用检查，核对缓存、历史和直接输入的旧检查仍通过。**
- [ ] **Step 5: 提交恢复行为。**

```powershell
git add -- src/Leaf/Popup.cs src/Leaf/AppShell.cs tests/PopupRegressionTests.cs tests/ApplicationTests.cs docs/PRODUCT.md docs/USAGE.md docs/USAGE.en.md
git commit -m "fix: restore cached reading results after clearing an edit draft"
```

## Task 4: 原文对齐与轻量编辑/追问控件（U1、U2、U3）

**Files:**
- Modify: `src/Leaf/Views/Popup.xaml`、`src/Leaf/Views/Theme.xaml:72`、`src/Leaf/Popup.cs:21`、`src/Leaf/Popup.cs:203`
- Modify: `src/Leaf/Smoke.cs`、`src/Leaf/Windows.cs:41`、`tests/WindowsNativeTests.cs`
- Test: WPF smoke geometry、离屏截图及隔离原生焦点检查

**Interfaces:**
- Consumes: 既有 SourceInput、SourcePlaceholder、TranslateButton、AskButton、SendButton、CloseQuestionButton 名称。
- Produces: PopupEditor、PopupActionButton、SubmitArrow/ChatOutline/FoldChevron 三个 Geometry 资源；删除 SourceActions/SourceEditHint/EmptyHint 的视图与代码引用。
- Produces: `Ui.Render(Window window, string path, double dpi): void`，保留两参 Render 作为 96 DPI 包装；原文 Hyperlink 的键盘入口和顶栏图标的辅助名称。

- [ ] **Step 1: 为布局和常驻提示添加失败检查。**

```csharp
shell.BeginSourceEdit(false);
shell.Popup.Width = 360; shell.Popup.Height = 380;
shell.UpdatePopupTypography();
Ui.Render(shell.Popup, Path.Combine(folder, "input-compact.png"));
var editor = Ui.Get<TextBox>(shell.Popup, "SourceInput");
var hint = Ui.Get<TextBlock>(shell.Popup, "SourcePlaceholder");
Check(editor.FontSize == hint.FontSize && editor.FontFamily.ToString() == hint.FontFamily.ToString(),
    "Input and placeholder share typography", checks);
Check(editor.VerticalContentAlignment == VerticalAlignment.Top,
    "Popup editor aligns text at the top", checks);
Check(shell.Popup.FindName("SourceEditHint") == null &&
    shell.Popup.FindName("EmptyHint") == null,
    "Original editor has no permanent keyboard tutorial", checks);
Check(Ui.Get<Button>(shell.Popup, "TranslateButton").Visibility == Visibility.Collapsed,
    "Empty input hides its submit action", checks);
```

渲染后读取 GetRectFromCharacterIndex(0,true)，把坐标换算到 SourceEditor；与占位首行起点比较 <=1 DIP。检查非空后按钮可见，键盘能激活，AutomationProperties.Name 有意义。光标的真正绘制须在隔离原生窗口截图/人工查看，不能宣称离屏 PNG 已验证原生 caret。

- [ ] **Step 2: 跑失败检查，并留存当前默认/紧凑输入截图作为对照。**
- [ ] **Step 3: 写专用编辑样式、矢量资源及 XAML。**

```xml
<Geometry x:Key="SubmitArrow">M4,12 L20,12 M13,5 L20,12 L13,19</Geometry>
<Geometry x:Key="ChatOutline">M4,4 L20,4 L20,16 L10,16 L5,20 L5,16 L4,16 Z</Geometry>
<Geometry x:Key="FoldChevron">M5,15 L12,8 L19,15</Geometry>
<Style x:Key="PopupEditor" TargetType="TextBox">
  <Setter Property="Background" Value="Transparent"/>
  <Setter Property="BorderThickness" Value="0"/>
  <Setter Property="Padding" Value="0,3"/>
  <Setter Property="VerticalContentAlignment" Value="Top"/>
  <Setter Property="Template"><Setter.Value>
    <ControlTemplate TargetType="TextBox">
      <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}"
                    VerticalAlignment="Top" Background="Transparent"/>
    </ControlTemplate>
  </Setter.Value></Setter>
</Style>
<Style x:Key="PopupActionButton" TargetType="Button"
       BasedOn="{StaticResource GhostButton}">
  <Setter Property="Width" Value="30"/>
  <Setter Property="Height" Value="30"/>
  <Setter Property="Padding" Value="0"/>
  <Setter Property="Foreground" Value="{StaticResource Ink}"/>
  <Setter Property="Background" Value="Transparent"/>
</Style>
```

PopupActionButton 加键盘 focus 可见背景，与 hover 使用 Soft。Path 使用 1.5 StrokeThickness、Round line caps/joins，放进 14×14 Viewbox，不能用字体 glyph 假装矢量。

SourceEditor 改成两列，第二列 Auto；保留 SourceInput 与 SourcePlaceholder，并绑定占位排版：

```xml
<Grid x:Name="SourceEditor" Visibility="Collapsed">
  <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
  <TextBox x:Name="SourceInput" Style="{StaticResource PopupEditor}"
           AcceptsReturn="True" TextWrapping="Wrap" MinHeight="38" MaxHeight="110"
           FontSize="16" MaxLength="6000" VerticalScrollBarVisibility="Auto"
           AutomationProperties.Name="原文"/>
  <TextBlock x:Name="SourcePlaceholder" Text="输入要翻译的文字…"
             IsHitTestVisible="False" VerticalAlignment="Top"
             Margin="{Binding Padding, ElementName=SourceInput}"
             FontFamily="{Binding FontFamily, ElementName=SourceInput}"
             FontSize="{Binding FontSize, ElementName=SourceInput}"
             FontWeight="{Binding FontWeight, ElementName=SourceInput}"
             Foreground="{StaticResource Muted}"/>
  <Button x:Name="TranslateButton" Grid.Column="1" Margin="8,0,0,0"
          Style="{StaticResource PopupActionButton}" VerticalAlignment="Top"
          ToolTip="翻译" AutomationProperties.Name="翻译原文">
    <Viewbox Width="14" Height="14">
      <Path Data="{StaticResource SubmitArrow}" Stroke="{StaticResource Ink}"
            StrokeThickness="1.5" StrokeStartLineCap="Round"
            StrokeEndLineCap="Round" StrokeLineJoin="Round"/>
    </Viewbox>
  </Button>
</Grid>
```

SourceInput.TextChanged 按是否有内容切换 TranslateButton.Visibility；删除 SourceActions/SourceEditHint/EmptyHint 以及所有 Ui.Get/SetTypography/Visibility 引用。保留空白提交保护与原文更改标志作为状态，不把状态重新写成常驻教学行。AskButton 保留文字，替换图标为 ChatOutline；SendButton 用 PopupActionButton+SubmitArrow，列宽 Auto、间距 8；CloseQuestionButton 用 FoldChevron 与 30×30 命中区。发送 tooltip 只写“发送”，键盘说明放用户指南。QuestionInput 保留边框和多行行为，高度按 56×scale、下限48计算，按钮不缩小。

原文 Hyperlink 从 Focusable=false 改为 true，已有 Click 委托同时响应键盘 Enter；用 IsKeyboardFocused 的样式触发器显示细下划线及 Soft 背景，不把键盘焦点当成发起 API 请求的条件。顶栏 EditSourceButton/SettingsButton/PinButton/HideButton 分别设置“编辑原文/设置/置顶或取消置顶/收起”辅助名称，Tab 可达，Enter/Space 可激活；无激活的 ShowPopup 不主动把焦点移到词语或按钮。自动检查遍历 wordLinks 确認 Focusable 与 focus 样式，原生检查实际 Tab→Enter 查词和关闭窗口后的前台保持。

Render 重载的完整实现保持 DIP 测量，仅输出像素按 DPI 缩放：

```csharp
public static void Render(Window window, string path) { Render(window, path, 96); }
public static void Render(Window window, string path, double dpi)
{
    if (dpi <= 0 || double.IsNaN(dpi) || double.IsInfinity(dpi))
        throw new ArgumentOutOfRangeException("dpi");
    var element = (FrameworkElement)window.Content;
    element.Measure(new Size(window.Width, window.Height));
    element.Arrange(new Rect(0, 0, window.Width, window.Height));
    element.UpdateLayout();
    var bitmap = new RenderTargetBitmap(
        (int)Math.Ceiling(window.Width * dpi / 96),
        (int)Math.Ceiling(window.Height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
    var background = new DrawingVisual();
    using (var drawing = background.RenderOpen())
        drawing.DrawRectangle(window.Background, null, new Rect(0, 0, window.Width, window.Height));
    bitmap.Render(background); bitmap.Render(element);
    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using (var output = File.Create(path)) encoder.Save(output);
}
```

Smoke 的 Check(bool,string,List<string>) 沿用现有入口；上述独立回归 Check(bool,string) 和 Click(Window,string) 在本文末尾定义，不调用现有测试类的 private 方法。

- [ ] **Step 4: 运行 WPF smoke 并逐张查看。**

新增空输入/输入内容/选中词卡/追问展开/发送中/错误状态的默认与紧凑 PNG。分别 Render(...,96/120/144/192)，核对图片像素与字号起点，检查 100/125/150/200% 的输出；离屏重采样不等于系统 DPI 切换，实际跨屏排版、caret 与焦点仍用 --native 和桌面检查。若几何仍不合格，修模板文字起点，不能靠负 margin 掩盖。

- [ ] **Step 5: 同步双语说明及演示截图后提交。**

```powershell
git add -- src/Leaf/Views/Popup.xaml src/Leaf/Views/Theme.xaml src/Leaf/Popup.cs src/Leaf/Windows.cs src/Leaf/Smoke.cs tests/WindowsNativeTests.cs docs/PRODUCT.md docs/USAGE.md docs/USAGE.en.md README.md README.en.md docs/images
git commit -m "refine: align popup input and simplify reading actions"
```

## Task 5: 按操作取消和重试（R1）

**Files:**
- Create: `src/Leaf/RequestOperations.cs`
- Modify: `src/Leaf/Popup.cs:63`、`src/Leaf/Popup.cs:356`、`src/Leaf/Popup.cs:433`、`src/Leaf/AppShell.cs:279`
- Test: `tests/PopupRegressionTests.cs`

**Interfaces:**
- Produces: RequestKind、RetryOperation、RetryBundle；AppShell 的 `StopForRetry()`；私有 `SendChatForAsync(TranslationRecord, string, string, WordCard): Task`。
- Consumes: 已有 generation/wordGeneration、CancellationTokenSource、Current 及传输 client。

- [ ] **Step 1: 用 ControlledHandler 重现停止查词后的错误重试。**

定义该测试文件的 ControlledHandler 与 PendingResponse，行为与现有 ApplicationTests 夹具一致：记录 Uri/Body/Cancellation，返回 TaskCompletionSource<HttpResponseMessage>；ReplyJson/ReplyStream 生成合法兼容响应。

```csharp
var record = shell.Current;
var wordTask = shell.SelectWordAsync(TextTools.Pieces(record.Source)
    .First(p => p.IsWord), false);
Click(shell.Popup, "CancelButton");
Click(shell.Popup, "RetryButton");
Check(ReferenceEquals(shell.Current, record),
    "Retrying a stopped word lookup keeps the conversation");
Check(handler.Requests.Last().Body.Contains("selected_text"),
    "Retry repeats word lookup rather than whole translation");
```

补充停止追问后修改输入框再重试：最后请求必须包含原问题，新的输入草稿不能丢失；已完成 Chat 不清空。并行 word/chat 停止后二者 cancellation 均触发，重试后新增两次对应请求，无第三次 translation。更换 Current 后旧重试无效；重试的 API 地址取最新已保存配置。

- [ ] **Step 2: 跑失败测试，确认旧重试发送 source_text。**
- [ ] **Step 3: 实现会话绑定的操作描述与停止恢复。**

```csharp
public enum RequestKind { Translation, Word, Followup }
public sealed class RetryOperation
{
    public RequestKind Kind { get; set; }
    public string RecordId { get; set; }
    public Func<Task> Run { get; set; }
}
public sealed class RetryBundle
{
    private readonly List<RetryOperation> operations;
    public RetryBundle(IEnumerable<RetryOperation> items)
    { operations = items.ToList(); }
    public async Task RunAsync(string currentId)
    {
        await Task.WhenAll(operations.Where(x => x.RecordId == currentId)
            .Select(x => x.Run()).ToArray());
    }
}
```

AppShell 维护三个 active operation 字段。在请求开始时创建不含 Key 的重试委托，finally 只清理仍属于该请求的字段。StopForRetry 在 CancelRequests 前拍下当前活跃操作，取消后 DrawChat 清理临时占位、恢复可用的旧词卡、保留 QuestionInput；ShowError 的重试闭包捕获 RetryBundle，并核对 Current.Id。新会话入口清除旧 retry。

SendChatAsync 捕获 question/topic/card 后调用 SendChatForAsync；后者不重新读取 QuestionInput 来决定请求内容。完成时仅在当前输入仍等于原 question 时清空。失败时也捕获原 question/topic/card 以构造重试。请求重新执行时读取 Store.Settings.Provider 与其凭据，不捕获旧密钥。

- [ ] **Step 4: 跑自动检查及 UI 停止/重试渲染，检查 BusyPanel、占位和按钮状态一致。**
- [ ] **Step 5: 提交独立取消重试修复。**

```powershell
git add -- src/Leaf/RequestOperations.cs src/Leaf/Popup.cs src/Leaf/AppShell.cs tests/PopupRegressionTests.cs docs/PRODUCT.md docs/USAGE.md docs/USAGE.en.md
git commit -m "fix: retry cancelled operations without replacing conversations"
```

## 批次出口

- [ ] 本批次完整检查通过，受影响 PNG 逐图检查，Codeg 桌面结果单独记录。
- [ ] 报告 C1 的实际阶段和原因；无法兼容的目标如实说明，不宣称所有软件都能取词。
- [ ] 无常驻键盘教学行，无默认抢焦点，无自动置顶或旧剪贴板上传。
- [ ] 审查 git diff 与公开源检查；本批次可独立交付，不等待历史存储改造。

## 浮窗回归辅助代码

`tests/PopupRegressionTests.cs` 的 Run 入口只在已有 ApplicationTests.Scenarios 内 await；每个测试组建立自己的子目录、handler 和 shell，结束时 Dispose。下面成员放进 PopupRegressionTests 类，不访问 ApplicationTests 的私有夹具。每个异步 click 后等待相应操作任务或请求数量变化，不能把 RaiseEvent 当作请求已经完成。

```csharp
private static int assertions;
private static void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAILED: " + label);
    assertions++;
}
private static void Click(Window window, string name)
{
    Ui.Get<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
private static LocalStore ConfiguredStore(string folder)
{
    var store = new LocalStore(folder); var settings = Json.Copy(store.Settings);
    settings.Provider.Model = "fixture-model"; store.SaveSettings(settings);
    return store;
}
private static HttpResponseMessage StreamReply(string text)
{
    string payload = "data: " + Json.Write(new { choices = new[] {
        new { delta = new { content = text } }
    } }) + "\n\ndata: [DONE]\n\n";
    return new HttpResponseMessage(HttpStatusCode.OK) {
        Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
    };
}
private static HttpResponseMessage JsonReply(object card)
{
    return new HttpResponseMessage(HttpStatusCode.OK) {
        Content = new StringContent(Json.Write(new { choices = new[] {
            new { message = new { content = Json.Write(card) }, finish_reason = "stop" }
        } }), Encoding.UTF8, "application/json")
    };
}
private sealed class FixtureHandler : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); Calls++;
        var body = (Dictionary<string, object>)Json.Read(
            request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        bool stream = body.ContainsKey("stream") && Convert.ToBoolean(body["stream"]);
        return Task.FromResult(stream ? StreamReply("夹具译文") : JsonReply(new {
            lemma = "fixture", part_of_speech = "noun", meaning = "夹具词义",
            target_phrase = "夹具", sections = new[] {
                new { title = "词形", content = "仅供隔离测试" }
            }
        }));
    }
}
private sealed class PendingResponse
{
    public Uri Uri; public string Body; public CancellationToken Cancellation;
    public readonly TaskCompletionSource<HttpResponseMessage> Completion =
        new TaskCompletionSource<HttpResponseMessage>();
}
private sealed class ControlledHandler : HttpMessageHandler
{
    public readonly List<PendingResponse> Requests = new List<PendingResponse>();
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellation)
    {
        var pending = new PendingResponse {
            Uri = request.RequestUri,
            Body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
            Cancellation = cancellation
        };
        Requests.Add(pending); return pending.Completion.Task;
    }
    public void ReplyStream(int index, string text)
    { Requests[index].Completion.TrySetResult(StreamReply(text)); }
    public void ReplyJson(int index, object card)
    { Requests[index].Completion.TrySetResult(JsonReply(card)); }
}
```

ControlledHandler 刻意保留迟到响应，模拟已取消但服务仍回包：Stop 后先检查 token 已取消与重试请求类型，再 Reply 对应的旧请求，await 旧任务结束，确认没有覆盖新状态；所有 PendingResponse 最终必须完成，不能让测试靠30秒超时退出。Run 开头重置 assertions，最后返回断言数；第一次用 FixtureHandler 做 Task 3，Task 5 单独新建 ControlledHandler 和已翻译 Current，不在两个 handler 之间交换在途请求。
