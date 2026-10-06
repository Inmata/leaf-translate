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
        // The selected text of this node, as a typed answer: an empty answer is a real one,
        // while a control that could not answer is unknown and never reported as empty.
        UiSelectionAnswer ReadSelection();
        IUiNode Parent { get; }
        IUiNode FirstChild { get; }
        IUiNode NextSibling { get; }
    }

    // What one node's selection read produced. An element that exposes no text pattern, one
    // whose query failed and one that really has nothing selected are three different answers,
    // and only the last of them may ever conclude that nothing is selected.
    public sealed class UiSelectionAnswer
    {
        private UiSelectionAnswer(string text, bool unreadable, int hResult)
        {
            Text = text; Unreadable = unreadable; HResult = hResult;
        }
        // Null unless the node answered with text or with a confirmed empty selection.
        public string Text { get; private set; }
        // True when a text pattern exists but did not answer, so the answer is unknown.
        public bool Unreadable { get; private set; }
        // The error code Windows reported for the failed call, 0 when it reported none.
        public int HResult { get; private set; }
        public static readonly UiSelectionAnswer Empty = new UiSelectionAnswer("", false, 0);
        public static readonly UiSelectionAnswer NoPattern = new UiSelectionAnswer(null, false, 0);
        public static UiSelectionAnswer Of(string text) { return new UiSelectionAnswer(text ?? "", false, 0); }
        public static UiSelectionAnswer Unknown(int hResult) { return new UiSelectionAnswer(null, true, hResult); }
    }

    // The tree the probe walks. A fixture implements it so the bounded traversal itself can
    // be checked off-desktop instead of only through a scripted ISelectionProbe.
    public interface IUiTree
    {
        IUiNode Focused { get; }
        // The window the foreground thread's keyboard focus is really on, read natively at
        // call time. IntPtr.Zero when Windows did not report one. This is the independent
        // witness for a focus whose UI Automation ancestor chain cannot be proven.
        IntPtr NativeFocusWindow { get; }
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
        public const int AutomationBudgetMs = 240;
        private const int AncestorDepth = 24;
        // How far the bounded ancestor chain may cross into other processes before it has to
        // have reached the captured window or a verified WebView host window inside it.
        public const int MaxForeignAncestors = 8;
        public static readonly int DocumentTypeId = ControlType.Document.Id;
        private static int automationRunning;
        private readonly IUiTree tree;
        private readonly IWindowOwnership ownership;

        public WindowsSelectionProbe() : this(new AutomationUiTree(), WindowOwnership.Native) { }
        public WindowsSelectionProbe(IUiTree uiTree) : this(uiTree, WindowOwnership.Native) { }
        public WindowsSelectionProbe(IUiTree uiTree, IWindowOwnership windowOwnership)
        {
            if (uiTree == null) throw new ArgumentNullException("uiTree");
            tree = uiTree;
            ownership = windowOwnership ?? WindowOwnership.Native;
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
                    new List<CaptureStep> { new CaptureStep { Stage = "uia", Reason = CaptureReason.AutomationBusy,
                        Checked = CaptureFields.Foreground, ForegroundSame = ForegroundIs(target) } }));
            var completion = new TaskCompletionSource<SelectionCaptureResult>();
            var owner = this;
            var worker = new Thread(() => {
                var steps = new List<CaptureStep>();
                SelectionCaptureResult outcome;
                try { outcome = Query(target, cancellation, steps, owner.tree, owner.ownership); }
                catch (Exception error) {
                    steps.Add(new CaptureStep { Stage = "uia", Reason = CaptureReason.SystemFailure,
                        Checked = CaptureFields.Foreground, HResult = error.HResult, ForegroundSame = ForegroundIs(target) });
                    outcome = SelectionCaptureResult.Create(CaptureStatus.Unavailable, CaptureReason.SystemFailure, "", steps);
                }
                finally { Interlocked.Exchange(ref automationRunning, 0); }
                completion.TrySetResult(outcome);
            }) { IsBackground = true, Name = "Leaf UIA" };
            worker.SetApartmentState(ApartmentState.MTA); worker.Start();
            return completion.Task;
        }

        // The rule a selection read follows, with the two AutomationElement calls supplied by
        // the caller so the real policy can be checked without a desktop. A query that threw,
        // one that returned no range list at all and a range that failed are all unknown
        // answers: only a query that really answered with nothing is a confirmed empty one.
        public static UiSelectionAnswer ReadSelectionAnswer(Func<object[]> ranges, Func<object, string> rangeText)
        {
            object[] selection;
            try { selection = ranges(); }
            catch (Exception error) { return UiSelectionAnswer.Unknown(error.HResult); }
            if (selection == null) return UiSelectionAnswer.Unknown(0);
            var text = new StringBuilder();
            foreach (object range in selection) {
                if (range == null) continue;
                string piece;
                try { piece = rangeText(range); }
                catch (Exception error) { return UiSelectionAnswer.Unknown(error.HResult); }
                if (piece == null) return UiSelectionAnswer.Unknown(0);
                text.Append(piece);
                if (text.Length > 6001) break;
            }
            return UiSelectionAnswer.Of(text.ToString());
        }

        // The selection read asks for TextPattern first; a provider that exposes only
        // TextPattern2 still answers, because TextPattern2 is the same selection surface with
        // extra caret calls on top of it. The caller keeps whatever the provider answered or
        // threw, so an unavailable pattern stays an unknown answer instead of an empty one.
        public static object ChooseTextPattern(Func<object> textPattern, Func<object> textPattern2)
        {
            if (textPattern == null) return null;
            object chosen = textPattern();
            if (chosen != null || textPattern2 == null) return chosen;
            return textPattern2();
        }

        // The running state of one bounded scan, so the read policy records what it saw
        // without a growing parameter list.
        private sealed class Scan
        {
            public int Visited, Candidates;
            public bool PatternSupported, TrustedEmpty, Unreadable;
            public int EmptyType, ReadError;
        }

        // One node of the window walk plus whether it was shown to hang inside a verified
        // WebView host, which carries its whole subtree with it.
        private struct WalkNode
        {
            public IUiNode Node;
            public bool InsideVerifiedHost;
        }

        // Bounded to the captured window: the focused element and its ancestors first, then a
        // limited walk of the same window for a reading document. Control Name and Value are
        // never read. Every tree call can block on another application, so cancellation and
        // the time budget are checked between them, and the queue may never grow past the
        // node budget. A scan that could not finish never concludes "nothing is selected".
        private static SelectionCaptureResult Query(CaptureTarget target, CancellationToken cancellation,
            List<CaptureStep> steps, IUiTree tree, IWindowOwnership ownership)
        {
            var watch = Stopwatch.StartNew();
            var scan = new Scan();
            IUiNode focused = null;
            try { focused = tree.Focused; } catch { }
            if (focused == null)
                return Finish(CaptureStatus.Unavailable, CaptureReason.UnsupportedPattern, steps, watch, scan, 0, false, tree, target);

            bool budgetSpent = false;
            int focusType = focused.ControlTypeId;
            bool focusedSameProcess = SameProcess(focused, target);

            // The focused element can belong to another window of the same process, or - inside
            // a WebView2 - to another process that is natively part of the captured window. Its
            // chain is walked up to the captured window, or to a verified WebView host window
            // under it, before anything on it is read: a chain that reaches neither belongs
            // somewhere else and may not answer for this window. The walk is bounded like every
            // other tree call.
            var chain = new List<IUiNode>();
            bool associated = false;
            int foreign = 0;
            IUiNode node = focused;
            for (int depth = 0; node != null && depth < AncestorDepth; depth++) {
                if (Stopped(cancellation)) return Cancelled(steps);
                if (watch.ElapsedMilliseconds > AutomationBudgetMs) { budgetSpent = true; break; }
                bool sameProcess = SameProcess(node, target);
                if (!sameProcess) {
                    // Crossing into another process is allowed only while the chain can still be
                    // shown to hang under the captured window; the hop limit keeps that finite.
                    foreign++;
                    if (foreign > MaxForeignAncestors) break;
                }
                scan.Visited++;
                chain.Add(node);
                if (IsCapturedWindow(node, target)) { associated = true; break; }
                // A verified WebView host window inside the captured window is the boundary
                // another process may answer across: the window identity decides, not the pid.
                if (!sameProcess && WindowOwnership.IsVerifiedHostWindow(ownership, target.Window, WindowHandlePtr(node))) {
                    associated = true; break;
                }
                node = Parent(node);
            }
            // A focus that could not be shown to belong to the captured window is not a
            // foreground change, and this window's own tree must not answer for it either.
            // The native keyboard focus is an independent witness: when it sits on a verified
            // WebView host window inside the captured one - read live, for this invocation,
            // not from a cached process id or class - a bounded selection query may run on
            // that host's subtree. A window identity decides, never a process family.
            IntPtr hostSubtree = IntPtr.Zero;
            if (!associated && !focusedSameProcess) {
                IntPtr nativeFocus = IntPtr.Zero;
                try { nativeFocus = tree.NativeFocusWindow; } catch { }
                hostSubtree = nativeFocus == IntPtr.Zero ? IntPtr.Zero
                    : WindowOwnership.VerifiedHostOf(ownership, target == null ? IntPtr.Zero : target.Window, nativeFocus);
                if (hostSubtree == IntPtr.Zero)
                    return Finish(CaptureStatus.Unavailable, CaptureReason.OwnershipUnproven, steps, watch, scan, focusType, false, tree, target);
            }

            if (associated) {
                // Which chain nodes may answer: the captured window's own process, and the
                // content that hangs under a verified WebView host inside the captured window.
                bool[] mayAnswer = ChainOwnership(chain, target, ownership);
                for (int i = 0; i < chain.Count; i++) {
                    if (Stopped(cancellation)) return Cancelled(steps);
                    if (watch.ElapsedMilliseconds > AutomationBudgetMs) { budgetSpent = true; break; }
                    if (!mayAnswer[i]) continue;
                    string selected = ReadCandidate(chain[i], scan);
                    if (!string.IsNullOrWhiteSpace(selected)) return Success(selected, steps, watch, scan, chain[i].ControlTypeId, tree, target);
                    if (scan.Candidates >= MaxCandidates) { budgetSpent = true; break; }
                }
            }

            bool complete = !budgetSpent && scan.Candidates < MaxCandidates;
            IUiNode root = null;
            try { root = tree.Root(target.Window); } catch { }
            if (root == null) {
                complete = false;
                if (hostSubtree != IntPtr.Zero && !budgetSpent)
                    return Finish(CaptureStatus.Unavailable, CaptureReason.OwnershipUnproven, steps, watch, scan, focusType, false, tree, target);
            }
            // In witness mode the query is confined to the verified host's own subtree: the
            // element that carries the host window is located first, before anything is read.
            IUiNode start = root;
            bool startInsideHost = false;
            if (root != null && hostSubtree != IntPtr.Zero) {
                var locate = new Queue<IUiNode>();
                locate.Enqueue(root);
                while (locate.Count > 0 && scan.Visited < MaxNodes) {
                    if (Stopped(cancellation)) return Cancelled(steps);
                    if (watch.ElapsedMilliseconds > AutomationBudgetMs) { budgetSpent = true; break; }
                    IUiNode current = locate.Dequeue(); scan.Visited++;
                    if (WindowHandle(current) == hostSubtree.ToInt64()) { start = current; startInsideHost = true; break; }
                    IUiNode child = FirstChild(current);
                    while (child != null) {
                        if (Stopped(cancellation)) return Cancelled(steps);
                        if (watch.ElapsedMilliseconds > AutomationBudgetMs) { budgetSpent = true; break; }
                        if (scan.Visited + locate.Count >= MaxNodes) { budgetSpent = true; break; }
                        locate.Enqueue(child);
                        child = NextSibling(child);
                    }
                    if (budgetSpent) break;
                }
                // The subtree the native focus vouches for is not exposed by this tree: it
                // stays unproven rather than falling back to the rest of the window.
                if (!startInsideHost && !budgetSpent)
                    return Finish(CaptureStatus.Unavailable, CaptureReason.OwnershipUnproven, steps, watch, scan, focusType, false, tree, target);
                if (!startInsideHost) complete = false;
            }
            if (start != null && complete && scan.Visited < MaxNodes) {
                var pending = new Queue<WalkNode>();
                pending.Enqueue(new WalkNode { Node = start, InsideVerifiedHost = startInsideHost });
                while (pending.Count > 0 && scan.Visited < MaxNodes && scan.Candidates < MaxCandidates) {
                    if (Stopped(cancellation)) return Cancelled(steps);
                    if (watch.ElapsedMilliseconds > AutomationBudgetMs) { budgetSpent = true; break; }
                    WalkNode current = pending.Dequeue(); scan.Visited++;
                    // Only the captured window's own process, and the content hanging under a
                    // verified WebView host inside it, may answer for this window.
                    if (!SameProcess(current.Node, target) && !current.InsideVerifiedHost) continue;
                    string selected = ReadCandidate(current.Node, scan);
                    if (!string.IsNullOrWhiteSpace(selected)) return Success(selected, steps, watch, scan, current.Node.ControlTypeId, tree, target);
                    // Siblings are enumerated under the same limits: a TreeWalker call can block
                    // on a foreign application, so each one is preceded by its own checks.
                    IUiNode child = FirstChild(current.Node);
                    while (child != null) {
                        if (Stopped(cancellation)) return Cancelled(steps);
                        if (watch.ElapsedMilliseconds > AutomationBudgetMs) { budgetSpent = true; break; }
                        if (scan.Visited + pending.Count >= MaxNodes) { budgetSpent = true; break; }
                        pending.Enqueue(new WalkNode {
                            Node = child,
                            InsideVerifiedHost = current.InsideVerifiedHost || IsVerifiedHost(child, target, ownership)
                        });
                        child = NextSibling(child);
                    }
                    // A child call that blocked long enough to spend the budget leaves the rest
                    // of that window unexamined even when it handed back no child at all.
                    if (!budgetSpent && watch.ElapsedMilliseconds > AutomationBudgetMs) budgetSpent = true;
                    if (budgetSpent) break;
                }
                // A scan only counts as finished when nothing was left over for any reason.
                if (pending.Count > 0 || scan.Visited >= MaxNodes || scan.Candidates >= MaxCandidates) complete = false;
            }
            if (Stopped(cancellation)) return Cancelled(steps);
            // Time that ran out is an unfinished scan even when nothing was left pending: the
            // call that spent it may have been the one that would have found the selection.
            if (budgetSpent || watch.ElapsedMilliseconds > AutomationBudgetMs) complete = false;

            // Only a trusted document that finished its selection query may report "nothing
            // selected"; a truncated scan is inconclusive, never a confirmed empty selection.
            if (scan.TrustedEmpty && complete)
                return Finish(CaptureStatus.Empty, CaptureReason.EmptySelection, steps, watch, scan,
                    scan.EmptyType != 0 ? scan.EmptyType : focusType, scan.PatternSupported, tree, target);
            return Finish(CaptureStatus.Unavailable,
                complete ? (scan.Unreadable && !scan.PatternSupported ? CaptureReason.AutomationUnreadable : CaptureReason.UnsupportedPattern)
                    : CaptureReason.AutomationBudget, steps, watch, scan,
                scan.TrustedEmpty && scan.EmptyType != 0 ? scan.EmptyType : focusType, scan.PatternSupported, tree, target);
        }

        // The chain is ordered from the focused element upwards. Only the captured window's own
        // process, a verified WebView host window, and whatever hangs under such a host may
        // answer: a foreign node between them is not this window's content.
        private static bool[] ChainOwnership(List<IUiNode> chain, CaptureTarget target, IWindowOwnership ownership)
        {
            var mayAnswer = new bool[chain.Count];
            bool insideHost = false;
            for (int i = chain.Count - 1; i >= 0; i--) {
                bool host = IsVerifiedHost(chain[i], target, ownership);
                insideHost = insideHost || host;
                mayAnswer[i] = SameProcess(chain[i], target) || host || insideHost;
            }
            return mayAnswer;
        }

        private static bool SameProcess(IUiNode node, CaptureTarget target)
        {
            if (node == null) return false;
            if (target == null || target.ProcessId == 0) return true;
            try { return node.ProcessId == (int)target.ProcessId; } catch { return false; }
        }
        // A node of another process that carries a verified WebView host window inside the
        // captured one. Its own pid proves nothing either way.
        private static bool IsVerifiedHost(IUiNode node, CaptureTarget target, IWindowOwnership ownership)
        {
            if (node == null || target == null || SameProcess(node, target)) return false;
            return WindowOwnership.IsVerifiedHostWindow(ownership, target.Window, WindowHandlePtr(node));
        }

        // Reads one candidate. A node whose control type cannot be read is not a candidate; a
        // node that could not answer is recorded as such and is never a confirmed empty one.
        private static string ReadCandidate(IUiNode node, Scan scan)
        {
            int type = node.ControlTypeId;
            if (type == 0) return null;
            scan.Candidates++;
            UiSelectionAnswer answer;
            try { answer = node.ReadSelection(); }
            catch (Exception error) {
                scan.Unreadable = true; scan.ReadError = error.HResult; return null;
            }
            if (answer == null) { scan.Unreadable = true; return null; }
            if (answer.Text == null) {
                // A control with no text pattern at all is a capability answer; one that has a
                // pattern but did not answer is unknown, and neither may conclude Empty.
                scan.Unreadable = true;
                if (answer.HResult != 0) scan.ReadError = answer.HResult;
                return null;
            }
            scan.PatternSupported = true;
            if (string.IsNullOrWhiteSpace(answer.Text) && type == DocumentTypeId) {
                scan.TrustedEmpty = true; scan.EmptyType = type;
            }
            return answer.Text;
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
        private static IntPtr WindowHandlePtr(IUiNode node)
        {
            return new IntPtr(WindowHandle(node));
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
            Scan scan, int controlTypeId, IUiTree tree, CaptureTarget target)
        {
            steps.Add(new CaptureStep { Stage = "uia", Reason = CaptureReason.None, Checked = CaptureFields.Foreground | CaptureFields.Read,
                CandidateCount = scan.Candidates,
                ControlTypeId = controlTypeId, PatternSupported = true, ForegroundSame = Same(tree, target),
                ElapsedMs = watch.ElapsedMilliseconds });
            return SelectionCaptureResult.Create(CaptureStatus.Text, CaptureReason.None,
                text.Length > 6001 ? text.Substring(0, 6001) : text, steps);
        }
        private static SelectionCaptureResult Finish(CaptureStatus status, CaptureReason reason, List<CaptureStep> steps,
            Stopwatch watch, Scan scan, int controlTypeId, bool patternSupported,
            IUiTree tree, CaptureTarget target)
        {
            steps.Add(new CaptureStep { Stage = "uia", Reason = reason, Checked = CaptureFields.Foreground | CaptureFields.Read,
                CandidateCount = scan.Candidates,
                ControlTypeId = controlTypeId, PatternSupported = patternSupported,
                // An unreadable query reports the code Windows gave it instead of losing it.
                HResult = scan.ReadError,
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
        // The real keyboard focus of the foreground thread, read at call time.
        public IntPtr NativeFocusWindow { get { return Native.ForegroundFocusWindow(); } }
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
        // Control Name and Value are never read, and no whole document is fetched. A pattern
        // that cannot answer is reported as unknown instead of as an empty selection.
        public UiSelectionAnswer ReadSelection()
        {
            object pattern;
            try {
                pattern = WindowsSelectionProbe.ChooseTextPattern(
                    () => { object found; return element.TryGetCurrentPattern(TextPattern.Pattern, out found) ? found : null; },
                    // TextPattern2 is the same selection surface with extra caret calls, and this
                    // client library has no managed wrapper for it: it is asked for by its own
                    // pattern id, and only an answer the selection read can actually use counts.
                    // A client that does not know the id leaves the capability unknown.
                    () => {
                        var extension = ExtensionTextPattern;
                        if (extension == null) return null;
                        try { object found; return element.TryGetCurrentPattern(extension, out found) ? found : null; }
                        catch { return null; }
                    });
            } catch (Exception error) { return UiSelectionAnswer.Unknown(error.HResult); }
            if (pattern == null) return UiSelectionAnswer.NoPattern;
            var text = pattern as TextPattern;
            if (text == null) return UiSelectionAnswer.NoPattern;
            return WindowsSelectionProbe.ReadSelectionAnswer(
                () => text.GetSelection(),
                range => ((TextPatternRange)range).GetText(6001));
        }
        // TextPattern2 is not part of this framework's client library, so its pattern id is
        // resolved once and stays unknown when it cannot be.
        private static readonly AutomationPattern ExtensionTextPattern = LookupExtensionTextPattern();
        private static AutomationPattern LookupExtensionTextPattern()
        {
            try { return AutomationPattern.LookupById(10024); } catch { return null; }
        }
        private static IUiNode Wrap(AutomationElement element)
        {
            return element == null ? null : new AutomationUiNode(element);
        }
    }
}
