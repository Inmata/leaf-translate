using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Leaf;

// Typed capture results and content-free stage diagnostics.
internal static class SelectionTests
{
    private static int assertions;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAILED: " + label);
        assertions++;
        Console.WriteLine("PASS " + label);
    }
    public static async Task<int> Run()
    {
        assertions = 0;
        int count = 0;
        var probe = new FakeSelectionProbe();
        var reader = new SelectionAcquirer(probe);

        // A document that positively reports an empty selection is a real answer.
        probe.Automation = SelectionCaptureResult.NoText(CaptureStatus.Empty, CaptureReason.EmptySelection);
        var empty = await reader.CaptureAsync(probe.Snapshot(), false, "Alt+Space", CancellationToken.None);
        if (empty.Status != CaptureStatus.Empty || probe.CopyCalls != 0)
            throw new Exception("Confirmed empty document must skip copy fallback.");
        count++;

        // Unknown UIA capability is not proof of anything, so one fresh copy is attempted.
        probe.Automation = SelectionCaptureResult.NoText(CaptureStatus.Unavailable, CaptureReason.UnsupportedPattern);
        probe.Copy = SelectionCaptureResult.Success("fresh fixture");
        var fresh = await reader.CaptureAsync(probe.Snapshot(), false, "Ctrl+Alt+T", CancellationToken.None);
        if (fresh.Status != CaptureStatus.Text || fresh.Text != "fresh fixture")
            throw new Exception("Unsupported UIA must attempt one fresh copy.");
        count++;

        probe.Copy = SelectionCaptureResult.NoText(CaptureStatus.Unavailable, CaptureReason.CopyNoUpdate);
        var unavailable = await reader.CaptureAsync(probe.Snapshot(), false, "Ctrl+Shift+T", CancellationToken.None);
        if (unavailable.Status != CaptureStatus.Unavailable)
            throw new Exception("No copy update is not proof of an empty selection.");
        count++;

        probe.Current = false;
        int before = probe.CopyCalls;
        var moved = await reader.CaptureAsync(probe.Snapshot(), false, "Alt+Space", CancellationToken.None);
        if (moved.Reason != CaptureReason.ForegroundChanged || probe.CopyCalls != before)
            throw new Exception("Foreground change must prevent injected copy.");
        count++;

        probe.Current = true;
        Check(fresh.Text != null && unavailable.Text == "" && empty.Text == "",
            "Only a real capture carries text");
        Check(fresh.Reason == CaptureReason.None && unavailable.Reason == CaptureReason.CopyNoUpdate,
            "Reasons stay typed instead of becoming free text");

        // Clipboard mode never runs UIA or simulated copy.
        probe.AutomationCalls = probe.CopyCalls = probe.ClipboardCalls = 0;
        probe.Clipboard = SelectionCaptureResult.Success("clipboard fixture");
        var clipboard = await reader.CaptureAsync(probe.Snapshot(), true, "Ctrl+Alt+T", CancellationToken.None);
        Check(clipboard.Status == CaptureStatus.Text && clipboard.Text == "clipboard fixture" &&
            probe.AutomationCalls == 0 && probe.CopyCalls == 0 && probe.ClipboardCalls == 1,
            "Clipboard mode reads the clipboard and nothing else");

        // Desktop and taskbar are a confirmed empty target rather than a failure.
        var desktopTarget = probe.Snapshot();
        desktopTarget.IsDesktop = true;
        probe.AutomationCalls = probe.CopyCalls = 0;
        var desktop = await reader.CaptureAsync(desktopTarget, false, "Ctrl+Alt+T", CancellationToken.None);
        Check(desktop.Status == CaptureStatus.Empty && desktop.Reason == CaptureReason.Desktop &&
            probe.AutomationCalls == 0 && probe.CopyCalls == 0,
            "A desktop target prepares manual input without touching the clipboard");
        desktopTarget.IsDesktop = false;

        // A pre-cancelled invocation is Cancelled and produces no side effect at all.
        probe.AutomationCalls = probe.CopyCalls = probe.ClipboardCalls = 0;
        using (var cancelled = new CancellationTokenSource()) {
            cancelled.Cancel();
            var stopped = await reader.CaptureAsync(probe.Snapshot(), false, "Ctrl+Alt+T", cancelled.Token);
            Check(stopped.Status == CaptureStatus.Cancelled && stopped.Reason == CaptureReason.Cancelled &&
                probe.AutomationCalls == 0 && probe.CopyCalls == 0,
                "A cancelled invocation is reported as Cancelled without side effects");
        }

        // A probe that reports cancellation is passed through rather than retried.
        probe.Automation = SelectionCaptureResult.NoText(CaptureStatus.Cancelled, CaptureReason.Cancelled);
        probe.Copy = SelectionCaptureResult.Success("must not be copied");
        int copiesBefore = probe.CopyCalls;
        var probeCancelled = await reader.CaptureAsync(probe.Snapshot(), false, "Ctrl+Alt+T", CancellationToken.None);
        Check(probeCancelled.Status == CaptureStatus.Cancelled && probe.CopyCalls == copiesBefore,
            "A cancelled automation probe is not retried through the clipboard");

        // A late UIA worker is abandoned, its deadline token is revoked and its result is ignored.
        probe.AutomationPending = new TaskCompletionSource<SelectionCaptureResult>();
        probe.AutomationToken = CancellationToken.None;
        probe.Automation = SelectionCaptureResult.NoText(CaptureStatus.Unavailable, CaptureReason.UnsupportedPattern);
        probe.Copy = SelectionCaptureResult.NoText(CaptureStatus.Unavailable, CaptureReason.CopyNoUpdate);
        var pending = reader.CaptureAsync(probe.Snapshot(), false, "Ctrl+Alt+T", CancellationToken.None);
        await Task.Delay(450);
        probe.AutomationPending.TrySetResult(SelectionCaptureResult.Success("late fixture"));
        var late = await pending;
        Check(late.Status == CaptureStatus.Unavailable && late.Text == "" &&
            late.Steps.Any(s => s.Stage == "uia" && s.Reason == CaptureReason.AutomationTimeout),
            "A late automation answer cannot become this invocation's text");
        Check(probe.AutomationToken.IsCancellationRequested,
            "Timing out the automation wait revokes the worker's deadline token");
        probe.AutomationPending = null;

        // An invalid shortcut is rejected before anything reads the desktop.
        probe.AutomationCalls = probe.CopyCalls = probe.ClipboardCalls = 0;
        var invalid = await reader.CaptureAsync(probe.Snapshot(), false, "not-a-shortcut", CancellationToken.None);
        Check(invalid.Status == CaptureStatus.Failed && probe.AutomationCalls == 0 && probe.CopyCalls == 0,
            "An unvalidated shortcut never reaches capture or the log");

        await Diagnostics(reader, probe);
        return count + assertions;
    }

    // The log records fixed stage names and typed reasons only, never content.
    private static Task Diagnostics(SelectionAcquirer reader, FakeSelectionProbe probe)
    {
        string folder = Path.Combine(Path.GetTempPath(), "leaf-capture-" + Guid.NewGuid().ToString("N"));
        try {
            var log = new DiagnosticLog(folder);
            probe.Current = true;
            var captured = SelectionCaptureResult.Success("private-selection-fixture");
            captured.Steps.Add(new CaptureStep {
                Stage = "uia", Reason = CaptureReason.None, CandidateCount = 3,
                ControlTypeId = 50030, PatternSupported = true, ForegroundSame = true, ElapsedMs = 12
            });
            captured.Steps.Add(new CaptureStep { Stage = "private-selection-fixture", Reason = CaptureReason.None });
            log.Capture(CaptureTelemetry.For("fixture-capture", probe.Snapshot(), false, "Ctrl+Alt+T", captured));

            string entries = string.Concat(Directory.GetFiles(log.Folder).Select(File.ReadAllText));
            Check(entries.Contains("capture_id") && entries.Contains("fixture-capture"), "Capture diagnostics carry a capture id");
            Check(entries.Contains("\"stage\":\"uia\"") && entries.Contains("candidate_count") && entries.Contains("pattern_supported"),
                "Capture diagnostics record a fixed stage with typed detail");
            Check(entries.Contains("\"reason\":\"None\"") && entries.Contains("\"foreground_same\":true"),
                "Capture reasons and flags are emitted as typed values");
            Check(entries.Contains("\"status\":\"Text\"") && entries.Contains("text_length"),
                "The final capture result records status and text length");
            Check(!entries.Contains("private-selection-fixture"),
                "Neither selection text nor unknown stage names can enter the log");
            Check(Directory.GetFiles(log.Folder).SelectMany(File.ReadAllLines).All(x => Json.Read(x) is Dictionary<string, object>),
                "Capture diagnostics remain complete JSON records");

            var busy = new DiagnosticLog(folder);
            var failed = SelectionCaptureResult.NoText(CaptureStatus.Failed, CaptureReason.InputDenied);
            busy.Capture(CaptureTelemetry.For("fixture-failed", probe.Snapshot(), false, "Ctrl+Alt+T", failed));
            string failedEntries = string.Concat(Directory.GetFiles(folder).Select(File.ReadAllText));
            Check(failedEntries.Contains("capture_failed") && failedEntries.Contains("InputDenied"),
                "Only a definite system failure is logged as capture_failed");
            return Task.FromResult(0);
        } finally {
            if (Directory.Exists(folder) && Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(folder, true);
        }
    }
}

// Shared by the application-level capture checks: no OS side effects, fully scripted answers.
internal sealed class FakeSelectionProbe : ISelectionProbe
{
    public CaptureTarget Target = new CaptureTarget { Window = new IntPtr(123), ProcessId = 456 };
    public SelectionCaptureResult Automation = SelectionCaptureResult.NoText(CaptureStatus.Unavailable, CaptureReason.UnsupportedPattern);
    public SelectionCaptureResult Copy = SelectionCaptureResult.NoText(CaptureStatus.Unavailable, CaptureReason.CopyNoUpdate);
    public SelectionCaptureResult Clipboard = SelectionCaptureResult.Success("clipboard fixture");
    public TaskCompletionSource<SelectionCaptureResult> AutomationPending;
    public CancellationToken AutomationToken;
    public bool Current = true;
    public int AutomationCalls, CopyCalls, ClipboardCalls;
    public CaptureTarget Snapshot() { return Target; }
    public bool IsCurrent(CaptureTarget target) { return Current; }
    public Task<SelectionCaptureResult> ReadAutomationAsync(CaptureTarget target, CancellationToken cancellation)
    {
        AutomationCalls++; AutomationToken = cancellation;
        return AutomationPending == null ? Task.FromResult(Automation) : AutomationPending.Task;
    }
    public Task<SelectionCaptureResult> CopySelectionAsync(CaptureTarget target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        CopyCalls++; return Task.FromResult(Copy);
    }
    public Task<SelectionCaptureResult> ReadClipboardAsync(CaptureTarget target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ClipboardCalls++; return Task.FromResult(Clipboard);
    }
}
