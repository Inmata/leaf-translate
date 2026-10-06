using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Leaf
{
    // The one node of the desktop tree the probe is allowed to read. Control Name and Value
    // are not part of this surface, and no unbounded FindAll exists on it.
    public interface IUiNode
    {
        int ProcessId { get; }
        // 0 when the control type cannot be read, which also makes it no candidate.
        int ControlTypeId { get; }
        long NativeWindowHandle { get; }
        // Selected text; "" when the pattern is present but nothing is selected, or null when
        // this node exposes no usable text pattern.
        string ReadSelection();
        IUiNode Parent { get; }
        IUiNode FirstChild { get; }
        IUiNode NextSibling { get; }
    }

    // The tree the probe walks. A fixture implements it so the bounded traversal itself can
    // be checked off-desktop instead of only through a scripted ISelectionProbe.
    public interface IUiTree
    {
        IUiNode Focused { get; }
        IUiNode Root(IntPtr window);
        bool IsForeground(CaptureTarget target);
    }

    // The Windows implementation of ISelectionProbe. The foreground target is taken
    // before the popup is shown, so the popup can never become the capture source.
    public sealed class WindowsSelectionProbe : ISelectionProbe
    {
        public const int MaxNodes = 128;
        public const int MaxCandidates = 32;
        // Slightly below the coordinator's budget so a bounded scan can report its own end.
        private const int AutomationBudgetMs = 240;
        private const int AncestorDepth = 24;
        public static readonly int DocumentTypeId = ControlType.Document.Id;
        private static int automationRunning;
        private readonly IUiTree tree;

        public WindowsSelectionProbe() : this(new AutomationUiTree()) { }
        public WindowsSelectionProbe(IUiTree uiTree)
        {
            if (uiTree == null) throw new ArgumentNullException("uiTree");
            tree = uiTree;
        }

        public CaptureTarget Snapshot()
        {
            IntPtr foreground = Native.GetForegroundWindow();
            uint process; Native.GetWindowThreadProcessId(foreground, out process);
            return new CaptureTarget {
                Window = foreground, ProcessId = process,
                IsDesktop = Native.IsDesktop(foreground),
                IsLeaf = process != 0 && process == (uint)Process.GetCurrentProcess().Id
            };
        }
        public bool IsCurrent(CaptureTarget target)
        {
            return ForegroundIs(target);
        }
        // The real foreground comparison: a capture that merely knows some window exists is
        // not "the same foreground", so this never reports metadata it did not check.
        public static bool ForegroundIs(CaptureTarget target)
        {
            return target != null && target.Window != IntPtr.Zero && Native.GetForegroundWindow() == target.Window;
        }

        // Reading the old clipboard, trusting another process's copy, or restoring over a
        // newer copy are decided here so they can be checked without touching a desktop.
        public static bool CanReadFreshCopy(uint before, uint observed, bool foregroundSame, bool cancelled)
        {
            return !cancelled && foregroundSame && observed != before;
        }
        public static bool CanRestoreCopy(uint captured, uint current, bool foregroundSame, bool originTrusted)
        {
            return originTrusted && foregroundSame && captured != 0 && captured == current;
        }
        // A saved clipboard is usable only while the clipboard is still the one it was read
        // from: content the user copied in the meantime is newer than the save.
        public static bool CanKeepBackup(uint savedAt, uint current)
        {
            return savedAt != 0 && savedAt == current;
        }
        public static bool CanTrustOrigin(uint ownerProcess, uint expectedProcess)
        {
            return expectedProcess != 0 && ownerProcess == expectedProcess;
        }

        public Task<SelectionCaptureResult> ReadAutomationAsync(CaptureTarget target, CancellationToken cancellation)
        {
            if (Stopped(cancellation))
                return Task.FromResult(SelectionCaptureResult.NoText(CaptureStatus.Cancelled, CaptureReason.Cancelled));
            // A worker that never returns keeps its slot instead of piling up new threads.
            if (Interlocked.CompareExchange(ref automationRunning, 1, 0) != 0)
                return Task.FromResult(SelectionCaptureResult.Create(CaptureStatus.Unavailable, CaptureReason.AutomationBusy, "",
                    new List<CaptureStep> { new CaptureStep { Stage = "uia", Reason = CaptureReason.AutomationBusy, ForegroundSame = ForegroundIs(target) } }));
            var completion = new TaskCompletionSource<SelectionCaptureResult>();
            var owner = this;
            var worker = new Thread(() => {
                var steps = new List<CaptureStep>();
                SelectionCaptureResult outcome;
                try { outcome = Query(target, cancellation, steps, owner.tree); }
                catch (Exception error) {
                    steps.Add(new CaptureStep { Stage = "uia", Reason = CaptureReason.SystemFailure,
                        HResult = error.HResult, ForegroundSame = ForegroundIs(target) });
                    outcome = SelectionCaptureResult.Create(CaptureStatus.Unavailable, CaptureReason.SystemFailure, "", steps);
                }
                finally { Interlocked.Exchange(ref automationRunning, 0); }
                completion.TrySetResult(outcome);
            }) { IsBackground = true, Name = "Leaf UIA" };
            worker.SetApartmentState(ApartmentState.MTA); worker.Start();
            return completion.Task;
        }

        // Bounded to the captured window: the focused element and its ancestors first, then a
        // limited walk of the same window for a reading document. Control Name and Value are
        // never read. Every tree call can block on another application, so cancellation and
        // the time budget are checked between them, and the queue may never grow past the
        // node budget. A scan that could not finish never concludes "nothing is selected".
        private static SelectionCaptureResult Query(CaptureTarget target, CancellationToken cancellation, List<CaptureStep> steps, IUiTree tree)
        {
            var watch = Stopwatch.StartNew();
            IUiNode focused = null;
            try { focused = tree.Focused; } catch { }
            if (focused == null)
                return Finish(CaptureStatus.Unavailable, CaptureReason.UnsupportedPattern, steps, watch, 0, 0, 0, false, tree, target);
            if (target.ProcessId != 0 && focused.ProcessId != (int)target.ProcessId)
                return Finish(CaptureStatus.Unavailable, CaptureReason.ForegroundChanged, steps, watch, 0, 0, focused.ControlTypeId, false, tree, target);

            int visited = 0, candidates = 0;
            bool trustedEmpty = false, budgetSpent = false, patternSupported = false;
            int emptyType = 0, focusType = focused.ControlTypeId;

            // The focused element can belong to another window of the same process, so its chain
            // is walked up to the captured window before anything on it is read: a chain that
            // never reaches that window belongs somewhere else and may not answer for this one.
            // The walk is bounded like every other tree call.
            var chain = new List<IUiNode>();
            bool associated = false;
            IUiNode node = focused;
            for (int depth = 0; node != null && depth < AncestorDepth; depth++) {
                if (Stopped(cancellation)) return Cancelled(steps);
                if (watch.ElapsedMilliseconds > AutomationBudgetMs) { budgetSpent = true; break; }
                // Ancestors stay inside the captured process; the walk never leaves it.
                if (target.ProcessId != 0 && node.ProcessId != (int)target.ProcessId) break;
                visited++;
                chain.Add(node);
                if (IsCapturedWindow(node, target)) { associated = true; break; }
                node = Parent(node);
            }
            if (associated) {
                foreach (IUiNode candidate in chain) {
                    if (Stopped(cancellation)) return Cancelled(steps);
                    if (watch.ElapsedMilliseconds > AutomationBudgetMs) { budgetSpent = true; break; }
                    string selected = ReadCandidate(candidate, ref candidates, ref patternSupported, ref trustedEmpty, ref emptyType);
                    if (!string.IsNullOrWhiteSpace(selected)) return Success(selected, steps, watch, candidates, candidate.ControlTypeId, tree, target);
                    if (candidates >= MaxCandidates) { budgetSpent = true; break; }
                }
            }

            bool complete = !budgetSpent && candidates < MaxCandidates;
            IUiNode root = null;
            try { root = tree.Root(target.Window); } catch { }
            if (root == null) complete = false;
            if (root != null && complete && visited < MaxNodes) {
                var pending = new Queue<IUiNode>();
                pending.Enqueue(root);
                while (pending.Count > 0 && visited < MaxNodes && candidates < MaxCandidates) {
                    if (Stopped(cancellation)) return Cancelled(steps);
                    if (watch.ElapsedMilliseconds > AutomationBudgetMs) { budgetSpent = true; break; }
                    IUiNode current = pending.Dequeue(); visited++;
                    if (target.ProcessId != 0 && current.ProcessId != (int)target.ProcessId) continue;
                    string selected = ReadCandidate(current, ref candidates, ref patternSupported, ref trustedEmpty, ref emptyType);
                    if (!string.IsNullOrWhiteSpace(selected)) return Success(selected, steps, watch, candidates, current.ControlTypeId, tree, target);
                    // Siblings are enumerated under the same limits: a TreeWalker call can block
                    // on a foreign application, so each one is preceded by its own checks.
                    IUiNode child = FirstChild(current);
                    while (child != null) {
                        if (Stopped(cancellation)) return Cancelled(steps);
                        if (watch.ElapsedMilliseconds > AutomationBudgetMs) { budgetSpent = true; break; }
                        if (visited + pending.Count >= MaxNodes) { budgetSpent = true; break; }
                        pending.Enqueue(child);
                        child = NextSibling(child);
                    }
                    if (budgetSpent) break;
                }
                // A scan only counts as finished when nothing was left over for any reason.
                if (pending.Count > 0 || visited >= MaxNodes || candidates >= MaxCandidates) complete = false;
            }

            // Only a trusted document that finished its selection query may report "nothing
            // selected"; a truncated scan is inconclusive, never a confirmed empty selection.
            if (trustedEmpty && complete)
                return Finish(CaptureStatus.Empty, CaptureReason.EmptySelection, steps, watch, visited, candidates,
                    emptyType != 0 ? emptyType : focusType, patternSupported, tree, target);
            return Finish(CaptureStatus.Unavailable,
                complete ? CaptureReason.UnsupportedPattern : CaptureReason.AutomationBudget, steps, watch, visited, candidates,
                trustedEmpty && emptyType != 0 ? emptyType : focusType, patternSupported, tree, target);
        }

        // Reads one candidate. A node whose control type cannot be read is not a candidate;
        // a Document that completed an empty query is the only node that may conclude Empty.
        private static string ReadCandidate(IUiNode node, ref int candidates, ref bool patternSupported,
            ref bool trustedEmpty, ref int emptyType)
        {
            int type = node.ControlTypeId;
            if (type == 0) return null;
            candidates++;
            string selected;
            try { selected = node.ReadSelection(); } catch { return null; }
            if (selected == null) return null; // No usable text pattern on this node.
            patternSupported = true;
            if (string.IsNullOrWhiteSpace(selected) && type == DocumentTypeId) {
                trustedEmpty = true; emptyType = type;
            }
            return selected;
        }

        private static bool IsCapturedWindow(IUiNode node, CaptureTarget target)
        {
            if (target == null || target.Window == IntPtr.Zero) return false;
            return WindowHandle(node) == target.Window.ToInt64();
        }
        private static long WindowHandle(IUiNode node)
        {
            try { return node.NativeWindowHandle; } catch { return 0; }
        }
        private static IUiNode Parent(IUiNode node)
        {
            try { return node.Parent; } catch { return null; }
        }
        private static IUiNode FirstChild(IUiNode node)
        {
            try { return node.FirstChild; } catch { return null; }
        }
        private static IUiNode NextSibling(IUiNode node)
        {
            try { return node.NextSibling; } catch { return null; }
        }
        private static SelectionCaptureResult Success(string text, List<CaptureStep> steps, Stopwatch watch,
            int candidates, int controlTypeId, IUiTree tree, CaptureTarget target)
        {
            steps.Add(new CaptureStep { Stage = "uia", Reason = CaptureReason.None, CandidateCount = candidates,
                ControlTypeId = controlTypeId, PatternSupported = true, ForegroundSame = Same(tree, target),
                ElapsedMs = watch.ElapsedMilliseconds });
            return SelectionCaptureResult.Create(CaptureStatus.Text, CaptureReason.None,
                text.Length > 6001 ? text.Substring(0, 6001) : text, steps);
        }
        private static SelectionCaptureResult Finish(CaptureStatus status, CaptureReason reason, List<CaptureStep> steps,
            Stopwatch watch, int visited, int candidates, int controlTypeId, bool patternSupported,
            IUiTree tree, CaptureTarget target)
        {
            steps.Add(new CaptureStep { Stage = "uia", Reason = reason, CandidateCount = candidates,
                ControlTypeId = controlTypeId, PatternSupported = patternSupported,
                ForegroundSame = Same(tree, target), ElapsedMs = watch.ElapsedMilliseconds });
            return SelectionCaptureResult.Create(status, reason, "", steps);
        }
        private static bool Same(IUiTree tree, CaptureTarget target)
        {
            try { return tree.IsForeground(target); } catch { return false; }
        }
        private static SelectionCaptureResult Cancelled(List<CaptureStep> steps)
        {
            steps.Add(new CaptureStep { Stage = "uia", Reason = CaptureReason.Cancelled, ElapsedMs = 0 });
            return SelectionCaptureResult.Create(CaptureStatus.Cancelled, CaptureReason.Cancelled, "", steps);
        }
        private static bool Stopped(CancellationToken token)
        {
            try { return token.IsCancellationRequested; } catch (ObjectDisposedException) { return true; }
        }

        public Task<SelectionCaptureResult> CopySelectionAsync(CaptureTarget target, CancellationToken cancellation)
        {
            return Native.CopySelectionAsync(target, cancellation);
        }
        public Task<SelectionCaptureResult> ReadClipboardAsync(CaptureTarget target, CancellationToken cancellation)
        {
            return Native.ReadClipboardAsync(target, cancellation);
        }
    }

    // The real UI Automation tree behind IUiTree. Every call can fail on a foreign or busy
    // application, so each one is contained and simply yields no node.
    internal sealed class AutomationUiTree : IUiTree
    {
        public IUiNode Focused
        {
            get {
                AutomationElement element = null;
                try { element = AutomationElement.FocusedElement; } catch { }
                return element == null ? null : new AutomationUiNode(element);
            }
        }
        public IUiNode Root(IntPtr window)
        {
            AutomationElement element = null;
            try { element = AutomationElement.FromHandle(window); } catch { }
            return element == null ? null : new AutomationUiNode(element);
        }
        public bool IsForeground(CaptureTarget target) { return WindowsSelectionProbe.ForegroundIs(target); }
    }

    internal sealed class AutomationUiNode : IUiNode
    {
        private readonly AutomationElement element;
        public AutomationUiNode(AutomationElement element) { this.element = element; }
        public int ProcessId { get { try { return element.Current.ProcessId; } catch { return 0; } } }
        public int ControlTypeId { get { try { return element.Current.ControlType.Id; } catch { return 0; } } }
        public long NativeWindowHandle { get { try { return element.Current.NativeWindowHandle; } catch { return 0; } } }
        public IUiNode Parent { get { return Wrap(TreeWalker.ControlViewWalker.GetParent(element)); } }
        public IUiNode FirstChild { get { return Wrap(TreeWalker.ControlViewWalker.GetFirstChild(element)); } }
        public IUiNode NextSibling { get { return Wrap(TreeWalker.ControlViewWalker.GetNextSibling(element)); } }
        // Control Name and Value are never read, and no whole document is fetched.
        public string ReadSelection()
        {
            object pattern;
            try { if (!element.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) return null; }
            catch { return null; }
            try {
                var ranges = ((TextPattern)pattern).GetSelection();
                if (ranges == null) return "";
                var text = new StringBuilder();
                foreach (TextPatternRange range in ranges) {
                    if (range == null) continue;
                    text.Append(range.GetText(6001));
                    if (text.Length > 6001) break;
                }
                return text.ToString();
            } catch { return ""; }
        }
        private static IUiNode Wrap(AutomationElement element)
        {
            return element == null ? null : new AutomationUiNode(element);
        }
    }
}
