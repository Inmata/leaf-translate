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

        await StagesRetained(reader, probe);
        await Diagnostics(reader, probe);
        CopyStateMachine();
        await CopyWorker();
        ClipboardBackups();
        await BoundedTraversal();
        return count + assertions;
    }

    // The real bounded traversal runs against a scripted tree, so the limits it is supposed
    // to apply are exercised by the production algorithm rather than by a fake probe.
    private static async Task BoundedTraversal()
    {
        var target = new CaptureTarget { Window = new IntPtr(123), ProcessId = 456 };

        // A window with far more siblings than the node budget may ever visit.
        var wide = new ScriptedUiTree();
        var wideRoot = wide.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        for (int i = 0; i < 5000; i++) wideRoot.Add(new ScriptedUiNode { Selection = null });
        wide.Focus = new ScriptedUiNode { ControlTypeIdValue = 50004, Selection = "" };
        var wideResult = await new WindowsSelectionProbe(wide).ReadAutomationAsync(target, CancellationToken.None);
        Check(wideResult.Status == CaptureStatus.Unavailable && wideResult.Reason == CaptureReason.AutomationBudget,
            "A window too large for the budget is inconclusive instead of confirmed empty (was " + wideResult.Status + "/" + wideResult.Reason + ")");
        Check(wide.TotalReads <= WindowsSelectionProbe.MaxNodes && wide.SiblingCalls <= WindowsSelectionProbe.MaxNodes,
            "A sibling list is never walked past the node budget (reads " + wide.TotalReads + ", siblings " + wide.SiblingCalls + ")");

        // Cancelling between two TreeWalker calls must stop the walk before it enumerates on.
        var cancelTree = new ScriptedUiTree();
        var cancelRoot = cancelTree.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        for (int i = 0; i < 5000; i++) cancelRoot.Add(new ScriptedUiNode { Selection = null });
        cancelTree.Focus = new ScriptedUiNode { ControlTypeIdValue = 50004, Selection = "" };
        using (var cancelled = new CancellationTokenSource()) {
            cancelTree.OnRead = node => { if (cancelTree.TotalReads >= 2) cancelled.Cancel(); };
            var cancelResult = await new WindowsSelectionProbe(cancelTree).ReadAutomationAsync(target, cancelled.Token);
            Check(cancelResult.Status == CaptureStatus.Cancelled && cancelTree.SiblingCalls == 0,
                "Cancelling a walk stops it before it can enumerate the next sibling list");
        }

        // The walk stays inside the captured window: a same-process window above it is never read.
        var neighbour = new ScriptedUiTree();
        var otherDocument = new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "another window's selection", WindowHandle = 999 };
        var captured = neighbour.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        otherDocument.Add(captured);
        neighbour.Focus = captured.Add(new ScriptedUiNode { ControlTypeIdValue = 50004, Selection = "" });
        var neighbourResult = await new WindowsSelectionProbe(neighbour).ReadAutomationAsync(target, CancellationToken.None);
        Check(neighbourResult.Status != CaptureStatus.Text && neighbourResult.Text == "" && otherDocument.Reads == 0,
            "Ancestors are never walked past the captured window into another window of the same process");

        // The focused editor is empty, but the reading document in the same window is not.
        var sameWindow = new ScriptedUiTree();
        var sameRoot = sameWindow.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        var document = sameRoot.Add(new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "document selection" });
        sameWindow.Focus = sameRoot.Add(new ScriptedUiNode { ControlTypeIdValue = 50004, Selection = "" });
        var sameResult = await new WindowsSelectionProbe(sameWindow).ReadAutomationAsync(target, CancellationToken.None);
        Check(sameResult.Status == CaptureStatus.Text && sameResult.Text == "document selection",
            "An empty focused editor does not hide the selection of the window's document");
        var sameStep = sameResult.Steps.First(s => s.Stage == "uia");
        Check(sameStep.ControlTypeId == WindowsSelectionProbe.DocumentTypeId && sameStep.PatternSupported && sameStep.CandidateCount >= 2,
            "The UIA stage names the control type that supplied the text");
        Check(document.Reads == 1, "The document that answered was read exactly once");

        // An empty document that is an ancestor of the focused editor answers the same way.
        var ancestorTree = new ScriptedUiTree();
        var ancestorDocument = ancestorTree.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "ancestor selection", WindowHandle = 123 };
        ancestorTree.Focus = ancestorDocument.Add(new ScriptedUiNode { ControlTypeIdValue = 50004, Selection = "" });
        var ancestorResult = await new WindowsSelectionProbe(ancestorTree).ReadAutomationAsync(target, CancellationToken.None);
        Check(ancestorResult.Status == CaptureStatus.Text && ancestorResult.Text == "ancestor selection",
            "The focused element's own document is read before the window is scanned");

        // A scan that ran out of budget must not conclude "nothing selected" from the one
        // empty document it happened to reach first.
        var cutTree = new ScriptedUiTree();
        var cutRoot = cutTree.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        cutRoot.Add(new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "" });
        for (int i = 0; i < 5000; i++) cutRoot.Add(new ScriptedUiNode { Selection = null });
        cutTree.Focus = cutRoot;
        var cutResult = await new WindowsSelectionProbe(cutTree).ReadAutomationAsync(target, CancellationToken.None);
        Check(cutResult.Status == CaptureStatus.Unavailable && cutResult.Reason == CaptureReason.AutomationBudget,
            "A truncated scan reports an inconclusive capture rather than an empty selection (was " + cutResult.Status + "/" + cutResult.Reason + ")");

        // A small window that really was scanned completely still reports a confirmed empty.
        var emptyTree = new ScriptedUiTree();
        var emptyRoot = emptyTree.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        emptyRoot.Add(new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "" });
        emptyTree.Focus = emptyRoot.Add(new ScriptedUiNode { ControlTypeIdValue = 50004, Selection = "" });
        var emptyResult = await new WindowsSelectionProbe(emptyTree).ReadAutomationAsync(target, CancellationToken.None);
        Check(emptyResult.Status == CaptureStatus.Empty && emptyResult.Reason == CaptureReason.EmptySelection &&
            emptyResult.Steps.First(s => s.Stage == "uia").PatternSupported,
            "A completely scanned window still reports a confirmed empty selection");

        // Foreground metadata is a real comparison, not "some foreground window exists".
        Check(!WindowsSelectionProbe.ForegroundIs(new CaptureTarget { Window = new IntPtr(123) }),
            "A window that is not in front is never reported as the same foreground");
        Check(!WindowsSelectionProbe.ForegroundIs(null) && !WindowsSelectionProbe.ForegroundIs(new CaptureTarget()),
            "A missing capture target is never reported as the same foreground");
        Check(WindowsSelectionProbe.ForegroundIs(new CaptureTarget { Window = Native.GetForegroundWindow() }),
            "The window that really is in front is reported as the same foreground");
        var movedTree = emptyTree;
        movedTree.Foreground = false;
        var movedResult = await new WindowsSelectionProbe(movedTree).ReadAutomationAsync(target, CancellationToken.None);
        Check(!movedResult.Steps.First(s => s.Stage == "uia").ForegroundSame,
            "A moved foreground is reported as a different one instead of merely non-zero");
        movedTree.Foreground = true;
    }

    private static CaptureStep Step(SelectionCaptureResult result, string stage)
    {
        if (result == null || result.Steps == null) return null;
        return result.Steps.FirstOrDefault(s => s != null && s.Stage == stage);
    }

    // The probe's own stage detail must survive every exit: a copy fallback that succeeds,
    // one that finds nothing, a cancelled worker and a thrown system failure. Losing it is
    // what made the reported Codeg failures undiagnosable.
    private static async Task StagesRetained(SelectionAcquirer reader, FakeSelectionProbe probe)
    {
        probe.Current = true; probe.AutomationPending = null;
        Func<List<CaptureStep>> uiaSteps = () => new List<CaptureStep> {
            new CaptureStep { Stage = "uia", Reason = CaptureReason.UnsupportedPattern, CandidateCount = 7,
                ControlTypeId = 50004, PatternSupported = false, ForegroundSame = true, ElapsedMs = 13 }
        };
        probe.Automation = SelectionCaptureResult.Create(CaptureStatus.Unavailable, CaptureReason.UnsupportedPattern, "", uiaSteps());
        probe.Copy = SelectionCaptureResult.Success("fresh copy");
        var carried = await reader.CaptureAsync(probe.Snapshot(), false, "Ctrl+Alt+T", CancellationToken.None);
        Check(carried.Status == CaptureStatus.Text && carried.Text == "fresh copy",
            "A copied fallback still reports its fresh text");
        Check(Step(carried, "uia") != null && Step(carried, "uia").CandidateCount == 7 &&
            Step(carried, "uia").ControlTypeId == 50004 && Step(carried, "uia").ElapsedMs == 13 &&
            Step(carried, "uia").Reason == CaptureReason.UnsupportedPattern,
            "A successful copy fallback keeps the UIA stage's candidate count, control type and elapsed time");
        Check(Step(carried, "snapshot") != null, "The snapshot stage is kept beside the UIA stage");

        probe.Automation = SelectionCaptureResult.Create(CaptureStatus.Unavailable, CaptureReason.UnsupportedPattern, "", uiaSteps());
        probe.Copy = SelectionCaptureResult.NoText(CaptureStatus.Unavailable, CaptureReason.CopyNoUpdate);
        var carriedEmpty = await reader.CaptureAsync(probe.Snapshot(), false, "Ctrl+Alt+T", CancellationToken.None);
        Check(carriedEmpty.Status == CaptureStatus.Unavailable &&
            Step(carriedEmpty, "uia") != null && Step(carriedEmpty, "uia").CandidateCount == 7,
            "A copy that finds nothing still reports why automation could not conclude");

        probe.Automation = SelectionCaptureResult.Create(CaptureStatus.Unavailable, CaptureReason.AutomationBusy, "",
            new List<CaptureStep> { new CaptureStep { Stage = "uia", Reason = CaptureReason.AutomationBusy, ForegroundSame = true } });
        probe.CopyThrows = new OperationCanceledException();
        var cancelledStages = await reader.CaptureAsync(probe.Snapshot(), false, "Ctrl+Alt+T", CancellationToken.None);
        Check(cancelledStages.Status == CaptureStatus.Cancelled && Step(cancelledStages, "snapshot") != null &&
            Step(cancelledStages, "uia") != null && Step(cancelledStages, "uia").Reason == CaptureReason.AutomationBusy,
            "A cancelled capture keeps the stages it had already recorded");

        var fixtureError = new InvalidOperationException("fixture failure text");
        probe.CopyThrows = fixtureError;
        var failedStages = await reader.CaptureAsync(probe.Snapshot(), false, "Ctrl+Alt+T", CancellationToken.None);
        Check(failedStages.Status == CaptureStatus.Failed && failedStages.Reason == CaptureReason.SystemFailure &&
            Step(failedStages, "snapshot") != null && Step(failedStages, "result") != null &&
            Step(failedStages, "result").HResult == fixtureError.HResult && failedStages.Text == "",
            "A system failure keeps the recorded stages and reports only its HResult");
        Check(Json.Write(new { steps = failedStages.Steps }) != null &&
            !Json.Write(new { steps = failedStages.Steps }).Contains("fixture failure text"),
            "Stages never carry the exception message as free text");
        probe.CopyThrows = null;
    }

    // Reading the old clipboard, trusting another process's copy or restoring over a
    // newer copy are all decided by pure predicates so they can be checked off-desktop.
    private static void CopyStateMachine()
    {
        Check(!WindowsSelectionProbe.CanReadFreshCopy(10, 10, true, false),
            "Old clipboard sequence cannot become selection text");
        Check(!WindowsSelectionProbe.CanReadFreshCopy(10, 11, false, false),
            "Another foreground window cannot supply selection text");
        Check(!WindowsSelectionProbe.CanReadFreshCopy(10, 11, true, true),
            "Cancellation prevents delayed capture side effects");
        Check(WindowsSelectionProbe.CanReadFreshCopy(10, 11, true, false),
            "A current copy can be read");
        Check(!WindowsSelectionProbe.CanRestoreCopy(11, 12, true, true),
            "A newer user copy must survive restoration");
        Check(WindowsSelectionProbe.CanRestoreCopy(11, 11, true, true) && !WindowsSelectionProbe.CanRestoreCopy(11, 11, false, true),
            "The saved clipboard is restored only while its sequence and foreground still match");
        Check(!WindowsSelectionProbe.CanRestoreCopy(11, 11, true, false),
            "The saved clipboard is never restored over a clipboard another process now owns");
        Check(WindowsSelectionProbe.CanKeepBackup(11, 11) && !WindowsSelectionProbe.CanKeepBackup(11, 12) &&
            !WindowsSelectionProbe.CanKeepBackup(0, 0),
            "A saved clipboard is usable only while its own sequence is still the current one");
        Check(!WindowsSelectionProbe.CanTrustOrigin(0, 456) && !WindowsSelectionProbe.CanTrustOrigin(789, 456) &&
            WindowsSelectionProbe.CanTrustOrigin(456, 456),
            "Clipboard content is used only when its owner is the captured process");
    }

    // What the worker is allowed to put back is decided by these rules, and the rule that
    // materialises the old clipboard runs as production code over a scripted format source.
    private static void ClipboardBackups()
    {
        var complete = new ScriptedClipboardFormats("UnicodeText", "HTML Format");
        complete.Values["UnicodeText"] = "old clipboard";
        complete.Values["HTML Format"] = "<b>old</b>";
        var kept = Native.Materialize(complete);
        Check(kept != null && !kept.IsEmpty && complete.Reads.Count == 2,
            "A clipboard whose formats all read is materialised into one restorable backup");

        var partial = new ScriptedClipboardFormats("UnicodeText", "HTML Format");
        partial.Values["UnicodeText"] = "old clipboard";
        Check(Native.Materialize(partial) == null,
            "A clipboard with a format that read as nothing is unknown, not a complete backup");

        var unreadable = new ScriptedClipboardFormats("UnicodeText", "HTML Format");
        unreadable.Values["UnicodeText"] = "old clipboard";
        unreadable.Unreadable.Add("HTML Format");
        Check(Native.Materialize(unreadable) == null,
            "A clipboard with a format that could not be read at all is unknown");

        Check(Native.Materialize(new ScriptedClipboardFormats()) == null && Native.Materialize(null) == null,
            "A clipboard that reports no formats is unknown rather than a verified empty");
    }

    // The production copy worker against a scripted desktop: sequence, ownership,
    // cancellation and restore are checked on the real state machine, not only on a predicate.
    private static async Task CopyWorker()
    {
        var target = new CaptureTarget { Window = new IntPtr(123), ProcessId = 456 };

        // The public entry point runs the same worker against the supplied host.
        var throughWorker = await Native.CopySelectionAsync(target, CancellationToken.None, new ScriptedCopyHost());
        Check(throughWorker.Status == CaptureStatus.Text && throughWorker.Text == "copy fixture",
            "The copy entry point runs the scripted host through the real worker");

        // An old clipboard that could not be read completely is "unknown", never "empty".
        var blind = new ScriptedCopyHost { BackupResult = null };
        using (var deadline = new CancellationTokenSource()) {
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), blind);
            Check(outcome.Status == CaptureStatus.Text && blind.RestoreCalls == 0 && blind.EmptyRestoreCalls == 0,
                "An old clipboard that could not be saved is left alone instead of being cleared");
            Check(Step(outcome, "clipboard_restore") != null && !Step(outcome, "clipboard_restore").ClipboardChanged &&
                Step(outcome, "clipboard_restore").Reason != CaptureReason.None,
                "A restore that was not performed is reported with an honest reason");
        }

        // A clipboard that was read and found empty can be restored as empty.
        var emptyClipboard = new ScriptedCopyHost { BackupResult = ClipboardBackup.Empty() };
        using (var deadline = new CancellationTokenSource()) {
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), emptyClipboard);
            Check(emptyClipboard.RestoreCalls == 1 && emptyClipboard.EmptyRestoreCalls == 1 &&
                Step(outcome, "clipboard_restore").ClipboardChanged && Step(outcome, "clipboard_restore").Reason == CaptureReason.None,
                "A clipboard that was verified empty is put back as empty");
        }

        // Cancelling during the read stops the worker before it writes anything back.
        var slowCancel = new ScriptedCopyHost();
        using (var deadline = new CancellationTokenSource()) {
            slowCancel.OnRead = () => deadline.Cancel();
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), slowCancel);
            Check(outcome.Status == CaptureStatus.Cancelled && slowCancel.RestoreCalls == 0 && slowCancel.EmptyRestoreCalls == 0,
                "A cancelled worker never writes the clipboard back after its deadline");
        }

        // A slow read must not accept what the user copied while it was reading.
        var racing = new ScriptedCopyHost();
        racing.OnRead = () => { racing.Sequence++; racing.Owner = 789; racing.Text = "user's newer copy"; };
        using (var deadline = new CancellationTokenSource()) {
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), racing);
            Check(outcome.Status != CaptureStatus.Text && outcome.Text == "" && racing.RestoreCalls == 0,
                "Text the user copied during the read is discarded instead of becoming the selection");
            Check(Step(outcome, "clipboard_restore") != null && !Step(outcome, "clipboard_restore").ClipboardChanged,
                "A newer user copy is not overwritten by the restore");
        }

        // The owner is part of "this is my copy": a clipboard that changed owner while its
        // text was being read is not this selection, and it is not restored over either.
        var ownerMoved = new ScriptedCopyHost();
        ownerMoved.OnRead = () => { ownerMoved.Owner = 789; };
        using (var deadline = new CancellationTokenSource()) {
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), ownerMoved);
            Check(outcome.Status == CaptureStatus.Unavailable && outcome.Reason == CaptureReason.ClipboardOriginUnknown &&
                outcome.Text == "" && ownerMoved.RestoreCalls == 0,
                "A clipboard that changed owner while it was read is an unknown origin, not this selection");
        }

        // The owner can also change after the text was read. The restore is a write of its own,
        // so it is decided with the owner and sequence the clipboard has at that moment.
        var ownerLost = new ScriptedCopyHost();
        ownerLost.OnOwner = () => { if (ownerLost.OwnerReads == 3) ownerLost.Owner = 789; };
        using (var deadline = new CancellationTokenSource()) {
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), ownerLost);
            Check(outcome.Status == CaptureStatus.Text && outcome.Text == "copy fixture" &&
                ownerLost.RestoreCalls == 0 && ownerLost.EmptyRestoreCalls == 0,
                "A clipboard another process owns by the time of the restore is not overwritten");
            Check(Step(outcome, "clipboard_restore") != null && !Step(outcome, "clipboard_restore").ClipboardChanged &&
                Step(outcome, "clipboard_restore").Reason == CaptureReason.ClipboardOriginUnknown,
                "A restore that was refused reports the unknown origin instead of a success");
        }

        // A definite clipboard read failure is typed, keeps its HResult and still restores.
        var broken = new ScriptedCopyHost { ReadThrows = new System.Runtime.InteropServices.ExternalException("fixture clipboard failure", unchecked((int)0x800401D0)) };
        using (var deadline = new CancellationTokenSource()) {
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), broken);
            Check(outcome.Status == CaptureStatus.Failed && outcome.Reason == CaptureReason.ClipboardBusy &&
                Step(outcome, "clipboard_read") != null && Step(outcome, "clipboard_read").HResult == unchecked((int)0x800401D0),
                "A clipboard read failure is reported as a typed failure with its HResult");
            Check(broken.RestoreCalls == 1, "A failed read still puts back the clipboard this copy replaced");
        }

        // No sequence change at all is "no new copy", not "unknown origin".
        var unchanged = new ScriptedCopyHost { CopyLandsOnSend = false, PauseMs = 0 };
        using (var deadline = new CancellationTokenSource()) {
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), unchanged);
            Check(outcome.Status == CaptureStatus.Unavailable && outcome.Reason == CaptureReason.CopyNoUpdate,
                "A copy that produced no new sequence is reported as no update, not as an unknown clipboard");
        }

        // A new sequence that belongs to another process is unknown, never our text.
        var foreign = new ScriptedCopyHost { Owner = 789, PauseMs = 0 };
        using (var deadline = new CancellationTokenSource()) {
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), foreign);
            Check(outcome.Status == CaptureStatus.Unavailable && outcome.Reason == CaptureReason.ClipboardOriginUnknown &&
                outcome.Text == "" && foreign.RestoreCalls == 0,
                "A clipboard written by another process is never read as this selection");
        }

        // Cancelling while the shortcut keys are still held stops the worker before SendInput.
        var held = new ScriptedCopyHost();
        using (var deadline = new CancellationTokenSource()) {
            held.Modifiers = () => { deadline.Cancel(); return true; };
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), held);
            Check(outcome.Status == CaptureStatus.Cancelled && held.SendCalls == 0,
                "A cancelled worker never injects the simulated copy");
        }

        // A refused input injection is a definite failure with its Win32 error.
        var denied = new ScriptedCopyHost { SendAccepted = false, LastSendError = 5 };
        using (var deadline = new CancellationTokenSource()) {
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), denied);
            Check(outcome.Status == CaptureStatus.Failed && outcome.Reason == CaptureReason.InputDenied &&
                Step(outcome, "copy_send").NativeError == 5,
                "A refused copy injection reports its own Win32 error");
        }

        // The last check before the injected copy reads the token again after the foreground
        // getter, which can block on another application.
        var lateModifiers = new ScriptedCopyHost();
        using (var deadline = new CancellationTokenSource()) {
            lateModifiers.OnForeground = () => { if (lateModifiers.ForegroundReads == 3) deadline.Cancel(); };
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), lateModifiers);
            Check(lateModifiers.SendCalls == 0 && Step(outcome, "copy_send") == null && Step(outcome, "copy_wait") != null,
                "Cancelling during the final foreground check still prevents the injected copy");
            Check(outcome.Status == CaptureStatus.Cancelled && outcome.Text == "",
                "A worker cancelled before injecting reports a cancelled capture");
        }

        // A cancellation that only arrives once the worker is already inside the restore path
        // stops the write: this is a clipboard write of its own, decided with the state it can
        // see at that moment rather than with the state it saw before the call.
        var lateRestore = new ScriptedCopyHost();
        using (var deadline = new CancellationTokenSource()) {
            lateRestore.OnOwner = () => { if (lateRestore.OwnerReads == 3) deadline.Cancel(); };
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), lateRestore);
            Check(lateRestore.RestoreCalls == 0 && lateRestore.EmptyRestoreCalls == 0,
                "A cancellation that arrives inside the restore path writes nothing back");
            Check(Step(outcome, "clipboard_restore") != null && !Step(outcome, "clipboard_restore").ClipboardChanged &&
                Step(outcome, "clipboard_restore").Reason == CaptureReason.Cancelled,
                "The restore the worker entered and declined is recorded as a cancelled restore");
            Check(outcome.Status == CaptureStatus.Cancelled && outcome.Text == "" && lateRestore.ReadCalls == 1,
                "A capture cancelled while restoring its clipboard reports Cancelled instead of the text it read");
        }

        // The saved clipboard has to be the one this capture replaces. A clipboard that moved
        // while it was being read makes the backup stale, so it must not be put back over what
        // the user copied in the meantime.
        var staleBackup = new ScriptedCopyHost();
        staleBackup.OnBackup = () => staleBackup.Sequence++;
        using (var deadline = new CancellationTokenSource()) {
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), staleBackup);
            Check(staleBackup.BackupCalls == 1 && staleBackup.RestoreCalls == 0 && staleBackup.EmptyRestoreCalls == 0,
                "A backup read while the user copied does not overwrite the newer clipboard");
            Check(Step(outcome, "clipboard_restore") != null && !Step(outcome, "clipboard_restore").ClipboardChanged &&
                Step(outcome, "clipboard_restore").Reason == CaptureReason.ClipboardOriginUnknown,
                "A stale backup is reported as an unknown origin instead of a restore that happened");
            Check(outcome.Status == CaptureStatus.Text && outcome.Text == "copy fixture",
                "The copy itself is still this invocation's text after a stale backup was dropped");
        }

        // The same rule at the last check before the injection: the user copies while the
        // worker is making it, and the backup no longer describes the clipboard it would
        // overwrite.
        var copiedBeforeSend = new ScriptedCopyHost();
        copiedBeforeSend.OnForeground = () => { if (copiedBeforeSend.ForegroundReads == 3) copiedBeforeSend.Sequence++; };
        using (var deadline = new CancellationTokenSource()) {
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), copiedBeforeSend);
            Check(copiedBeforeSend.SendCalls == 1 && copiedBeforeSend.RestoreCalls == 0,
                "A user copy that lands during the final check is not overwritten by the restore");
            Check(Step(outcome, "clipboard_restore") != null && Step(outcome, "clipboard_restore").Reason == CaptureReason.ClipboardOriginUnknown,
                "The dropped backup is recorded as an unknown origin");
        }

        // A restore that fails is metadata, never a claimed success.
        var restoreFails = new ScriptedCopyHost { RestoreThrows = new System.Runtime.InteropServices.ExternalException("fixture restore failure", unchecked((int)0x800401D0)) };
        using (var deadline = new CancellationTokenSource()) {
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), restoreFails);
            Check(outcome.Status == CaptureStatus.Text && Step(outcome, "clipboard_restore") != null &&
                !Step(outcome, "clipboard_restore").ClipboardChanged &&
                Step(outcome, "clipboard_restore").Reason == CaptureReason.ClipboardBusy &&
                Step(outcome, "clipboard_restore").HResult == unchecked((int)0x800401D0),
                "A failed restore leaves only its own metadata instead of a reason of None");
        }

        // Stage durations are differences of one stopwatch, so their sum is the elapsed time
        // rather than a multiple of it.
        var timed = new ScriptedCopyHost { CopyLandsOnSend = false, SequenceChangeAfterPauses = 3, PauseMs = 25 };
        using (var deadline = new CancellationTokenSource()) {
            var steps = new List<CaptureStep>();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var outcome = Native.CopyOnSta(target, deadline, steps, timed);
            watch.Stop();
            long total = 0; foreach (var step in steps) total += step.ElapsedMs;
            Check(outcome.Status == CaptureStatus.Text && total <= watch.ElapsedMilliseconds + 40,
                "Stage elapsed times are durations, not cumulative readings (sum " + total + "ms of " + watch.ElapsedMilliseconds + "ms)");
            Check(total >= 50, "A worker that waits for a late copy still reports that wait");
        }

        // The compatibility wrapper keeps the acquirer's order and its input limit instead of
        // copying blindly. Its fixture window is never the real foreground one, so the worker
        // refuses to inject anything even on the unfixed path.
        var wrapperProbe = new FakeSelectionProbe();
        wrapperProbe.Automation = SelectionCaptureResult.Success("wrapper fixture");
        wrapperProbe.Copy = SelectionCaptureResult.Success("copy fixture");
        Check(await Native.SelectedTextAsync(new SelectionAcquirer(wrapperProbe)) == "wrapper fixture",
            "The compatibility wrapper reads automation before falling back to a copy");
        Check(wrapperProbe.CopyCalls == 0, "A confirmed automation answer is not re-read through the clipboard");

        wrapperProbe.Automation = SelectionCaptureResult.NoText(CaptureStatus.Unavailable, CaptureReason.UnsupportedPattern);
        wrapperProbe.Copy = SelectionCaptureResult.NoText(CaptureStatus.Unavailable, CaptureReason.CopyNoUpdate);
        Check(wrapperProbe.CopyCalls == 0, "An inconclusive automation answer has not copied anything yet");
        await ThrowsAsync(() => Native.SelectedTextAsync(new SelectionAcquirer(wrapperProbe)), "selection",
            "The compatibility wrapper still reports an inconclusive capture as a selection error");
        Check(wrapperProbe.CopyCalls == 1, "An inconclusive automation answer still attempts exactly one fresh copy");

        var longProbe = new FakeSelectionProbe();
        longProbe.Automation = SelectionCaptureResult.Success(new string('x', 6001));
        bool limited = false;
        try { await Native.SelectedTextAsync(new SelectionAcquirer(longProbe)); }
        catch (UserError error) { limited = error.Code == "length"; }
        Check(limited, "The compatibility wrapper keeps the input limit instead of returning oversized text");
    }

    private static async Task ThrowsAsync(Func<Task> action, string code, string label)
    {
        try { await action(); } catch (UserError error) { Check(error.Code == code, label); return; }
        throw new Exception("FAILED: " + label);
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

// A scripted UI Automation tree. Nodes are ordinary objects, so a traversal that is supposed
// to stay bounded is what limits the work - not the fixture.
internal sealed class ScriptedUiTree : IUiTree
{
    public ScriptedUiNode Focus, WindowRoot;
    public bool Foreground = true;
    public IUiNode Focused { get { return Focus; } }
    public IUiNode Root(IntPtr window) { return WindowRoot; }
    public bool IsForeground(CaptureTarget target) { return Foreground; }
    public Action<ScriptedUiNode> OnRead
    {
        get { return ScriptedUiNode.OnRead; }
        set { ScriptedUiNode.OnRead = value; }
    }
    public int TotalReads { get { return ScriptedUiNode.TotalReads; } }
    public int SiblingCalls { get { return ScriptedUiNode.SiblingCalls; } }
    public ScriptedUiTree()
    {
        ScriptedUiNode.TotalReads = 0; ScriptedUiNode.SiblingCalls = 0; ScriptedUiNode.OnRead = null;
    }
}

internal sealed class ScriptedUiNode : IUiNode
{
    public static int TotalReads, SiblingCalls;
    public static Action<ScriptedUiNode> OnRead;
    public int ProcessIdValue = 456;
    public int ControlTypeIdValue = 50004;
    public long WindowHandle;
    public string Selection = "";   // null means this node exposes no usable text pattern.
    public readonly List<ScriptedUiNode> Children = new List<ScriptedUiNode>();
    public ScriptedUiNode ParentNode;
    public int Reads;
    private int index = -1;

    public ScriptedUiNode Add(ScriptedUiNode child)
    {
        child.ParentNode = this; child.index = Children.Count; Children.Add(child);
        return child;
    }
    public int ProcessId { get { return ProcessIdValue; } }
    public int ControlTypeId { get { return ControlTypeIdValue; } }
    public long NativeWindowHandle { get { return WindowHandle; } }
    public string ReadSelection()
    {
        Reads++; TotalReads++;
        if (OnRead != null) OnRead(this);
        return Selection;
    }
    public IUiNode Parent { get { return ParentNode; } }
    public IUiNode FirstChild { get { return Children.Count == 0 ? null : Children[0]; } }
    public IUiNode NextSibling
    {
        get {
            SiblingCalls++;
            ScriptedUiNode parent = ParentNode;
            return parent != null && index + 1 < parent.Children.Count ? parent.Children[index + 1] : null;
        }
    }
}

// A scripted desktop for the production copy worker: no clipboard, no keyboard, no
// foreground and no sleeping unless the test asks for it.
internal sealed class ScriptedCopyHost : ICopyHost
{
    public IntPtr Foreground = new IntPtr(123);
    public uint Sequence = 10;
    public uint Owner = 456;
    public string Text = "copy fixture";
    public ClipboardBackup BackupResult = ClipboardBackup.Of("old clipboard");
    public Exception ReadThrows, BackupThrows, RestoreThrows;
    public bool SendAccepted = true;
    public int LastSendError;
    public bool CopyLandsOnSend = true;
    public int SequenceChangeAfterPauses = -1;
    public int PauseMs = 1;
    public Func<bool> Modifiers = () => false;
    public Action OnRead, OnPause, OnOwner, OnForeground, OnBackup;
    public int BackupCalls, RestoreCalls, EmptyRestoreCalls, SendCalls, ReadCalls, PauseCalls, OwnerReads, ForegroundReads;

    public IntPtr ForegroundWindow
    {
        get { ForegroundReads++; if (OnForeground != null) OnForeground(); return Foreground; }
    }
    public uint ClipboardSequence { get { return Sequence; } }
    public uint ClipboardOwnerProcess
    {
        get { OwnerReads++; if (OnOwner != null) OnOwner(); return Owner; }
    }
    public int SendError { get { return LastSendError; } }
    public bool ModifiersHeld() { return Modifiers(); }
    public void Pause(int milliseconds)
    {
        PauseCalls++;
        if (OnPause != null) OnPause();
        if (PauseCalls == SequenceChangeAfterPauses) Sequence++;
        if (PauseMs > 0) Thread.Sleep(PauseMs);
    }
    public bool SendCopy()
    {
        SendCalls++;
        if (!SendAccepted) return false;
        if (CopyLandsOnSend) Sequence++;
        return true;
    }
    public string ReadClipboardText()
    {
        ReadCalls++;
        if (OnRead != null) OnRead();
        if (ReadThrows != null) throw ReadThrows;
        return Text;
    }
    public ClipboardBackup Backup()
    {
        BackupCalls++;
        if (OnBackup != null) OnBackup();
        if (BackupThrows != null) throw BackupThrows;
        return BackupResult;
    }
    public void Restore(ClipboardBackup backup)
    {
        RestoreCalls++;
        if (backup != null && backup.IsEmpty) EmptyRestoreCalls++;
        if (RestoreThrows != null) throw RestoreThrows;
    }
}

// A scripted clipboard: only the format read itself is faked, so the rule that decides
// whether a backup is complete runs as the production code it is.
internal sealed class ScriptedClipboardFormats : IClipboardFormatSource
{
    private readonly string[] available;
    public readonly Dictionary<string, object> Values = new Dictionary<string, object>();
    public readonly List<string> Unreadable = new List<string>();
    public readonly List<string> Reads = new List<string>();
    public ScriptedClipboardFormats(params string[] formats) { available = formats; }
    public string[] Formats { get { return available; } }
    public bool TryRead(string format, out object value)
    {
        Reads.Add(format);
        if (Unreadable.Contains(format)) { value = null; return false; }
        Values.TryGetValue(format, out value);
        return true;
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
    public Exception AutomationThrows, CopyThrows, ClipboardThrows;
    public bool Current = true;
    public int AutomationCalls, CopyCalls, ClipboardCalls;
    public CaptureTarget Snapshot() { return Target; }
    public bool IsCurrent(CaptureTarget target) { return Current; }
    public Task<SelectionCaptureResult> ReadAutomationAsync(CaptureTarget target, CancellationToken cancellation)
    {
        AutomationCalls++; AutomationToken = cancellation;
        if (AutomationThrows != null) throw AutomationThrows;
        return AutomationPending == null ? Task.FromResult(Automation) : AutomationPending.Task;
    }
    public Task<SelectionCaptureResult> CopySelectionAsync(CaptureTarget target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        CopyCalls++; if (CopyThrows != null) throw CopyThrows;
        return Task.FromResult(Copy);
    }
    public Task<SelectionCaptureResult> ReadClipboardAsync(CaptureTarget target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        ClipboardCalls++; if (ClipboardThrows != null) throw ClipboardThrows;
        return Task.FromResult(Clipboard);
    }
}
