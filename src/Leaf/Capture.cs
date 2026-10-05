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
        AutomationBusy, AutomationBudget, ForegroundChanged, ModifiersHeld, CopyNoUpdate,
        ClipboardOriginUnknown,
        ClipboardBusy, CaptureBusy, InputDenied, SystemFailure, Cancelled
    }

    // The window this invocation was started for, taken before the popup is shown.
    public sealed class CaptureTarget
    {
        public IntPtr Window { get; set; }
        public uint ProcessId { get; set; }
        public bool IsDesktop { get; set; }
        public bool IsLeaf { get; set; }
    }

    // One stage of one invocation. Stage names come from a fixed whitelist in DiagnosticLog.
    public sealed class CaptureStep
    {
        public string Stage { get; set; }
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
        internal static SelectionCaptureResult Create(CaptureStatus status, CaptureReason reason, string text, List<CaptureStep> steps)
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
            try { return await CaptureCoreAsync(target, clipboardMode, shortcut, cancellation); }
            catch (OperationCanceledException) { return SelectionCaptureResult.NoText(CaptureStatus.Cancelled, CaptureReason.Cancelled); }
            catch (Exception) { return SelectionCaptureResult.NoText(CaptureStatus.Failed, CaptureReason.SystemFailure); }
        }

        private async Task<SelectionCaptureResult> CaptureCoreAsync(CaptureTarget target, bool clipboardMode, string shortcut, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            HotkeySpec.Parse(shortcut); // Rejects free text before it could reach diagnostics.
            var steps = new List<CaptureStep>();
            var watch = Stopwatch.StartNew();
            bool current = probe.IsCurrent(target);
            steps.Add(new CaptureStep { Stage = "snapshot", Reason = CaptureReason.None, ForegroundSame = current, ElapsedMs = watch.ElapsedMilliseconds });

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
                    steps.Add(new CaptureStep { Stage = "uia", Reason = CaptureReason.ForegroundChanged, ForegroundSame = false, ElapsedMs = automationWatch.ElapsedMilliseconds });
                    return SelectionCaptureResult.Create(CaptureStatus.Unavailable, CaptureReason.ForegroundChanged, "", steps);
                }
                if (first == automation) {
                    var result = await automation;
                    bool concluded = result != null &&
                        (result.Status == CaptureStatus.Text || result.Status == CaptureStatus.Empty || result.Status == CaptureStatus.Cancelled);
                    if (result != null && (result.Steps == null || result.Steps.Count == 0))
                        steps.Add(new CaptureStep {
                            Stage = "uia", Reason = result.Reason, PatternSupported = concluded,
                            ForegroundSame = true, ElapsedMs = automationWatch.ElapsedMilliseconds
                        });
                    if (concluded) return Adopt(result, steps);
                } else {
                    // The worker keeps its own lifetime; revoke its token and observe its fault.
                    deadline.Cancel(); Abandon(automation);
                    steps.Add(new CaptureStep { Stage = "uia", Reason = CaptureReason.AutomationTimeout, ForegroundSame = true, ElapsedMs = automationWatch.ElapsedMilliseconds });
                }
                cancellation.ThrowIfCancellationRequested();
                if (!probe.IsCurrent(target)) {
                    steps.Add(new CaptureStep { Stage = "copy_wait", Reason = CaptureReason.ForegroundChanged, ForegroundSame = false, ElapsedMs = watch.ElapsedMilliseconds });
                    return SelectionCaptureResult.Create(CaptureStatus.Unavailable, CaptureReason.ForegroundChanged, "", steps);
                }
                var copied = await probe.CopySelectionAsync(target, cancellation);
                return Adopt(copied, steps);
            }
        }

        private static SelectionCaptureResult Adopt(SelectionCaptureResult result, List<CaptureStep> steps)
        {
            if (result == null)
                return SelectionCaptureResult.Create(CaptureStatus.Failed, CaptureReason.SystemFailure, "", new List<CaptureStep>(steps));
            if (result.Steps != null) foreach (var step in result.Steps) if (step != null) steps.Add(step);
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
