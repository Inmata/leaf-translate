using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Leaf
{
    // Every capture ends in exactly one of these states. "Unavailable" is never a
    // confirmed empty selection and never falls back to whatever the clipboard held.
    public enum CaptureStatus { Text, Empty, Unavailable, Failed, Cancelled }

    // Typed reasons only: no free text ever reaches diagnostics.
    public enum CaptureReason
    {
        None, Desktop, EmptySelection, UnsupportedPattern, AutomationTimeout,
        AutomationBusy, AutomationBudget, AutomationUnreadable, ForegroundChanged, ModifiersHeld, CopyNoUpdate,
        // A direct clipboard-mode read found no text. That is what the clipboard held, not a
        // statement about any selection, so it is never the copy path's "no update".
        ClipboardEmpty,
        ClipboardOriginUnknown,
        ClipboardBusy, CaptureBusy, InputDenied, SystemFailure, Cancelled,
        // The focused element belongs to another process and could not be shown to be part of
        // the captured window. The foreground never changed, so this is not one.
        OwnershipUnproven,
        // The keyboard focus the simulated copy would reach is not inside the captured window.
        FocusNotOwned
    }

    // The window this invocation was started for, taken before the popup is shown.
    public sealed class CaptureTarget
    {
        public IntPtr Window { get; set; }
        public uint ProcessId { get; set; }
        public bool IsDesktop { get; set; }
        public bool IsLeaf { get; set; }
    }

    // The fields one stage actually checked. A stage logs only what it looked at: a copy
    // stage that never asked about modifiers must not emit "modifiers_released:false" -
    // an absent field reads as unknown instead of a misleading checked-and-false.
    [Flags]
    public enum CaptureFields
    {
        None = 0,
        Foreground = 1,   // foreground_same
        Clipboard = 2,    // clipboard_changed
        Modifiers = 4,    // modifiers_released
        Focus = 8,        // focus_checked, focus_owned
        Owner = 16,       // clipboard_owner_process, owner_window_verified
        Read = 32         // candidate_count, control_type_id, pattern_supported
    }

    // One stage of one invocation. Stage names come from a fixed whitelist in DiagnosticLog.
    public sealed class CaptureStep
    {
        public string Stage { get; set; }
        public CaptureFields Checked { get; set; }
        public CaptureReason Reason { get; set; }
        public int CandidateCount { get; set; }
        public int ControlTypeId { get; set; }
        public bool PatternSupported { get; set; }
        public bool ForegroundSame { get; set; }
        public bool ClipboardChanged { get; set; }
        public bool ModifiersReleased { get; set; }
        // The window a simulated copy would reach: was it checked, and was it inside the
        // captured window? Metadata only - no title, name or value is ever read.
        public bool FocusChecked { get; set; }
        public bool FocusOwned { get; set; }
        // The clipboard owner's process, and whether its window was verified as part of the
        // captured one. Metadata only: nothing is read from the owning process.
        public uint ClipboardOwnerProcess { get; set; }
        public bool OwnerWindowVerified { get; set; }
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
        // A shortcut that does not parse is dropped rather than written as free text.
        public static CaptureTelemetry For(string id, CaptureTarget target, bool clipboardMode, string shortcut, SelectionCaptureResult result)
        {
            string validated = "";
            try { HotkeySpec.Parse(shortcut); validated = shortcut ?? ""; } catch (Exception) { }
            return new CaptureTelemetry {
                Id = id, Shortcut = validated, ClipboardMode = clipboardMode,
                ForegroundProcessId = target == null ? 0u : target.ProcessId,
                Status = result == null ? CaptureStatus.Failed : result.Status,
                Reason = result == null ? CaptureReason.SystemFailure : result.Reason,
                TextLength = result == null || result.Text == null ? 0 : result.Text.Length,
                Steps = result == null || result.Steps == null ? new List<CaptureStep>() : new List<CaptureStep>(result.Steps)
            };
        }
    }

    public sealed class SelectionCaptureResult
    {
        public CaptureStatus Status { get; private set; }
        public CaptureReason Reason { get; private set; }
        public string Text { get; private set; }
        public List<CaptureStep> Steps { get; private set; }
        public static SelectionCaptureResult Success(string text)
        {
            return new SelectionCaptureResult {
                Status = CaptureStatus.Text, Reason = CaptureReason.None, Text = text ?? "",
                Steps = new List<CaptureStep>()
            };
        }
        public static SelectionCaptureResult NoText(CaptureStatus status, CaptureReason reason)
        {
            if (status == CaptureStatus.Text) throw new ArgumentException("status");
            return new SelectionCaptureResult { Status = status, Reason = reason, Text = "", Steps = new List<CaptureStep>() };
        }
        // The probe-facing factory: same contract as Success/NoText but it carries the
        // stages the probe recorded, so a fixture can produce real metadata.
        public static SelectionCaptureResult Create(CaptureStatus status, CaptureReason reason, string text, List<CaptureStep> steps)
        {
            if (status != CaptureStatus.Text) text = "";
            return new SelectionCaptureResult {
                Status = status, Reason = reason, Text = text ?? "",
                Steps = steps ?? new List<CaptureStep>()
            };
        }
    }

    // Implemented by WindowsSelectionProbe in production and by a fixture in tests.
    public interface ISelectionProbe
    {
        CaptureTarget Snapshot();
        bool IsCurrent(CaptureTarget target);
        Task<SelectionCaptureResult> ReadAutomationAsync(CaptureTarget target, CancellationToken cancellation);
        Task<SelectionCaptureResult> CopySelectionAsync(CaptureTarget target, CancellationToken cancellation);
        Task<SelectionCaptureResult> ReadClipboardAsync(CaptureTarget target, CancellationToken cancellation);
    }

    // Short, concrete feedback for a definite failure, plus the on-demand hint used by
    // the source badge when a capture is merely inconclusive.
    public static class CaptureMessages
    {
        public static string For(CaptureReason reason)
        {
            switch (reason) {
                case CaptureReason.InputDenied: return "当前程序不允许读取选区，请手动复制后使用剪贴板模式。";
                case CaptureReason.ClipboardBusy: return "剪贴板暂时不可用，可稍后再试。";
                default: return "取词未完成，可重试或手动输入。";
            }
        }
        // Shown on demand from the source badge, never as a permanent line.
        public static string Hint(CaptureReason reason)
        {
            string detail;
            switch (reason) {
                case CaptureReason.ClipboardBusy: detail = "剪贴板暂时不可用。"; break;
                case CaptureReason.CopyNoUpdate: detail = "没有读到可用的文字。"; break;
                case CaptureReason.ClipboardEmpty: detail = "剪贴板里没有文字。"; break;
                case CaptureReason.ModifiersHeld: detail = "按键还没有松开，请松开快捷键后重试。"; break;
                case CaptureReason.ForegroundChanged: detail = "前台窗口已经变化，取词已取消。"; break;
                case CaptureReason.AutomationBusy: detail = "上一次取词还没有结束。"; break;
                case CaptureReason.AutomationTimeout: detail = "读取选中文字超时。"; break;
                case CaptureReason.ClipboardOriginUnknown: detail = "无法确认剪贴板来源，未使用其中内容。"; break;
                case CaptureReason.InputDenied: detail = "当前程序不允许读取选区。"; break;
                case CaptureReason.OwnershipUnproven: detail = "焦点不在取词窗口内，没有读取其他窗口的内容。"; break;
                case CaptureReason.FocusNotOwned: detail = "键盘焦点已经不在取词窗口中。"; break;
                default: detail = "可直接输入要翻译的文字。"; break;
            }
            return "未取得选区：" + detail;
        }
    }

    // Decides the order of attempts and turns every outcome into a typed result.
    // The probe owns the platform detail; this type owns the policy.
    public sealed class SelectionAcquirer
    {
        public const int AutomationBudgetMs = 250;
        private readonly ISelectionProbe probe;
        public SelectionAcquirer(ISelectionProbe selectionProbe)
        {
            if (selectionProbe == null) throw new ArgumentNullException("selectionProbe");
            probe = selectionProbe;
        }
        public CaptureTarget Snapshot() { return probe.Snapshot(); }

        public async Task<SelectionCaptureResult> CaptureAsync(CaptureTarget target, bool clipboardMode, string shortcut, CancellationToken cancellation)
        {
            // The stage list is owned here so every exit still reports what was already
            // known: a cancelled or thrown worker must not reduce the diagnosis to nothing.
            var steps = new List<CaptureStep>();
            try { return await CaptureCoreAsync(target, clipboardMode, shortcut, cancellation, steps); }
            catch (OperationCanceledException) {
                return SelectionCaptureResult.Create(CaptureStatus.Cancelled, CaptureReason.Cancelled, "", steps);
            } catch (Exception error) {
                // Only the HResult is kept; the message never becomes free text in the log.
                steps.Add(new CaptureStep { Stage = "result", Reason = CaptureReason.SystemFailure, HResult = error.HResult });
                return SelectionCaptureResult.Create(CaptureStatus.Failed, CaptureReason.SystemFailure, "", steps);
            }
        }

        private async Task<SelectionCaptureResult> CaptureCoreAsync(CaptureTarget target, bool clipboardMode, string shortcut,
            CancellationToken cancellation, List<CaptureStep> steps)
        {
            cancellation.ThrowIfCancellationRequested();
            HotkeySpec.Parse(shortcut); // Rejects free text before it could reach diagnostics.
            var watch = Stopwatch.StartNew();
            long mark = 0;
            bool current = probe.IsCurrent(target);
            mark = watch.ElapsedMilliseconds;
            steps.Add(new CaptureStep { Stage = "snapshot", Reason = CaptureReason.None, Checked = CaptureFields.Foreground,
                ForegroundSame = current, ElapsedMs = mark });

            if (clipboardMode) {
                var fromClipboard = await probe.ReadClipboardAsync(target, cancellation);
                return Adopt(fromClipboard, steps);
            }
            if (target != null && target.IsDesktop)
                return SelectionCaptureResult.Create(CaptureStatus.Empty, CaptureReason.Desktop, "", steps);
            if (!current)
                return SelectionCaptureResult.Create(CaptureStatus.Unavailable, CaptureReason.ForegroundChanged, "", steps);

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                var automationWatch = Stopwatch.StartNew();
                var automation = probe.ReadAutomationAsync(target, deadline.Token);
                var first = await Task.WhenAny(automation, Task.Delay(AutomationBudgetMs, cancellation));
                cancellation.ThrowIfCancellationRequested();
                // The foreground can move while UIA is querying; never inject a copy then.
                if (!probe.IsCurrent(target)) {
                    deadline.Cancel(); Abandon(automation);
                    steps.Add(new CaptureStep { Stage = "uia", Reason = CaptureReason.ForegroundChanged,
                        Checked = CaptureFields.Foreground, ForegroundSame = false, ElapsedMs = automationWatch.ElapsedMilliseconds });
                    return SelectionCaptureResult.Create(CaptureStatus.Unavailable, CaptureReason.ForegroundChanged, "", steps);
                }
                if (first == automation) {
                    var result = await automation;
                    bool concluded = result != null &&
                        (result.Status == CaptureStatus.Text || result.Status == CaptureStatus.Empty || result.Status == CaptureStatus.Cancelled);
                    // The probe owns the UIA stage detail: keep it either way, so an
                    // inconclusive automation answer is still diagnosable after a copy.
                    if (result != null && result.Steps != null && result.Steps.Count > 0) Merge(steps, result.Steps);
                    else steps.Add(new CaptureStep {
                        Stage = "uia", Reason = result == null ? CaptureReason.SystemFailure : result.Reason,
                        Checked = CaptureFields.Foreground | CaptureFields.Read,
                        PatternSupported = concluded, ForegroundSame = true, ElapsedMs = automationWatch.ElapsedMilliseconds
                    });
                    if (concluded) return SelectionCaptureResult.Create(result.Status, result.Reason, result.Text, new List<CaptureStep>(steps));
                } else {
                    // The worker keeps its own lifetime; revoke its token and observe its fault.
                    deadline.Cancel(); Abandon(automation);
                    steps.Add(new CaptureStep { Stage = "uia", Reason = CaptureReason.AutomationTimeout, ForegroundSame = true, ElapsedMs = automationWatch.ElapsedMilliseconds });
                }
                cancellation.ThrowIfCancellationRequested();
                if (!probe.IsCurrent(target)) {
                    long now = watch.ElapsedMilliseconds;
                    steps.Add(new CaptureStep { Stage = "copy_wait", Reason = CaptureReason.ForegroundChanged,
                        Checked = CaptureFields.Foreground, ForegroundSame = false, ElapsedMs = now - mark });
                    return SelectionCaptureResult.Create(CaptureStatus.Unavailable, CaptureReason.ForegroundChanged, "", steps);
                }
                var copied = await probe.CopySelectionAsync(target, cancellation);
                return Adopt(copied, steps);
            }
        }

        private static void Merge(List<CaptureStep> target, List<CaptureStep> extra)
        {
            if (extra == null) return;
            foreach (var step in extra) if (step != null) target.Add(step);
        }

        private static SelectionCaptureResult Adopt(SelectionCaptureResult result, List<CaptureStep> steps)
        {
            if (result == null)
                return SelectionCaptureResult.Create(CaptureStatus.Failed, CaptureReason.SystemFailure, "", new List<CaptureStep>(steps));
            Merge(steps, result.Steps);
            return SelectionCaptureResult.Create(result.Status, result.Reason, result.Text, new List<CaptureStep>(steps));
        }

        // A worker that finishes after its deadline must not surface as an unobserved fault.
        private static void Abandon(Task<SelectionCaptureResult> task)
        {
            if (task == null) return;
            task.ContinueWith(t => { var ignored = t.Exception; },
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
