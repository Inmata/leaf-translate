using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Leaf
{
    public static class SelectionCopy
    {

        // One safe simulated copy against the window this invocation was started for.
        // The worker owns the deadline source, so a worker that finishes after the
        // caller gave up still sees a revoked token and never injects a late copy.
        public static Task<SelectionCaptureResult> CopySelectionAsync(CaptureTarget target, CancellationToken cancellation)
        {
            return CopySelectionAsync(target, cancellation, new DesktopCopyHost(), WindowOwnership.Native);
        }
        // The same worker against a supplied host, so a scripted desktop can exercise the
        // real sequence, ownership, cancellation and restore rules.
        public static Task<SelectionCaptureResult> CopySelectionAsync(CaptureTarget target, CancellationToken cancellation, ICopyHost host)
        {
            return CopySelectionAsync(target, cancellation, host, WindowOwnership.Native);
        }
        public static Task<SelectionCaptureResult> CopySelectionAsync(CaptureTarget target, CancellationToken cancellation,
            ICopyHost host, IWindowOwnership ownership)
        {
            if (host == null) throw new ArgumentNullException("host");
            if (Stopped(cancellation))
                return Task.FromResult(SelectionCaptureResult.NoText(CaptureStatus.Cancelled, CaptureReason.Cancelled));
            if (!ClipboardAccess.TryBeginCapture())
                return Task.FromResult(Step(CaptureStatus.Unavailable, CaptureReason.CaptureBusy, "copy_wait", CaptureReason.CaptureBusy));
            var completion = new TaskCompletionSource<SelectionCaptureResult>();
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var worker = new Thread(() => {
                var steps = new List<CaptureStep>();
                SelectionCaptureResult outcome;
                try { outcome = CopyOnSta(target, deadline, steps, host, ownership); }
                catch (Exception error) { outcome = CopyFailure(error, steps); }
                finally { ClipboardAccess.EndCapture(); }
                completion.TrySetResult(outcome);
                deadline.Dispose();
            }) { IsBackground = true, Name = "Leaf capture" };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
            return AwaitCopyAsync(completion.Task, deadline);
        }

        private static async Task<SelectionCaptureResult> AwaitCopyAsync(Task<SelectionCaptureResult> worker, CancellationTokenSource deadline)
        {
            if (await Task.WhenAny(worker, Task.Delay(1600)) == worker) return await worker;
            // Revoke the token first so the worker cannot inject anything it had not sent yet.
            try { deadline.Cancel(); } catch (ObjectDisposedException) { }
            return Step(CaptureStatus.Unavailable, CaptureReason.ClipboardBusy, "copy_wait", CaptureReason.ClipboardBusy);
        }

        // The copy worker's decisions, separated from the thread that hosts it so a scripted
        // host can exercise them: no real clipboard, keyboard or foreground is involved.
        public static SelectionCaptureResult CopyOnSta(CaptureTarget target, CancellationTokenSource deadline, List<CaptureStep> steps, ICopyHost host)
        {
            return CopyOnSta(target, deadline, steps, host, WindowOwnership.Native);
        }
        public static SelectionCaptureResult CopyOnSta(CaptureTarget target, CancellationTokenSource deadline,
            List<CaptureStep> steps, ICopyHost host, IWindowOwnership ownership)
        {
            if (host == null) throw new ArgumentNullException("host");
            ownership = ownership ?? WindowOwnership.Native;
            var watch = Stopwatch.StartNew();
            long mark = 0;
            // Wait for the shortcut keys to come up, but never release the user's own keys.
            bool released = WaitForModifiers(deadline.Token, 600, host);
            steps.Add(new CaptureStep {
                Stage = "copy_wait", Reason = released ? CaptureReason.None : CaptureReason.ModifiersHeld,
                Checked = CaptureFields.Foreground | CaptureFields.Modifiers,
                ModifiersReleased = released, ForegroundSame = host.ForegroundWindow == target.Window,
                ElapsedMs = Next(watch, ref mark)
            });
            if (Stopped(deadline.Token)) return Stopped(steps);
            if (!released) return Stepped(CaptureStatus.Unavailable, CaptureReason.ModifiersHeld, steps);
            if (host.ForegroundWindow != target.Window) return Stepped(CaptureStatus.Unavailable, CaptureReason.ForegroundChanged, steps);

            // The old clipboard is saved before it is overwritten. A clipboard that could not
            // be read is unknown, never empty, so a failed save must not turn into a clear. It
            // is read on both sides of the save: a clipboard the user changed while it was
            // being read is one this capture does not replace, so the stale save is dropped
            // instead of being put back over the newer content later.
            uint backedUpAt = host.ClipboardSequence;
            ClipboardBackup previous = null;
            try { previous = host.Backup(); } catch { previous = null; }
            if (previous != null && !WindowsSelectionProbe.CanKeepBackup(backedUpAt, host.ClipboardSequence)) previous = null;
            if (Stopped(deadline.Token)) return Stopped(steps);
            if (host.ForegroundWindow != target.Window) return Stepped(CaptureStatus.Unavailable, CaptureReason.ForegroundChanged, steps);

            // The baseline of "a new copy" is taken as late as possible: anything the clipboard
            // held before this instant is not the copy this invocation asked for. The same read
            // decides whether the saved clipboard still describes the one about to be replaced.
            uint before = host.ClipboardSequence;
            if (previous != null && !WindowsSelectionProbe.CanKeepBackup(backedUpAt, before)) previous = null;

            // Everything the injected copy depends on is re-checked as late as possible before
            // the keys are sent: the clipboard backup above can block on another application,
            // so the user's keys may have come down again and the keyboard focus may have left
            // the captured window. The keys are never released and nothing is retried - a state
            // that would send this copy somewhere else stops it instead.
            bool releasedNow;
            try { releasedNow = !host.ModifiersHeld(); } catch { releasedNow = false; }
            IntPtr focusWindow;
            try { focusWindow = host.FocusedWindow; } catch { focusWindow = IntPtr.Zero; }
            bool focusChecked = focusWindow != IntPtr.Zero;
            bool focusOwned = focusChecked && WindowOwnership.Contains(ownership, target == null ? IntPtr.Zero : target.Window, focusWindow);
            bool foregroundNow;
            try { foregroundNow = host.ForegroundWindow == target.Window; } catch { foregroundNow = false; }
            steps.Add(new CaptureStep {
                Stage = "copy_focus",
                Reason = !releasedNow ? CaptureReason.ModifiersHeld
                    : !foregroundNow ? CaptureReason.ForegroundChanged
                    : focusChecked && !focusOwned ? CaptureReason.FocusNotOwned : CaptureReason.None,
                Checked = CaptureFields.Foreground | CaptureFields.Modifiers | CaptureFields.Focus,
                ModifiersReleased = releasedNow, ForegroundSame = foregroundNow,
                FocusChecked = focusChecked, FocusOwned = focusOwned, ElapsedMs = Next(watch, ref mark)
            });
            // The getters above can block on another application, so the token is read once more
            // immediately before the injected copy and never after it.
            if (Stopped(deadline.Token)) return Stopped(steps);
            if (!releasedNow) return Stepped(CaptureStatus.Unavailable, CaptureReason.ModifiersHeld, steps);
            if (!foregroundNow) return Stepped(CaptureStatus.Unavailable, CaptureReason.ForegroundChanged, steps);
            if (focusChecked && !focusOwned) return Stepped(CaptureStatus.Unavailable, CaptureReason.FocusNotOwned, steps);

            bool sent = host.SendCopy();
            steps.Add(new CaptureStep {
                Stage = "copy_send", Reason = sent ? CaptureReason.None : CaptureReason.InputDenied,
                Checked = CaptureFields.Foreground | CaptureFields.Cleanup,
                NativeError = sent ? 0 : host.SendError, ForegroundSame = host.ForegroundWindow == target.Window,
                // A refused copy reports the real outcome of lifting its own keys, including a
                // lift that failed, instead of claiming the desktop was left clean.
                KeyCleanupFailed = host.KeyCleanupFailed, KeyCleanupError = host.KeyCleanupError,
                ElapsedMs = Next(watch, ref mark)
            });
            if (!sent) return Stepped(CaptureStatus.Failed, CaptureReason.InputDenied, steps);

            // Only a new sequence owned by the captured process - or by a window it verifiably
            // contains - may be read as this copy, and the same constraints are checked again
            // after the read: a slow clipboard read must not hand the worker text the user
            // copied in the meantime.
            uint captured = 0; string text = ""; Exception readError = null;
            bool sequenceMoved = false, textRaced = false, ownerWindowVerified = false;
            uint ownerProcess = 0;
            var copyWatch = Stopwatch.StartNew();
            while (copyWatch.ElapsedMilliseconds < 400) {
                host.Pause(20);
                uint observed = host.ClipboardSequence;
                if (observed == before) continue;
                sequenceMoved = true;
                bool foregroundSame = host.ForegroundWindow == target.Window;
                if (!WindowsSelectionProbe.CanReadFreshCopy(before, observed, foregroundSame, Stopped(deadline.Token))) break;
                bool verified;
                if (!TrustedOrigin(host, target, ownership, out verified, out ownerProcess)) { ownerWindowVerified = verified; continue; }
                ownerWindowVerified = verified;
                captured = observed;
                try { text = host.ReadClipboardText(); readError = null; }
                catch (Exception error) { text = ""; readError = error; }
                // The owner is part of "this is my copy": content that changed hands while it
                // was being read is not this selection, and it is not restored over either.
                if (host.ClipboardSequence != captured ||
                    !TrustedOrigin(host, target, ownership, out verified, out ownerProcess) ||
                    host.ForegroundWindow != target.Window || Stopped(deadline.Token)) {
                    // The clipboard moved on while reading: what came back is not this copy.
                    text = ""; readError = null; textRaced = true; break;
                }
                if (readError == null && !string.IsNullOrWhiteSpace(text)) break;
            }
            bool readFailed = readError != null && string.IsNullOrWhiteSpace(text);
            steps.Add(new CaptureStep {
                Stage = "clipboard_read",
                Reason = captured == 0
                    ? (sequenceMoved ? CaptureReason.ClipboardOriginUnknown : CaptureReason.CopyNoUpdate)
                    : textRaced ? CaptureReason.ClipboardOriginUnknown
                    : readFailed ? CaptureReason.ClipboardBusy : CaptureReason.None,
                Checked = CaptureFields.Foreground | CaptureFields.Clipboard | CaptureFields.Owner,
                HResult = readFailed ? readError.HResult : 0,
                ClipboardChanged = captured != 0, ForegroundSame = host.ForegroundWindow == target.Window,
                ClipboardOwnerProcess = ownerProcess, OwnerWindowVerified = ownerWindowVerified,
                ElapsedMs = Next(watch, ref mark)
            });

            // A cancelled worker stops here: the clipboard is never written after its deadline.
            if (Stopped(deadline.Token)) return Stopped(steps);
            if (captured != 0) RestoreClipboard(target, host, previous, captured, deadline.Token, steps, watch, ref mark, ownership);
            // A restore that was itself interrupted leaves this invocation cancelled, so the
            // text it happened to read is not handed on as a completed capture.
            if (Stopped(deadline.Token)) return Stopped(steps);

            if (captured == 0)
                return Stepped(CaptureStatus.Unavailable, sequenceMoved ? CaptureReason.ClipboardOriginUnknown : CaptureReason.CopyNoUpdate, steps);
            if (textRaced) return Stepped(CaptureStatus.Unavailable, CaptureReason.ClipboardOriginUnknown, steps);
            if (readFailed) return Stepped(CaptureStatus.Failed, CaptureReason.ClipboardBusy, steps);
            if (string.IsNullOrWhiteSpace(text)) return Stepped(CaptureStatus.Unavailable, CaptureReason.CopyNoUpdate, steps);
            return SelectionCaptureResult.Create(CaptureStatus.Text, CaptureReason.None,
                text.Length > 6001 ? text.Substring(0, 6001) : text, steps);
        }

        // The saved clipboard goes back only while this capture is still the newest one, the
        // origin is still the captured process and the foreground has not moved. Every outcome
        // is recorded with its own metadata: a restore that did not happen is never reported
        // as a successful one.
        private static void RestoreClipboard(CaptureTarget target, ICopyHost host, ClipboardBackup previous,
            uint captured, CancellationToken cancellation, List<CaptureStep> steps, Stopwatch watch, ref long mark,
            IWindowOwnership ownership)
        {
            if (previous == null) {
                steps.Add(new CaptureStep { Stage = "clipboard_restore", Reason = CaptureReason.ClipboardOriginUnknown,
                    Checked = CaptureFields.Foreground | CaptureFields.Clipboard,
                    ClipboardChanged = false, ForegroundSame = host.ForegroundWindow == target.Window, ElapsedMs = Next(watch, ref mark) });
                return;
            }
            // A write of its own, so it is decided with the state the clipboard has now: text
            // another process put there in the meantime must survive instead of being replaced
            // by a backup that no longer describes this clipboard. The getters can block on
            // another application, so the whole decision - including the token - is taken after
            // them and immediately before the write.
            bool foregroundSame = host.ForegroundWindow == target.Window;
            bool windowVerified; uint ownerProcess;
            bool originTrusted = TrustedOrigin(host, target, ownership, out windowVerified, out ownerProcess);
            uint current = host.ClipboardSequence;
            bool cancelled = Stopped(cancellation);
            if (cancelled) {
                steps.Add(new CaptureStep { Stage = "clipboard_restore", Reason = CaptureReason.Cancelled,
                    Checked = CaptureFields.Foreground | CaptureFields.Clipboard,
                    ClipboardChanged = false, ForegroundSame = foregroundSame, ElapsedMs = Next(watch, ref mark) });
                return;
            }
            if (!WindowsSelectionProbe.CanRestoreCopy(captured, current, foregroundSame, originTrusted)) {
                steps.Add(new CaptureStep { Stage = "clipboard_restore",
                    Reason = !originTrusted ? CaptureReason.ClipboardOriginUnknown
                        : foregroundSame ? CaptureReason.CopyNoUpdate : CaptureReason.ForegroundChanged,
                    Checked = CaptureFields.Foreground | CaptureFields.Clipboard,
                    ClipboardChanged = false, ForegroundSame = foregroundSame, ElapsedMs = Next(watch, ref mark) });
                return;
            }
            bool restored = false; int error = 0;
            try { host.Restore(previous); restored = true; }
            catch (Exception failure) { error = failure.HResult; }
            steps.Add(new CaptureStep { Stage = "clipboard_restore",
                Reason = restored ? CaptureReason.None : CaptureReason.ClipboardBusy,
                Checked = CaptureFields.Foreground | CaptureFields.Clipboard,
                ClipboardChanged = restored, HResult = error, ForegroundSame = foregroundSame,
                ElapsedMs = Next(watch, ref mark) });
        }

        // The clipboard owner is either the captured process itself, or a window the captured
        // window verifiably contains: a WebView2 copy is owned by its browser process even
        // though the window belongs to the captured one. The window class is what proves that;
        // no process tree and no process id is trusted on its own. The owner process is handed
        // back as metadata, and is never read twice for it.
        private static bool TrustedOrigin(ICopyHost host, CaptureTarget target, IWindowOwnership ownership,
            out bool windowVerified, out uint ownerProcess)
        {
            windowVerified = false;
            try { ownerProcess = host.ClipboardOwnerProcess; } catch { ownerProcess = 0; }
            if (WindowsSelectionProbe.CanTrustOrigin(ownerProcess, target == null ? 0u : target.ProcessId)) return true;
            if (ownerProcess == 0 || target == null) return false;
            IntPtr window;
            try { window = host.ClipboardOwnerWindow; } catch { window = IntPtr.Zero; }
            windowVerified = WindowOwnership.IsVerifiedHostWindow(ownership, target.Window, window);
            return windowVerified;
        }

        // Stage durations are differences of one stopwatch, so the log's total is their sum
        // instead of a multiple of the real elapsed time.
        private static long Next(Stopwatch watch, ref long mark)
        {
            long now = watch.ElapsedMilliseconds;
            long elapsed = now - mark;
            mark = now;
            return elapsed;
        }

        private static bool WaitForModifiers(CancellationToken cancellation, int limitMs, ICopyHost host)
        {
            var watch = Stopwatch.StartNew();
            while (host.ModifiersHeld()) {
                if (Stopped(cancellation) || watch.ElapsedMilliseconds >= limitMs) return false;
                host.Pause(20);
            }
            return true;
        }
        internal static bool Stopped(CancellationToken token)
        {
            try { return token.IsCancellationRequested; } catch (ObjectDisposedException) { return true; }
        }
        private static SelectionCaptureResult Stopped(List<CaptureStep> steps)
        {
            return SelectionCaptureResult.Create(CaptureStatus.Cancelled, CaptureReason.Cancelled, "", steps);
        }
        private static SelectionCaptureResult Stepped(CaptureStatus status, CaptureReason reason, List<CaptureStep> steps)
        {
            return SelectionCaptureResult.Create(status, reason, "", steps);
        }
        private static SelectionCaptureResult Step(CaptureStatus status, CaptureReason reason, string stage, CaptureReason stepReason)
        {
            return SelectionCaptureResult.Create(status, reason, "", new List<CaptureStep> {
                new CaptureStep { Stage = stage, Reason = stepReason }
            });
        }
        internal static SelectionCaptureResult CopyFailure(Exception error, List<CaptureStep> steps)
        {
            var user = error as UserError;
            bool clipboard = user != null && (user.Code == "clipboard" || user.Code == "clipboard_busy");
            steps.Add(new CaptureStep {
                Stage = clipboard ? "clipboard_read" : "copy_send",
                Reason = clipboard ? CaptureReason.ClipboardBusy : CaptureReason.SystemFailure,
                HResult = error.HResult
            });
            return SelectionCaptureResult.Create(CaptureStatus.Failed,
                clipboard ? CaptureReason.ClipboardBusy : CaptureReason.SystemFailure, "", steps);
        }
    }
}
