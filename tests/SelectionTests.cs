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
        OwnershipRules();
        await CopyWorker();
        ClipboardBackups();
        await BoundedTraversal();
        await CrossProcessOwnership();
        await NativeFocusWitness();
        DirectClipboardPolicy();
        SelectionAnswers();
        assertions += InternalSelectionTests.Run();
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
        wide.Focus = wideRoot.Add(new ScriptedUiNode { ControlTypeIdValue = 50004, Selection = "" });
        var wideResult = await new WindowsSelectionProbe(wide).ReadAutomationAsync(target, CancellationToken.None);
        Check(wideResult.Status == CaptureStatus.Unavailable && wideResult.Reason == CaptureReason.AutomationBudget,
            "A window too large for the budget is inconclusive instead of confirmed empty (was " + wideResult.Status + "/" + wideResult.Reason + ")");
        Check(wide.TotalReads <= WindowsSelectionProbe.MaxNodes && wide.SiblingCalls <= WindowsSelectionProbe.MaxNodes,
            "A sibling list is never walked past the node budget (reads " + wide.TotalReads + ", siblings " + wide.SiblingCalls + ")");

        // Cancelling between two TreeWalker calls must stop the walk before it enumerates on.
        var cancelTree = new ScriptedUiTree();
        var cancelRoot = cancelTree.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        for (int i = 0; i < 5000; i++) cancelRoot.Add(new ScriptedUiNode { Selection = null });
        cancelTree.Focus = cancelRoot.Add(new ScriptedUiNode { ControlTypeIdValue = 50004, Selection = "" });
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

        // A focus inside another top-level window of the same process belongs to that window,
        // so it is never read and never becomes this capture's answer: the captured window is
        // a boundary of its own, not just a process id.
        var foreignFocus = new ScriptedUiTree();
        var foreignWindow = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 999 };
        var foreignDocument = foreignWindow.Add(new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "another window's selection" });
        var capturedWindow = foreignFocus.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        capturedWindow.Add(new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "captured window selection" });
        foreignFocus.Focus = foreignDocument;
        var foreignResult = await new WindowsSelectionProbe(foreignFocus).ReadAutomationAsync(target, CancellationToken.None);
        Check(foreignResult.Status == CaptureStatus.Text && foreignResult.Text == "captured window selection",
            "Another window of the same process never supplies this capture's selection (was \"" + foreignResult.Text + "\")");
        Check(foreignDocument.Reads == 0 && foreignWindow.Reads == 0,
            "A focused element that cannot be shown to belong to the captured window is never read");

        // The same boundary when the captured window has nothing selected: the foreign focus
        // cannot decide the captured window's answer either.
        var emptyForeign = new ScriptedUiTree();
        var otherWindow = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 999 };
        var otherFocusDocument = otherWindow.Add(new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "another window's selection" });
        var emptyWindow = emptyForeign.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        emptyWindow.Add(new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "" });
        emptyForeign.Focus = otherFocusDocument;
        var emptyForeignResult = await new WindowsSelectionProbe(emptyForeign).ReadAutomationAsync(target, CancellationToken.None);
        Check(emptyForeignResult.Status == CaptureStatus.Empty && emptyForeignResult.Text == "" && otherFocusDocument.Reads == 0,
            "A foreign focus is not turned into this window's empty selection either");

        // A focus inside a child window of the captured window is still this window's own: the
        // boundary is the captured window, not the first window handle on the way up.
        var childWindow = new ScriptedUiTree();
        var outerWindow = childWindow.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        var innerWindow = outerWindow.Add(new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 321 });
        var innerDocument = innerWindow.Add(new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "child window selection" });
        childWindow.Focus = innerDocument;
        var childResult = await new WindowsSelectionProbe(childWindow).ReadAutomationAsync(target, CancellationToken.None);
        Check(childResult.Status == CaptureStatus.Text && childResult.Text == "child window selection",
            "A focused element inside a child window still belongs to the captured window");

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

        // A TreeWalker call that spends the whole budget on its way to "no children" leaves
        // nothing pending, and a truncated walk is still not a confirmed empty selection.
        var slowChild = new ScriptedUiTree();
        var slowRoot = slowChild.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "", WindowHandle = 123 };
        slowChild.Focus = slowRoot;
        slowChild.OnChild = node => { if (node == slowRoot) Thread.Sleep(WindowsSelectionProbe.AutomationBudgetMs + 80); };
        var slowResult = await new WindowsSelectionProbe(slowChild).ReadAutomationAsync(target, CancellationToken.None);
        Check(slowResult.Status == CaptureStatus.Unavailable && slowResult.Reason == CaptureReason.AutomationBudget,
            "A child enumeration that ran out of budget is inconclusive instead of a confirmed empty (was " + slowResult.Status + "/" + slowResult.Reason + ")");

        // The same for the selection query itself: spending the budget inside one read leaves
        // the walk unfinished, whatever the node answered.
        var slowRead = new ScriptedUiTree();
        var slowReadRoot = slowRead.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "", WindowHandle = 123 };
        slowRead.Focus = slowReadRoot;
        slowRead.OnRead = node => { if (node == slowReadRoot) Thread.Sleep(WindowsSelectionProbe.AutomationBudgetMs + 80); };
        var slowReadResult = await new WindowsSelectionProbe(slowRead).ReadAutomationAsync(target, CancellationToken.None);
        Check(slowReadResult.Status == CaptureStatus.Unavailable && slowReadResult.Reason == CaptureReason.AutomationBudget,
            "A selection query that overran the budget is inconclusive too (was " + slowReadResult.Status + "/" + slowReadResult.Reason + ")");

        // A control that has a text pattern but cannot answer is not a confirmed empty
        // selection, and the error code Windows reported has to survive into the stage.
        var unreadableTree = new ScriptedUiTree();
        var unreadableRoot = unreadableTree.WindowRoot = new ScriptedUiNode {
            ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, WindowHandle = 123,
            SelectionUnreadable = true, SelectionHResult = unchecked((int)0x800401D0)
        };
        unreadableTree.Focus = unreadableRoot;
        var unreadableResult = await new WindowsSelectionProbe(unreadableTree).ReadAutomationAsync(target, CancellationToken.None);
        Check(unreadableResult.Status == CaptureStatus.Unavailable && unreadableResult.Reason == CaptureReason.AutomationUnreadable &&
            unreadableResult.Text == "" && unreadableResult.Steps.First(s => s.Stage == "uia").HResult == unchecked((int)0x800401D0),
            "A document whose selection query failed is unavailable with its HResult, not an empty selection (was "
                + unreadableResult.Status + "/" + unreadableResult.Reason + ")");

        // A sibling call that blocks the same way is bounded too: the enumeration is abandoned
        // and the scan stays inconclusive instead of concluding anything from a partial list.
        var slowSibling = new ScriptedUiTree();
        var siblingRoot = slowSibling.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "", WindowHandle = 123 };
        var firstChild = siblingRoot.Add(new ScriptedUiNode { Selection = null });
        siblingRoot.Add(new ScriptedUiNode { Selection = null });
        slowSibling.Focus = siblingRoot;
        slowSibling.OnSibling = node => { if (node == firstChild) Thread.Sleep(WindowsSelectionProbe.AutomationBudgetMs + 80); };
        var slowSiblingResult = await new WindowsSelectionProbe(slowSibling).ReadAutomationAsync(target, CancellationToken.None);
        Check(slowSiblingResult.Status == CaptureStatus.Unavailable && slowSiblingResult.Reason == CaptureReason.AutomationBudget,
            "A sibling enumeration that ran out of budget is inconclusive as well (was " + slowSiblingResult.Status + "/" + slowSiblingResult.Reason + ")");

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

    // The read policy behind the real UIA node, checked directly: a query that threw, a range
    // list that never came back and a range that failed are unknown answers, while a query
    // that really answered with nothing is a confirmed empty one.
    private static void SelectionAnswers()
    {
        var failedQuery = WindowsSelectionProbe.ReadSelectionAnswer(
            () => { throw new System.Runtime.InteropServices.COMException("fixture query failure", unchecked((int)0x800401D0)); },
            range => "text");
        Check(failedQuery.Text == null && failedQuery.Unreadable && failedQuery.HResult == unchecked((int)0x800401D0),
            "A selection query that threw is unknown and keeps its HResult");

        var noRanges = WindowsSelectionProbe.ReadSelectionAnswer(() => null, range => "text");
        Check(noRanges.Text == null && noRanges.Unreadable,
            "A selection query that returned no range list is unknown, not an empty selection");

        var failedRange = WindowsSelectionProbe.ReadSelectionAnswer(
            () => new object[] { "range" },
            range => { throw new System.Runtime.InteropServices.COMException("fixture range failure", unchecked((int)0x8001010A)); });
        Check(failedRange.Text == null && failedRange.Unreadable && failedRange.HResult == unchecked((int)0x8001010A),
            "A range that cannot be read is unknown and keeps its HResult");

        var answeredEmpty = WindowsSelectionProbe.ReadSelectionAnswer(() => new object[0], range => "text");
        Check(answeredEmpty.Text == "" && !answeredEmpty.Unreadable,
            "A query that answered with nothing is a confirmed empty selection");

        var answeredText = WindowsSelectionProbe.ReadSelectionAnswer(() => new object[] { "one ", "two" }, range => (string)range);
        Check(answeredText.Text == "one two" && !answeredText.Unreadable,
            "The ranges that answered are joined into the selection text");

        // The provider's own pattern surface: TextPattern is asked for first, and a provider
        // that only exposes TextPattern2 still answers instead of looking like no pattern.
        Check((string)WindowsSelectionProbe.ChooseTextPattern(() => "text", () => "text2") == "text",
            "The selection read asks for TextPattern first");
        Check((string)WindowsSelectionProbe.ChooseTextPattern(() => null, () => "text2") == "text2",
            "A provider that exposes only TextPattern2 still answers");
        Check(WindowsSelectionProbe.ChooseTextPattern(() => null, () => null) == null &&
            WindowsSelectionProbe.ChooseTextPattern(null, () => "text2") == null,
            "A control with neither text pattern stays no candidate");
    }

    // The window identity behind the cross-process exception: native containment plus a
    // verified WebView host class. Nothing else - no process id, no process tree - qualifies.
    private static void OwnershipRules()
    {
        var ownership = new ScriptedWindowOwnership();
        ownership.Child(123, 321); ownership.Class(321, "Chrome_WidgetWin_1");
        Check(WindowOwnership.Contains(ownership, new IntPtr(123), new IntPtr(123)),
            "The captured window contains itself");
        Check(WindowOwnership.Contains(ownership, new IntPtr(123), new IntPtr(321)),
            "A window that hangs natively under the captured one belongs to it");
        Check(!WindowOwnership.Contains(ownership, new IntPtr(123), new IntPtr(777)) &&
            !WindowOwnership.Contains(ownership, IntPtr.Zero, new IntPtr(321)) &&
            !WindowOwnership.Contains(ownership, new IntPtr(123), IntPtr.Zero),
            "A window the captured one does not contain is not part of it");
        Check(WindowOwnership.IsVerifiedHostWindow(ownership, new IntPtr(123), new IntPtr(321)),
            "A verified WebView host window inside the captured window is accepted");
        Check(!WindowOwnership.IsVerifiedHostWindow(ownership, new IntPtr(123), new IntPtr(123)),
            "The captured window is not a host boundary of its own");
        var plainClass = new ScriptedWindowOwnership();
        plainClass.Child(123, 321); plainClass.Class(321, "HwndWrapper[Other.exe]");
        Check(!WindowOwnership.IsVerifiedHostWindow(plainClass, new IntPtr(123), new IntPtr(321)),
            "A contained window of another class is not a verified host identity");
        var unrelated = new ScriptedWindowOwnership();
        unrelated.Class(321, "Chrome_WidgetWin_1");
        Check(!WindowOwnership.IsVerifiedHostWindow(unrelated, new IntPtr(123), new IntPtr(321)),
            "A WebView class outside the captured window proves nothing");
    }

    // The reported Codeg case: the UIA ancestor chain of the focused page cannot be proven to
    // belong to the captured window, but the native keyboard focus sits on the verified
    // WebView host inside it. That witness - read live per invocation, never a cached process
    // id or class - allows one bounded selection query confined to the host's subtree, and
    // nothing outside it is read.
    private sealed class WitnessTree
    {
        public ScriptedUiTree Tree = new ScriptedUiTree();
        public ScriptedUiNode Root, ShellDocument, Host, Page;
        public IntPtr NativeFocus { set { Tree.NativeFocus = value; } }
    }

    private static WitnessTree NewWitnessTree()
    {
        var fixture = new WitnessTree();
        var tree = fixture.Tree;
        fixture.Root = tree.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        fixture.ShellDocument = fixture.Root.Add(new ScriptedUiNode {
            ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "shell selection", WindowHandle = 123 });
        fixture.Host = fixture.Root.Add(new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 321, ProcessIdValue = 999 });
        fixture.Page = fixture.Host.Add(new ScriptedUiNode {
            ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "page selection", ProcessIdValue = 999 });
        // The control-view chain never reaches the host: the page's own ancestor chain is
        // unproven, so only the native witness can vouch for the host's subtree.
        fixture.Page.ParentNode = null;
        tree.Focus = fixture.Page;
        return fixture;
    }

    private static async Task NativeFocusWitness()
    {
        var target = new CaptureTarget { Window = new IntPtr(123), ProcessId = 456 };
        var ownership = new ScriptedWindowOwnership();
        ownership.Child(123, 321); ownership.Class(321, "Chrome_WidgetWin_1");
        ownership.Child(123, 322); ownership.Class(322, "HwndWrapper[Shell.exe]");
        ownership.Child(123, 355); ownership.Parent(355, 321);
        ownership.Child(123, 777); ownership.Class(777, "Chrome_WidgetWin_1");
        ownership.Child(777, 778); ownership.Parent(778, 777); ownership.Class(778, "Chrome_WidgetWin_1");

        // The control-view chain never reaches the host: the page's own ancestor chain is
        // unproven, so only the native witness can vouch for the host's subtree.
        var witnessed = NewWitnessTree();
        witnessed.NativeFocus = new IntPtr(321);
        var witnessedResult = await new WindowsSelectionProbe(witnessed.Tree, ownership).ReadAutomationAsync(target, CancellationToken.None);
        Check(witnessedResult.Status == CaptureStatus.Text && witnessedResult.Text == "page selection",
            "A native focus inside the verified WebView host subtree answers for the captured window (was "
                + witnessedResult.Status + "/" + witnessedResult.Reason + ")");
        Check(witnessed.Page.Reads == 1 && witnessed.ShellDocument.Reads == 0,
            "The witness-bounded query reads the host's page and nothing outside the subtree");

        // A witness one native level deeper than the host still resolves to the same subtree.
        var deeper = NewWitnessTree();
        deeper.NativeFocus = new IntPtr(355);
        var deeperResult = await new WindowsSelectionProbe(deeper.Tree, ownership).ReadAutomationAsync(target, CancellationToken.None);
        Check(deeperResult.Status == CaptureStatus.Text && deeperResult.Text == "page selection",
            "A native focus below the verified host resolves to the same bounded subtree (was "
                + deeperResult.Status + "/" + deeperResult.Reason + ")");

        // No native witness, or one that does not hang inside the captured window: the
        // ownership stays unproven and nothing is read - not even the window's own shell.
        foreach (var caseFocus in new[] { IntPtr.Zero, new IntPtr(777), new IntPtr(322) }) {
            var unproven = NewWitnessTree();
            unproven.NativeFocus = caseFocus;
            var unprovenResult = await new WindowsSelectionProbe(unproven.Tree, ownership).ReadAutomationAsync(target, CancellationToken.None);
            Check(unprovenResult.Status == CaptureStatus.Unavailable && unprovenResult.Reason == CaptureReason.OwnershipUnproven &&
                unproven.Page.Reads == 0 && unproven.Host.Reads == 0 && unproven.ShellDocument.Reads == 0,
                "Without a proven native witness the capture stays OwnershipUnproven and reads nothing (focus "
                    + caseFocus + ", was " + unprovenResult.Status + "/" + unprovenResult.Reason + ")");
        }

        // A foreign window's own WebView class proves nothing even when its child is focused.
        var foreignWitness = NewWitnessTree();
        foreignWitness.NativeFocus = new IntPtr(778);
        var foreignResult = await new WindowsSelectionProbe(foreignWitness.Tree, ownership).ReadAutomationAsync(target, CancellationToken.None);
        Check(foreignResult.Reason == CaptureReason.OwnershipUnproven && foreignResult.Status == CaptureStatus.Unavailable &&
            foreignWitness.Page.Reads == 0,
            "A WebView chain outside the captured window is never a witness");

        // The vouched subtree may not be exposed by this tree at all: unproven, and the rest
        // of the window is not read as a fallback.
        var missing = new WitnessTree();
        missing.Root = missing.Tree.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        missing.Page = missing.Root.Add(new ScriptedUiNode {
            ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "unvouched selection", ProcessIdValue = 999 });
        missing.Page.ParentNode = null;
        missing.Tree.Focus = missing.Page;
        missing.NativeFocus = new IntPtr(321);
        var missingResult = await new WindowsSelectionProbe(missing.Tree, ownership).ReadAutomationAsync(target, CancellationToken.None);
        Check(missingResult.Status == CaptureStatus.Unavailable && missingResult.Reason == CaptureReason.OwnershipUnproven &&
            missing.Page.Reads == 0,
            "A witness without an exposed host subtree stays unproven instead of falling back (was "
                + missingResult.Status + "/" + missingResult.Reason + ")");

        // Locating the subtree is bounded like every other walk: running out of nodes is an
        // inconclusive capture, never a read of whatever came first.
        var huge = NewWitnessTree();
        huge.Root.AddMany(5000);
        huge.NativeFocus = new IntPtr(321);
        var hugeResult = await new WindowsSelectionProbe(huge.Tree, ownership).ReadAutomationAsync(target, CancellationToken.None);
        Check(hugeResult.Status == CaptureStatus.Unavailable && hugeResult.Reason == CaptureReason.AutomationBudget &&
            huge.Page.Reads == 0,
            "A witness search too large for the node budget is inconclusive instead of a partial read (was "
                + hugeResult.Status + "/" + hugeResult.Reason + ")");

        // A truncated walk inside the vouched subtree is equally inconclusive: the one empty
        // document it reached first cannot conclude the whole subtree is unselected. The
        // filler nodes hang under the page, so the sibling chain survives the unproven chain
        // fixure and the walk genuinely runs out of node budget.
        var truncated = NewWitnessTree();
        truncated.Page.Selection = "";
        truncated.Page.AddMany(5000);
        truncated.NativeFocus = new IntPtr(321);
        var truncatedResult = await new WindowsSelectionProbe(truncated.Tree, ownership).ReadAutomationAsync(target, CancellationToken.None);
        Check(truncatedResult.Status == CaptureStatus.Unavailable && truncatedResult.Reason == CaptureReason.AutomationBudget,
            "A subtree walk that runs out of budget is inconclusive instead of a confirmed empty (was "
                + truncatedResult.Status + "/" + truncatedResult.Reason + ")");
    }

    // The direct clipboard read and the copy path share no reasons: an empty clipboard is
    // ClipboardEmpty (a fact about the clipboard), CopyNoUpdate belongs to a simulated copy
    // alone, and a direct read never logs a clipboard change it never compared.
    private static void DirectClipboardPolicy()
    {
        var empty = Native.DirectClipboardResult("   \r\n");
        Check(empty.Status == CaptureStatus.Unavailable && empty.Reason == CaptureReason.ClipboardEmpty &&
            !empty.Steps[0].Checked.HasFlag(CaptureFields.Clipboard) && !empty.Steps[0].Checked.HasFlag(CaptureFields.Foreground),
            "A direct clipboard read without text is ClipboardEmpty and claims no unchecked metadata");
        var read = Native.DirectClipboardResult("clipboard content");
        Check(read.Status == CaptureStatus.Text && read.Reason == CaptureReason.None &&
            !read.Steps[0].Checked.HasFlag(CaptureFields.Clipboard),
            "A direct clipboard read with text is Text without claiming a clipboard change");

        string folder = Path.Combine(Path.GetTempPath(), "leaf-clip-" + Guid.NewGuid().ToString("N"));
        try {
            var log = new DiagnosticLog(folder);
            log.Capture(CaptureTelemetry.For("direct-empty", null, true, "Ctrl+Alt+T", empty));
            var copySteps = new List<CaptureStep> {
                new CaptureStep { Stage = "copy_send", Reason = CaptureReason.None, Checked = CaptureFields.Foreground, ForegroundSame = true },
                new CaptureStep { Stage = "clipboard_read", Reason = CaptureReason.CopyNoUpdate,
                    Checked = CaptureFields.Foreground | CaptureFields.Clipboard | CaptureFields.Owner,
                    ClipboardChanged = false, ForegroundSame = true }
            };
            log.Capture(CaptureTelemetry.For("copy-noupdate", new CaptureTarget { Window = new IntPtr(123), ProcessId = 456 }, false, "Ctrl+Alt+T",
                SelectionCaptureResult.Create(CaptureStatus.Unavailable, CaptureReason.CopyNoUpdate, "", copySteps)));
            var rows = Directory.GetFiles(folder).SelectMany(File.ReadAllLines).ToList();
            string directRow = rows.First(r => r.Contains("direct-empty") && r.Contains("clipboard_read"));
            Check(directRow.Contains("ClipboardEmpty") && !directRow.Contains("clipboard_changed") && !directRow.Contains("foreground_same"),
                "The direct empty read logs ClipboardEmpty without a clipboard_changed or foreground field");
            string sendRow = rows.First(r => r.Contains("copy-noupdate") && r.Contains("copy_send"));
            Check(sendRow.Contains("copy_send") && !sendRow.Contains("modifiers_released") && !sendRow.Contains("focus_checked"),
                "The copy_send stage omits the fields it never checked, such as modifiers");
            string copyRow = rows.First(r => r.Contains("copy-noupdate") && r.Contains("clipboard_read"));
            Check(copyRow.Contains("CopyNoUpdate") && copyRow.Contains("clipboard_changed"),
                "A simulated copy with no update is CopyNoUpdate with its checked metadata");
        } finally {
            if (Directory.Exists(folder) && Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(folder, true);
        }
    }

    // The reported Codeg failure: the page inside the captured window is hosted by another
    // process. Only the verified host window may bridge that, and only for its own content.
    private static async Task CrossProcessOwnership()
    {
        var target = new CaptureTarget { Window = new IntPtr(123), ProcessId = 456 };
        var ownership = new ScriptedWindowOwnership();
        ownership.Child(123, 321); ownership.Class(321, "Chrome_WidgetWin_1");

        // The focused document is in the host process; its chain reaches the verified WebView
        // host window that hangs natively under the captured window.
        var focusedTree = new ScriptedUiTree();
        focusedTree.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        var host = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 321, ProcessIdValue = 999 };
        var page = host.Add(new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "page selection", ProcessIdValue = 999 });
        focusedTree.Focus = page;
        var focusedResult = await new WindowsSelectionProbe(focusedTree, ownership).ReadAutomationAsync(target, CancellationToken.None);
        Check(focusedResult.Status == CaptureStatus.Text && focusedResult.Text == "page selection",
            "A WebView page inside the captured window answers across its process boundary (was " + focusedResult.Status + "/" + focusedResult.Reason + ")");

        // The same when the focus never leaves the host window: the walk enters the verified
        // WebView content and reads the page inside it.
        var walkTree = new ScriptedUiTree();
        var walkRoot = walkTree.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        var walkHost = walkRoot.Add(new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 321, ProcessIdValue = 999 });
        var walkPage = walkHost.Add(new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "walked page selection", ProcessIdValue = 999 });
        walkTree.Focus = walkRoot.Add(new ScriptedUiNode { ControlTypeIdValue = 50004, Selection = "" });
        var walkResult = await new WindowsSelectionProbe(walkTree, ownership).ReadAutomationAsync(target, CancellationToken.None);
        Check(walkResult.Status == CaptureStatus.Text && walkResult.Text == "walked page selection",
            "The window walk reads the page hanging under the verified WebView host (was " + walkResult.Status + "/" + walkResult.Reason + ")");

        // Another application's WebView: same class, but not a native child of the captured
        // window, so its process never answers for this capture.
        var outsideTree = new ScriptedUiTree();
        outsideTree.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        var outsideHost = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 777, ProcessIdValue = 999 };
        var outsidePage = outsideHost.Add(new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "another application's selection", ProcessIdValue = 999 });
        outsideTree.Focus = outsidePage;
        var outsideOwnership = new ScriptedWindowOwnership();
        outsideOwnership.Class(777, "Chrome_WidgetWin_1");
        var outsideResult = await new WindowsSelectionProbe(outsideTree, outsideOwnership).ReadAutomationAsync(target, CancellationToken.None);
        Check(outsideResult.Status == CaptureStatus.Unavailable && outsideResult.Reason == CaptureReason.OwnershipUnproven &&
            outsideResult.Text == "" && outsidePage.Reads == 0 && outsideHost.Reads == 0,
            "A WebView host outside the captured window never supplies its selection (was " + outsideResult.Status + "/" + outsideResult.Reason + ")");

        // Reaching the captured window through another process is not enough by itself: only a
        // verified host carries its content with it, so those nodes are never read.
        var mixedTree = new ScriptedUiTree();
        var mixedRoot = mixedTree.WindowRoot = new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, WindowHandle = 123 };
        var mixedHost = mixedRoot.Add(new ScriptedUiNode { ControlTypeIdValue = 50033, Selection = null, ProcessIdValue = 999 });
        var mixedPage = mixedHost.Add(new ScriptedUiNode { ControlTypeIdValue = WindowsSelectionProbe.DocumentTypeId, Selection = "unverified foreign selection", ProcessIdValue = 999 });
        mixedTree.Focus = mixedPage;
        var mixedResult = await new WindowsSelectionProbe(mixedTree, new ScriptedWindowOwnership()).ReadAutomationAsync(target, CancellationToken.None);
        Check(mixedResult.Status != CaptureStatus.Text && mixedResult.Text == "" && mixedPage.Reads == 0 && mixedHost.Reads == 0,
            "A chain that only passes through another process is not this window's content (was " + mixedResult.Status + "/" + mixedResult.Reason + ")");

        // A process change inside the captured window is not a foreground change.
        Check(outsideResult.Reason != CaptureReason.ForegroundChanged && focusedResult.Reason == CaptureReason.None,
            "A same-window process boundary is never reported as a foreground change");
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

        // The desktop read is the same rule at its source: a data object the clipboard did not
        // hand over is unknown, never a verified empty that a later restore may clear.
        Check(Native.BackupFrom(null) == null,
            "A desktop read that handed over no data object is unknown rather than a verified empty");
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

        // The answer the desktop adapter itself gives for a data object the clipboard did not
        // hand over, run through the real worker: it may not become a clear of the clipboard.
        var unhanded = new ScriptedCopyHost { BackupResult = Native.BackupFrom(null) };
        using (var deadline = new CancellationTokenSource()) {
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), unhanded);
            Check(unhanded.RestoreCalls == 0 && unhanded.EmptyRestoreCalls == 0,
                "A data object the desktop did not hand over never becomes a clear of the clipboard");
            Check(Step(outcome, "clipboard_restore") != null && !Step(outcome, "clipboard_restore").ClipboardChanged &&
                Step(outcome, "clipboard_restore").Reason == CaptureReason.ClipboardOriginUnknown,
                "An unhanded data object is recorded as an unknown restore that did not happen");
        }

        // A clipboard that was read and found empty can be restored as empty. The fixture
        // stands for that verified empty: the desktop read cannot establish it, and answers
        // unknown for a data object the clipboard never handed over.
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
            Check(Step(outcome, "clipboard_read") != null && Step(outcome, "clipboard_read").Reason == CaptureReason.ClipboardOriginUnknown,
                "The read stage reports the discarded origin instead of a read that counted");
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

        // The keyboard focus the simulated copy would reach is checked as late as possible, and
        // a focus outside the captured window stops the copy instead of sending it elsewhere.
        var elsewhere = new ScriptedCopyHost { Focus = new IntPtr(777), PauseMs = 0 };
        using (var deadline = new CancellationTokenSource()) {
            var steps = new List<CaptureStep>();
            var outcome = Native.CopyOnSta(target, deadline, steps, elsewhere, new ScriptedWindowOwnership());
            Check(outcome.Status == CaptureStatus.Unavailable && outcome.Reason == CaptureReason.FocusNotOwned &&
                elsewhere.SendCalls == 0 && outcome.Text == "",
                "A keyboard focus outside the captured window stops the injected copy");
            Check(Step(outcome, "copy_focus") != null && Step(outcome, "copy_focus").FocusChecked &&
                !Step(outcome, "copy_focus").FocusOwned && Step(outcome, "copy_focus").Reason == CaptureReason.FocusNotOwned,
                "The refused copy reports the focus check it made instead of a sent copy");
        }

        // The same focus inside the captured window - its own child window - still sends the
        // one copy, and the metadata says the check passed.
        var insideOwnership = new ScriptedWindowOwnership(); insideOwnership.Child(123, 321);
        var inside = new ScriptedCopyHost { Focus = new IntPtr(321), PauseMs = 0 };
        using (var deadline = new CancellationTokenSource()) {
            var steps = new List<CaptureStep>();
            var outcome = Native.CopyOnSta(target, deadline, steps, inside, insideOwnership);
            Check(outcome.Status == CaptureStatus.Text && inside.SendCalls == 1 &&
                Step(outcome, "copy_focus").FocusChecked && Step(outcome, "copy_focus").FocusOwned,
                "A keyboard focus inside the captured window still sends the one injected copy");
        }

        // A focus Windows did not report is unknown, not "somewhere else": it never blocks the
        // copy, and the metadata says the check could not be made.
        var unknownFocus = new ScriptedCopyHost { PauseMs = 0 };
        using (var deadline = new CancellationTokenSource()) {
            var steps = new List<CaptureStep>();
            var outcome = Native.CopyOnSta(target, deadline, steps, unknownFocus, new ScriptedWindowOwnership());
            Check(outcome.Status == CaptureStatus.Text && unknownFocus.SendCalls == 1 &&
                Step(outcome, "copy_focus") != null && !Step(outcome, "copy_focus").FocusChecked &&
                !Step(outcome, "copy_focus").FocusOwned && Step(outcome, "copy_focus").Reason == CaptureReason.None,
                "An unreported keyboard focus is recorded as unchecked rather than as another window");
        }

        // The keys can come down again while the old clipboard is being saved; the copy is then
        // not sent, and the user's own keys are never released for it.
        var pressedAgain = new ScriptedCopyHost { PauseMs = 0 };
        pressedAgain.OnBackup = () => { pressedAgain.Modifiers = () => true; };
        using (var deadline = new CancellationTokenSource()) {
            var steps = new List<CaptureStep>();
            var outcome = Native.CopyOnSta(target, deadline, steps, pressedAgain, new ScriptedWindowOwnership());
            Check(outcome.Status == CaptureStatus.Unavailable && outcome.Reason == CaptureReason.ModifiersHeld &&
                pressedAgain.SendCalls == 0 && Step(outcome, "copy_focus") != null && !Step(outcome, "copy_focus").ModifiersReleased,
                "Keys pressed again after the backup stop the copy instead of releasing the user's keys");
        }

        // A WebView2 copy is owned by its browser process even though the window belongs to the
        // captured one. The owning window is what proves the origin; an owner process alone is
        // never trusted.
        var webOwnership = new ScriptedWindowOwnership();
        webOwnership.Child(123, 321); webOwnership.Class(321, "Chrome_WidgetWin_1");
        var webOwner = new ScriptedCopyHost { Owner = 999, OwnerWindow = new IntPtr(321), PauseMs = 0 };
        using (var deadline = new CancellationTokenSource()) {
            var steps = new List<CaptureStep>();
            var outcome = Native.CopyOnSta(target, deadline, steps, webOwner, webOwnership);
            Check(outcome.Status == CaptureStatus.Text && outcome.Text == "copy fixture" &&
                Step(outcome, "clipboard_read") != null && Step(outcome, "clipboard_read").OwnerWindowVerified,
                "A copy owned by the WebView window inside the captured window is this selection");
        }
        var unverifiedOwner = new ScriptedCopyHost { Owner = 999, OwnerWindow = IntPtr.Zero, PauseMs = 0 };
        using (var deadline = new CancellationTokenSource()) {
            var steps = new List<CaptureStep>();
            var outcome = Native.CopyOnSta(target, deadline, steps, unverifiedOwner, webOwnership);
            Check(outcome.Status == CaptureStatus.Unavailable && outcome.Text == "" && unverifiedOwner.ReadCalls == 0 &&
                Step(outcome, "clipboard_read").Reason == CaptureReason.ClipboardOriginUnknown &&
                Step(outcome, "clipboard_read").ClipboardOwnerProcess == 999 &&
                !Step(outcome, "clipboard_read").OwnerWindowVerified,
                "Another process's copy is never this capture's text, whatever its window looks like");
        }
        var plainWindowOwner = new ScriptedCopyHost { Owner = 999, OwnerWindow = new IntPtr(321), PauseMs = 0 };
        using (var deadline = new CancellationTokenSource()) {
            var outcome = Native.CopyOnSta(target, deadline, new List<CaptureStep>(), plainWindowOwner, new ScriptedWindowOwnership());
            Check(outcome.Status == CaptureStatus.Unavailable && outcome.Text == "" && plainWindowOwner.ReadCalls == 0,
                "A contained window without a verified host class does not vouch for another process");
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
                Stage = "uia", Reason = CaptureReason.None, Checked = CaptureFields.Foreground | CaptureFields.Read,
                CandidateCount = 3, ControlTypeId = 50030, PatternSupported = true, ForegroundSame = true, ElapsedMs = 12
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

            // The copy path's new metadata is recorded without any window title, control name
            // or clipboard content: only the facts a later check can compare.
            var metadata = new DiagnosticLog(folder);
            var detailed = SelectionCaptureResult.Success(""); detailed.Steps.Add(new CaptureStep {
                Stage = "copy_focus", Reason = CaptureReason.FocusNotOwned, Checked = CaptureFields.Foreground | CaptureFields.Modifiers | CaptureFields.Focus,
                FocusChecked = true, FocusOwned = false, ModifiersReleased = true, ForegroundSame = true, ElapsedMs = 5
            });
            detailed.Steps.Add(new CaptureStep {
                Stage = "clipboard_read", Reason = CaptureReason.None, Checked = CaptureFields.Foreground | CaptureFields.Clipboard | CaptureFields.Owner,
                ClipboardChanged = true, ClipboardOwnerProcess = 4242, OwnerWindowVerified = true, ElapsedMs = 20
            });
            metadata.Capture(CaptureTelemetry.For("fixture-metadata", probe.Snapshot(), false, "Ctrl+Alt+T", detailed));
            string metadataEntries = string.Concat(Directory.GetFiles(folder).Select(File.ReadAllText));
            Check(metadataEntries.Contains("\"stage\":\"copy_focus\"") && metadataEntries.Contains("\"focus_owned\":false") &&
                metadataEntries.Contains("\"clipboard_owner_process\":4242") && metadataEntries.Contains("\"owner_window_verified\":true"),
                "The copy stage reports the focus and owner checks as typed metadata");

            // The in-window page read logs its own typed row with a whitelisted source name;
            // the text it read and any control name outside that list never reach the log.
            var pageRead = new DiagnosticLog(folder);
            pageRead.InternalSelection(InternalSelectionStatus.Text, InternalSelectionReason.None, "TranslationText", 12);
            pageRead.InternalSelection(InternalSelectionStatus.Unavailable, InternalSelectionReason.SensitiveControl, "ApiKeyInput", 0);
            string pageEntries = string.Concat(Directory.GetFiles(folder).Select(File.ReadAllText));
            Check(pageEntries.Contains("\"event\":\"internal_selection\"") && pageEntries.Contains("\"source\":\"TranslationText\"") &&
                pageEntries.Contains("\"status\":\"Unavailable\"") && pageEntries.Contains("text_length"),
                "The in-window read records typed metadata with a whitelisted source name");
            Check(!pageEntries.Contains("ApiKeyInput") && pageEntries.Contains("\"source\":\"other\""),
                "A control name outside the whitelist cannot become free text in the log");
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
    public IntPtr NativeFocus;
    public bool Foreground = true;
    public IUiNode Focused { get { return Focus; } }
    public IntPtr NativeFocusWindow { get { return NativeFocus; } }
    public IUiNode Root(IntPtr window) { return WindowRoot; }
    public bool IsForeground(CaptureTarget target) { return Foreground; }
    public Action<ScriptedUiNode> OnRead
    {
        get { return ScriptedUiNode.OnRead; }
        set { ScriptedUiNode.OnRead = value; }
    }
    public Action<ScriptedUiNode> OnChild
    {
        get { return ScriptedUiNode.OnChild; }
        set { ScriptedUiNode.OnChild = value; }
    }
    public Action<ScriptedUiNode> OnSibling
    {
        get { return ScriptedUiNode.OnSibling; }
        set { ScriptedUiNode.OnSibling = value; }
    }
    public int TotalReads { get { return ScriptedUiNode.TotalReads; } }
    public int SiblingCalls { get { return ScriptedUiNode.SiblingCalls; } }
    public ScriptedUiTree()
    {
        ScriptedUiNode.TotalReads = 0; ScriptedUiNode.SiblingCalls = 0;
        ScriptedUiNode.OnRead = null; ScriptedUiNode.OnChild = null; ScriptedUiNode.OnSibling = null;
    }
}

internal sealed class ScriptedUiNode : IUiNode
{
    public static int TotalReads, SiblingCalls;
    public static Action<ScriptedUiNode> OnRead, OnChild, OnSibling;
    public int ProcessIdValue = 456;
    public int ControlTypeIdValue = 50004;
    public long WindowHandle;
    public string Selection = "";   // null means this node exposes no usable text pattern.
    public bool SelectionUnreadable; // true means it has a pattern but cannot answer.
    public int SelectionHResult;
    public readonly List<ScriptedUiNode> Children = new List<ScriptedUiNode>();
    public ScriptedUiNode ParentNode;
    public int Reads;
    private int index = -1;

    public ScriptedUiNode Add(ScriptedUiNode child)
    {
        child.ParentNode = this; child.index = Children.Count; Children.Add(child);
        return child;
    }
    // A wide flat list of unreadable siblings, for budget tests.
    public void AddMany(int count)
    {
        for (int i = 0; i < count; i++) Add(new ScriptedUiNode { Selection = null });
    }
    public int ProcessId { get { return ProcessIdValue; } }
    public int ControlTypeId { get { return ControlTypeIdValue; } }
    public long NativeWindowHandle { get { return WindowHandle; } }
    public UiSelectionAnswer ReadSelection()
    {
        Reads++; TotalReads++;
        if (OnRead != null) OnRead(this);
        if (SelectionUnreadable) return UiSelectionAnswer.Unknown(SelectionHResult);
        return Selection == null ? UiSelectionAnswer.NoPattern : UiSelectionAnswer.Of(Selection);
    }
    public IUiNode Parent { get { return ParentNode; } }
    public IUiNode FirstChild
    {
        get {
            if (OnChild != null) OnChild(this);
            return Children.Count == 0 ? null : Children[0];
        }
    }
    public IUiNode NextSibling
    {
        get {
            SiblingCalls++;
            if (OnSibling != null) OnSibling(this);
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
    public IntPtr Focus = IntPtr.Zero;        // zero means Windows reported no focus window.
    public uint Sequence = 10;
    public uint Owner = 456;
    public IntPtr OwnerWindow = IntPtr.Zero;  // zero means the clipboard has no owner window.
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
    public IntPtr FocusedWindow { get { return Focus; } }
    public uint ClipboardSequence { get { return Sequence; } }
    public uint ClipboardOwnerProcess
    {
        get { OwnerReads++; if (OnOwner != null) OnOwner(); return Owner; }
    }
    public IntPtr ClipboardOwnerWindow { get { return OwnerWindow; } }
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

// The native window facts behind the cross-process ownership rule, scripted: a window is a
// child only when this fixture says so, and carries only the class the fixture gives it.
internal sealed class ScriptedWindowOwnership : IWindowOwnership
{
    public readonly HashSet<string> Children = new HashSet<string>();
    public readonly Dictionary<long, string> Classes = new Dictionary<long, string>();
    public readonly Dictionary<long, long> Parents = new Dictionary<long, long>();
    public void Child(long parent, long child) { Children.Add(parent + ">" + child); }
    public void Class(long window, string name) { Classes[window] = name; }
    public void Parent(long child, long parent) { Parents[child] = parent; }
    public bool IsChildOf(IntPtr parent, IntPtr child) { return Children.Contains(parent.ToInt64() + ">" + child.ToInt64()); }
    public string ClassName(IntPtr window) { string name; return Classes.TryGetValue(window.ToInt64(), out name) ? name : ""; }
    public IntPtr ParentOf(IntPtr window)
    {
        long parent;
        return Parents.TryGetValue(window.ToInt64(), out parent) ? new IntPtr(parent) : IntPtr.Zero;
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
