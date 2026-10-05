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
    // The Windows implementation of ISelectionProbe. The foreground target is taken
    // before the popup is shown, so the popup can never become the capture source.
    public sealed class WindowsSelectionProbe : ISelectionProbe
    {
        public const int MaxNodes = 128;
        public const int MaxCandidates = 32;
        // Slightly below the coordinator's budget so a bounded scan can report its own end.
        private const int AutomationBudgetMs = 240;
        private const int AncestorDepth = 24;
        private static int automationRunning;

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
            return target != null && target.Window != IntPtr.Zero && Native.GetForegroundWindow() == target.Window;
        }

        // Reading the old clipboard, trusting another process's copy, or restoring over a
        // newer copy are decided here so they can be checked without touching a desktop.
        public static bool CanReadFreshCopy(uint before, uint observed, bool foregroundSame, bool cancelled)
        {
            return !cancelled && foregroundSame && observed != before;
        }
        public static bool CanRestoreCopy(uint captured, uint current, bool foregroundSame)
        {
            return foregroundSame && captured != 0 && captured == current;
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
                    new List<CaptureStep> { new CaptureStep { Stage = "uia", Reason = CaptureReason.AutomationBusy } }));
            var completion = new TaskCompletionSource<SelectionCaptureResult>();
            var worker = new Thread(() => {
                var steps = new List<CaptureStep>();
                SelectionCaptureResult outcome;
                try { outcome = Query(target, cancellation, steps); }
                catch (Exception error) {
                    steps.Add(new CaptureStep { Stage = "uia", Reason = CaptureReason.SystemFailure, HResult = error.HResult });
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
        // never read, and no unbounded FindAll is used.
        private static SelectionCaptureResult Query(CaptureTarget target, CancellationToken cancellation, List<CaptureStep> steps)
        {
            var watch = Stopwatch.StartNew();
            AutomationElement focused = null;
            try { focused = AutomationElement.FocusedElement; } catch { }
            if (focused == null) return Finish(CaptureStatus.Unavailable, CaptureReason.UnsupportedPattern, steps, watch, 0, 0);
            int focusedProcess = 0;
            try { focusedProcess = focused.Current.ProcessId; } catch { }
            if (target.ProcessId != 0 && focusedProcess != (int)target.ProcessId)
                return Finish(CaptureStatus.Unavailable, CaptureReason.ForegroundChanged, steps, watch, 0, 0);

            int visited = 0, candidates = 0;
            bool trustedEmpty = false, budgetSpent = false;
            AutomationElement node = focused;
            for (int depth = 0; node != null && depth < AncestorDepth; depth++) {
                if (Stopped(cancellation)) return Cancelled(steps);
                if (watch.ElapsedMilliseconds > AutomationBudgetMs) { budgetSpent = true; break; }
                visited++;
                var selected = ReadSelection(node, ref candidates, ref trustedEmpty);
                if (!string.IsNullOrWhiteSpace(selected)) return Success(selected, steps, watch, visited, candidates);
                if (candidates >= MaxCandidates) break;
                node = Parent(node);
            }

            AutomationElement root = null;
            try { root = AutomationElement.FromHandle(target.Window); } catch { }
            if (root != null && !budgetSpent && candidates < MaxCandidates) {
                var walker = TreeWalker.ControlViewWalker;
                var pending = new Queue<AutomationElement>(); pending.Enqueue(root);
                while (pending.Count > 0 && visited < MaxNodes && candidates < MaxCandidates) {
                    if (Stopped(cancellation)) return Cancelled(steps);
                    if (watch.ElapsedMilliseconds > AutomationBudgetMs) { budgetSpent = true; break; }
                    AutomationElement current = pending.Dequeue(); visited++;
                    int process = 0;
                    try { process = current.Current.ProcessId; } catch { }
                    if (target.ProcessId != 0 && process != (int)target.ProcessId) continue;
                    var selected = ReadSelection(current, ref candidates, ref trustedEmpty);
                    if (!string.IsNullOrWhiteSpace(selected)) return Success(selected, steps, watch, visited, candidates);
                    AutomationElement child = null;
                    try { child = walker.GetFirstChild(current); } catch { }
                    while (child != null) {
                        pending.Enqueue(child);
                        try { child = walker.GetNextSibling(child); } catch { child = null; }
                    }
                }
            }

            // Only a trusted document that finished its selection query may report "nothing selected".
            if (trustedEmpty && !budgetSpent)
                return Finish(CaptureStatus.Empty, CaptureReason.EmptySelection, steps, watch, visited, candidates);
            return Finish(CaptureStatus.Unavailable,
                budgetSpent ? CaptureReason.AutomationBudget : CaptureReason.UnsupportedPattern, steps, watch, visited, candidates);
        }

        // Returns the selected text of one candidate. A candidate that supports TextPattern and
        // is a Document may conclude "empty" when its query completed without an exception.
        private static string ReadSelection(AutomationElement node, ref int candidates, ref bool trustedEmpty)
        {
            int controlType = 0;
            try { controlType = node.Current.ControlType.Id; } catch { return null; }
            object pattern; bool supported;
            candidates++;
            try { supported = node.TryGetCurrentPattern(TextPattern.Pattern, out pattern); } catch { return null; }
            if (!supported) return null;
            try {
                var ranges = ((TextPattern)pattern).GetSelection();
                if (ranges == null) return null;
                var text = new StringBuilder();
                foreach (TextPatternRange range in ranges) {
                    if (range == null) continue;
                    text.Append(range.GetText(6001));
                    if (text.Length > 6001) break;
                }
                string selected = text.ToString();
                if (string.IsNullOrWhiteSpace(selected) && controlType == ControlType.Document.Id) trustedEmpty = true;
                return selected;
            } catch { return null; }
        }

        private static AutomationElement Parent(AutomationElement node)
        {
            try { return TreeWalker.ControlViewWalker.GetParent(node); } catch { return null; }
        }
        private static SelectionCaptureResult Success(string text, List<CaptureStep> steps, Stopwatch watch, int visited, int candidates)
        {
            steps.Add(new CaptureStep { Stage = "uia", Reason = CaptureReason.None, CandidateCount = candidates,
                PatternSupported = true, ForegroundSame = true, ElapsedMs = watch.ElapsedMilliseconds });
            return SelectionCaptureResult.Create(CaptureStatus.Text, CaptureReason.None,
                text.Length > 6001 ? text.Substring(0, 6001) : text, steps);
        }
        private static SelectionCaptureResult Finish(CaptureStatus status, CaptureReason reason, List<CaptureStep> steps,
            Stopwatch watch, int visited, int candidates)
        {
            steps.Add(new CaptureStep { Stage = "uia", Reason = reason, CandidateCount = candidates,
                PatternSupported = false, ForegroundSame = Native.GetForegroundWindow() != IntPtr.Zero,
                ElapsedMs = watch.ElapsedMilliseconds });
            return SelectionCaptureResult.Create(status, reason, "", steps);
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
}
