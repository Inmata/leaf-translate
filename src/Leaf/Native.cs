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
    public sealed class HotkeySpec
    {
        public uint Modifiers; public uint VirtualKey;
        public static bool IsModifier(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin;
        }
        public static string Record(Key key, ModifierKeys modifiers)
        {
            if (IsModifier(key) || key == Key.System || key == Key.ImeProcessed || key == Key.DeadCharProcessed)
                throw new UserError("shortcut", "继续按下字母、数字或功能键。");
            string combination = ((modifiers & ModifierKeys.Control) != 0 ? "Ctrl+" : "") +
                ((modifiers & ModifierKeys.Alt) != 0 ? "Alt+" : "") +
                ((modifiers & ModifierKeys.Shift) != 0 ? "Shift+" : "") +
                ((modifiers & ModifierKeys.Windows) != 0 ? "Win+" : "");
            string name = key >= Key.D0 && key <= Key.D9 ? ((int)key - (int)Key.D0).ToString() : key.ToString();
            combination += name; Parse(combination); return combination;
        }
        public static HotkeySpec Parse(string text)
        {
            var parts = (text ?? "").Split('+').Select(x => x.Trim()).ToArray();
            if (parts.Length < 2) throw new UserError("shortcut", "快捷键需包含 Ctrl、Alt 或 Win 与一个按键，例如 Ctrl+Alt+T。");
            uint modifiers = 0;
            for (int i = 0; i < parts.Length - 1; i++) {
                switch (parts[i].ToLowerInvariant()) {
                    case "alt": modifiers |= 1; break;
                    case "ctrl": case "control": modifiers |= 2; break;
                    case "shift": modifiers |= 4; break;
                    case "win": case "windows": modifiers |= 8; break;
                    default: throw new UserError("shortcut", "无法识别快捷键，请使用例如 Ctrl+Alt+T 的形式。");
                }
            }
            string keyName = parts.Last();
            if (keyName.Equals("spacebar", StringComparison.OrdinalIgnoreCase) || keyName == "空格" || keyName == "空格键") keyName = "Space";
            if (keyName.Length == 1 && char.IsDigit(keyName[0])) keyName = "D" + keyName;
            Key key;
            if ((modifiers & 11) == 0 || !Enum.TryParse<Key>(keyName, true, out key) || !Enum.IsDefined(typeof(Key), key) || key == Key.None)
                throw new UserError("shortcut", "快捷键需包含 Ctrl、Alt 或 Win 与一个有效按键。");
            int virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey == 0 || virtualKey == 16 || virtualKey == 17 || virtualKey == 18 || virtualKey == 91 || virtualKey == 92)
                throw new UserError("shortcut", "快捷键最后一项需为字母、数字或功能键。");
            return new HotkeySpec { Modifiers = modifiers | 0x4000, VirtualKey = (uint)virtualKey };
        }
    }
    // A restorable copy of whatever the clipboard held before this capture. A null backup
    // means "unknown": the worker then leaves the clipboard exactly as its copy left it,
    // because it cannot tell an empty clipboard from one it failed to read.
    public sealed class ClipboardBackup
    {
        private readonly object data;
        private ClipboardBackup(object data) { this.data = data; }
        // An empty clipboard that was verified as empty. Nothing on the desktop path produces
        // one: a data object the clipboard did not hand over is unknown instead.
        public static ClipboardBackup Empty() { return new ClipboardBackup(null); }
        public static ClipboardBackup Of(object data) { return new ClipboardBackup(data); }
        public bool IsEmpty { get { return data == null; } }
        public object Data { get { return data; } }
    }

    // The clipboard formats of one data object, as the backup needs them. A data object cannot
    // be built off-desktop, so this is the one step a check scripts; the rule that decides
    // whether a backup is complete stays in Native.Materialize.
    public interface IClipboardFormatSource
    {
        string[] Formats { get; }
        // False when this format could not be read at all.
        bool TryRead(string format, out object value);
    }

    // The desktop operations the copy worker is allowed to perform, so its sequence,
    // ownership, cancellation and restore rules can be checked with a scripted host.
    public interface ICopyHost
    {
        IntPtr ForegroundWindow { get; }
        // The window the foreground thread's keyboard focus is on, zero when Windows did not
        // report one. This is the window a simulated copy really reaches.
        IntPtr FocusedWindow { get; }
        uint ClipboardSequence { get; }
        uint ClipboardOwnerProcess { get; }
        // The clipboard owner's window, zero when the clipboard has no owner. A WebView2 copy
        // is owned by its browser process, so this window is what proves it belongs here.
        IntPtr ClipboardOwnerWindow { get; }
        int SendError { get; }
        bool ModifiersHeld();
        void Pause(int milliseconds);
        bool SendCopy();
        string ReadClipboardText();
        ClipboardBackup Backup();
        void Restore(ClipboardBackup backup);
    }

    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public IntPtr Extra; }
        // The subset of GUITHREADINFO the copy worker needs: the window this thread's keyboard
        // focus is on. Read only, and never to look at what is written in it.
        [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo {
            public int Size; public uint Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public Rect CaretRect;
        }
        public delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out uint value, uint size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string name, string title);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
        private static int clipboardWriting;
        private static int captureRunning;

        public static bool IsDesktop(IntPtr window)
        {
            var name = new StringBuilder(128); GetClassName(window, name, name.Capacity);
            string kind = name.ToString();
            return window == IntPtr.Zero || kind == "Progman" || kind == "WorkerW" || kind == "Shell_TrayWnd" || kind == "Shell_SecondaryTrayWnd";
        }
        public static void Reveal(Window window)
        {
            IntPtr target = new WindowInteropHelper(window).EnsureHandle();
            // Re-show even when WPF IsVisible is still true after Explorer's Show Desktop.
            uint cloaked;
            if (DwmGetWindowAttribute(target, 14, out cloaked, 4) == 0 && cloaked != 0) ShowWindow(target, 0);
            ShowWindow(target, 4); // SW_SHOWNOACTIVATE restores without taking keyboard focus.
            SetWindowPos(target, new IntPtr(-1), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002 | 0x0040);
        }
        public static async Task CopyTextAsync(string text)
        {
            if (Interlocked.CompareExchange(ref clipboardWriting, 1, 0) != 0)
                throw new UserError("clipboard", "剪贴板正在忙，请稍后再复制。");
            var result = new TaskCompletionSource<bool>();
            var worker = new Thread(() => {
                try {
                    var data = new Forms.DataObject(); data.SetText(text, Forms.TextDataFormat.UnicodeText);
                    Forms.Clipboard.SetDataObject(data, true, 0, 0); result.TrySetResult(true);
                } catch { result.TrySetResult(false); }
                finally { Interlocked.Exchange(ref clipboardWriting, 0); }
            }) { IsBackground = true, Name = "Leaf clipboard" };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
            if (await Task.WhenAny(result.Task, Task.Delay(800)) != result.Task || !await result.Task)
                throw new UserError("clipboard", "暂时无法复制，剪贴板可能正被其他程序使用。请稍后再试。");
        }

        public static string ClipboardText()
        {
            try {
                if (!Clipboard.ContainsText()) throw new UserError("clipboard", "剪贴板里没有文字。请先复制文字，再按快捷键。");
                return TextTools.ValidateInput(Clipboard.GetText());
            } catch (UserError) { throw; }
            catch { throw new UserError("clipboard", "剪贴板正被其他程序使用。请稍候再试。"); }
        }
        public static Task<string> ClipboardTextAsync()
        {
            if (!IsClipboardFormatAvailable(13) && !IsClipboardFormatAvailable(1)) return Task.FromResult("");
            return StaCapture(() => Forms.Clipboard.GetText(Forms.TextDataFormat.UnicodeText), false);
        }
        private sealed class StaCaptureResult { public string Text; public Exception Error; }
        private static async Task<string> StaCapture(Func<string> action, bool selection)
        {
            if (Interlocked.CompareExchange(ref captureRunning, 1, 0) != 0)
                throw new UserError("clipboard_busy", "取词尚未完成，可直接输入或稍后再试。");
            var result = new TaskCompletionSource<StaCaptureResult>();
            var worker = new Thread(() => {
                var captured = new StaCaptureResult();
                try { captured.Text = action(); } catch (Exception error) { captured.Error = error; }
                finally { Interlocked.Exchange(ref captureRunning, 0); result.TrySetResult(captured); }
            }) { IsBackground = true, Name = "Leaf capture" };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
            if (await Task.WhenAny(result.Task, Task.Delay(selection ? 1600 : 600)) != result.Task)
                throw new UserError("clipboard_busy", "取词暂时不可用，可直接输入或稍后再试。");
            var value = await result.Task;
            if (value.Error != null) throw value.Error is UserError ? value.Error : new UserError("clipboard_busy", "剪贴板暂时不可用，可直接输入或稍后再试。");
            return value.Text;
        }

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
            if (Interlocked.CompareExchange(ref captureRunning, 1, 0) != 0)
                return Task.FromResult(Step(CaptureStatus.Unavailable, CaptureReason.CaptureBusy, "copy_wait", CaptureReason.CaptureBusy));
            var completion = new TaskCompletionSource<SelectionCaptureResult>();
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var worker = new Thread(() => {
                var steps = new List<CaptureStep>();
                SelectionCaptureResult outcome;
                try { outcome = CopyOnSta(target, deadline, steps, host, ownership); }
                catch (Exception error) { outcome = CopyFailure(error, steps); }
                finally { Interlocked.Exchange(ref captureRunning, 0); }
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
                Checked = CaptureFields.Foreground,
                NativeError = sent ? 0 : host.SendError, ForegroundSame = host.ForegroundWindow == target.Window,
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
        private static uint OwnerProcess()
        {
            IntPtr owner = GetClipboardOwner();
            if (owner == IntPtr.Zero) return 0;
            uint process; GetWindowThreadProcessId(owner, out process);
            return process;
        }
        // The keyboard focus of the foreground thread: the window a simulated copy reaches.
        // Zero means Windows did not report one, which is unknown rather than "somewhere else".
        // Also the independent native witness the UIA probe reads for cross-process focus.
        public static IntPtr ForegroundFocusWindow()
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return IntPtr.Zero;
            uint process; uint thread = GetWindowThreadProcessId(foreground, out process);
            if (thread == 0) return IntPtr.Zero;
            var info = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
            if (!GetGUIThreadInfo(thread, ref info)) return IntPtr.Zero;
            return info.Focus;
        }
        private static bool Stopped(CancellationToken token)
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
        private static SelectionCaptureResult CopyFailure(Exception error, List<CaptureStep> steps)
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
        private static bool ModifierKeysDown() { return new[] { 16, 17, 18, 91, 92 }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0); }

        // The old clipboard is materialised while it can still be read: a delayed-rendering
        // proxy belongs to the source application and could not be rendered after this copy
        // replaced it. Anything short of every format read successfully stays unknown, because
        // a partial copy must never be put back as if it were what the user had.
        public static ClipboardBackup Materialize(IClipboardFormatSource source)
        {
            if (source == null) return null;
            string[] formats = source.Formats;
            if (formats == null || formats.Length == 0) return null;
            var copy = new Forms.DataObject();
            foreach (string format in formats) {
                object value;
                try { if (!source.TryRead(format, out value)) return null; }
                catch { return null; }
                // A format the clipboard lists but cannot hand over leaves a partial copy,
                // which is unknown rather than something safe to put back later.
                if (value == null) return null;
                copy.SetData(format, value);
            }
            return copy.GetFormats(false).Length == 0 ? null : ClipboardBackup.Of(copy);
        }

        // The desktop read behind the host's Backup(): the data object the clipboard handed
        // over. Split out so a check can drive the real adapter - including the object it was
        // handed - with no real clipboard. A data object the clipboard did not hand over is
        // unknown, exactly like a partial read: .NET answers null both for a clipboard that
        // held nothing and for one it could not read, so Empty here would let a later restore
        // Clear a clipboard that may well have held something.
        public static ClipboardBackup BackupFrom(Forms.IDataObject data)
        {
            if (data == null) return null;
            return Materialize(new ClipboardFormatSource(data));
        }

        // The real clipboard behind the materialisation: one data object, read once per format.
        private sealed class ClipboardFormatSource : IClipboardFormatSource
        {
            private readonly Forms.IDataObject data;
            public ClipboardFormatSource(Forms.IDataObject data) { this.data = data; }
            public string[] Formats { get { return data.GetFormats(false); } }
            public bool TryRead(string format, out object value)
            {
                try { value = data.GetData(format, false); return true; }
                catch { value = null; return false; }
            }
        }

        // Everything the copy worker does to the desktop. The worker's decisions are the
        // part worth checking, so they run against this seam instead of the real clipboard.
        private sealed class DesktopCopyHost : ICopyHost
        {
            private int sendError;
            public IntPtr ForegroundWindow { get { return GetForegroundWindow(); } }
            public IntPtr FocusedWindow { get { return ForegroundFocusWindow(); } }
            public uint ClipboardSequence { get { return GetClipboardSequenceNumber(); } }
            public uint ClipboardOwnerProcess { get { return OwnerProcess(); } }
            public IntPtr ClipboardOwnerWindow { get { return GetClipboardOwner(); } }
            public int SendError { get { return sendError; } }
            public bool ModifiersHeld() { return ModifierKeysDown(); }
            public void Pause(int milliseconds) { Thread.Sleep(milliseconds); }
            public bool SendCopy()
            {
                var inputs = new[] { KeyInput(17, false), KeyInput(67, false), KeyInput(67, true), KeyInput(17, true) };
                uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input)));
                sendError = sent == inputs.Length ? 0 : Marshal.GetLastWin32Error();
                return sent == inputs.Length;
            }
            public string ReadClipboardText()
            {
                if (!IsClipboardFormatAvailable(13)) return "";
                return Forms.Clipboard.GetText(Forms.TextDataFormat.UnicodeText);
            }
            // A delayed-rendering proxy belongs to the source application, so the formats are
            // materialised now: after this copy the proxy could no longer be rendered. A
            // clipboard that cannot be read completely stays "unknown" instead of empty.
            public ClipboardBackup Backup()
            {
                return BackupFrom(Forms.Clipboard.GetDataObject());
            }
            public void Restore(ClipboardBackup backup)
            {
                if (backup == null) throw new ArgumentNullException("backup");
                if (backup.IsEmpty) { Forms.Clipboard.Clear(); return; }
                Forms.Clipboard.SetDataObject(backup.Data, true, 0, 0);
            }
        }

        // The direct clipboard-mode read maps what the clipboard held onto a typed result.
        // No text is what the clipboard held - a fact about the clipboard, never about a
        // selection - so it is ClipboardEmpty, not the copy path's CopyNoUpdate, and the
        // stage claims no clipboard change it never compared sequences for.
        public static SelectionCaptureResult DirectClipboardResult(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return SelectionCaptureResult.Create(CaptureStatus.Unavailable, CaptureReason.ClipboardEmpty, "",
                    new List<CaptureStep> { new CaptureStep { Stage = "clipboard_read", Reason = CaptureReason.ClipboardEmpty } });
            return SelectionCaptureResult.Create(CaptureStatus.Text, CaptureReason.None, text,
                new List<CaptureStep> { new CaptureStep { Stage = "clipboard_read", Reason = CaptureReason.None } });
        }

        // Clipboard mode reads the clipboard only for the invocation that asked for it.
        public static async Task<SelectionCaptureResult> ReadClipboardAsync(CaptureTarget target, CancellationToken cancellation)
        {
            if (Stopped(cancellation)) return SelectionCaptureResult.NoText(CaptureStatus.Cancelled, CaptureReason.Cancelled);
            try {
                string text = await ClipboardTextAsync();
                return DirectClipboardResult(text);
            } catch (OperationCanceledException) {
                return SelectionCaptureResult.NoText(CaptureStatus.Cancelled, CaptureReason.Cancelled);
            } catch (UserError error) {
                var steps = new List<CaptureStep>();
                return CopyFailure(error, steps);
            } catch (Exception error) {
                var steps = new List<CaptureStep>();
                return CopyFailure(error, steps);
            }
        }

        // Compatibility wrapper: the shortcut path uses SelectionAcquirer, and this entry
        // point keeps the same acquisition order - UIA first, one safe copy only when the
        // automation answer is inconclusive - plus the same input limit, while still
        // returning a string to callers that predate the typed result.
        public static Task<string> SelectedTextAsync()
        {
            return SelectedTextAsync(new SelectionAcquirer(new WindowsSelectionProbe()));
        }
        public static async Task<string> SelectedTextAsync(SelectionAcquirer selection)
        {
            if (selection == null) throw new ArgumentNullException("selection");
            var target = selection.Snapshot();
            if (target.IsDesktop) return "";
            var result = await selection.CaptureAsync(target, false, WrapperShortcut, CancellationToken.None);
            if (result.Status == CaptureStatus.Text) return TextTools.ValidateInput(result.Text);
            if (result.Status == CaptureStatus.Empty) return "";
            throw new UserError("selection", "没有取得选中文字。请松开快捷键重试，或切到剪贴板模式。");
        }
        // The wrapper has no configured shortcut of its own; the acquirer only uses it to
        // reject free text, and this path never writes diagnostics.
        private const string WrapperShortcut = "Ctrl+Alt+T";
        private static Input KeyInput(ushort key, bool up)
        {
            return new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } } };
        }
        public static Forms.Screen SavedScreen(Settings settings)
        {
            var saved = settings.Placement;
            return Forms.Screen.AllScreens.FirstOrDefault(s => saved != null && s.DeviceName == saved.Screen) ?? Forms.Screen.PrimaryScreen;
        }
        public static double Scale(Forms.Screen screen)
        {
            try {
                uint x, y;
                if (GetDpiForMonitor(MonitorFromPoint(new Point { X = screen.Bounds.Left + 1, Y = screen.Bounds.Top + 1 }, 2), 0, out x, out y) == 0)
                    return x / 96.0;
            } catch { }
            return 1;
        }
        public static void Position(Window window, Settings settings)
        {
            var screen = SavedScreen(settings); var area = screen.WorkingArea;
            double scale = Scale(screen);
            var placement = PopupLayout.Fit(settings.Placement, area.Left, area.Top, area.Width, area.Height, scale, screen.DeviceName);
            window.MinWidth = Math.Min(360, Math.Max(1, (area.Width - 24) / scale));
            window.MinHeight = Math.Min(380, Math.Max(1, (area.Height - 24) / scale));
            window.MaxWidth = Math.Max(window.MinWidth, (area.Width - 24) / scale);
            window.MaxHeight = Math.Max(window.MinHeight, (area.Height - 24) / scale);
            window.Width = placement.Width; window.Height = placement.Height;
            SetWindowPos(new WindowInteropHelper(window).EnsureHandle(), new IntPtr(-1), (int)placement.X, (int)placement.Y,
                (int)Math.Round(placement.Width * scale), (int)Math.Round(placement.Height * scale), 0x0010);
        }
        public static WindowPlacement CapturePlacement(Window window)
        {
            Rect rect;
            if (!GetWindowRect(new WindowInteropHelper(window).Handle, out rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top) return null;
            var screen = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            return new WindowPlacement { X = rect.Left, Y = rect.Top, Width = window.ActualWidth,
                Height = window.ActualHeight, Screen = screen.DeviceName };
        }
        public static void AutoStart(bool enabled)
        {
            try {
                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
                    if (enabled) key.SetValue("LeafTranslate", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" --background");
                    else key.DeleteValue("LeafTranslate", false);
                }
            } catch { throw new UserError("startup", "无法更新开机启动设置，请检查系统权限。"); }
        }
    }
    public static class PopupLayout
    {
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        public static WindowPlacement Fit(WindowPlacement saved, double left, double top, double areaWidth, double areaHeight, double scale, string screen)
        {
            if (!Finite(scale) || scale <= 0) scale = 1;
            double width = saved != null && Finite(saved.Width) && saved.Width > 0 ? saved.Width : 456;
            double height = saved != null && Finite(saved.Height) && saved.Height > 0 ? saved.Height : 620;
            width = Math.Min(Math.Max(360, width), Math.Max(1, (areaWidth - 24) / scale));
            height = Math.Min(Math.Max(380, height), Math.Max(1, (areaHeight - 24) / scale));
            bool restore = saved != null && saved.Screen == screen && Finite(saved.X) && Finite(saved.Y);
            double x = restore ? saved.X : left + areaWidth - width * scale - 32;
            double y = restore ? saved.Y : top + (areaHeight - height * scale) / 2;
            x = Math.Max(left + 12, Math.Min(x, left + areaWidth - width * scale - 12));
            y = Math.Max(top + 12, Math.Min(y, top + areaHeight - height * scale - 12));
            return new WindowPlacement { X = x, Y = y, Width = width, Height = height, Screen = screen };
        }
    }
    public sealed class GlobalShortcut : IDisposable
    {
        private readonly IntPtr window;
        private readonly int id;
        private IntPtr hook;
        private Native.HookProc callback;
        private bool registered, spaceDown;
        [StructLayout(LayoutKind.Sequential)] private struct KeyEvent
        {
            public uint Key, Scan, Flags, Time;
            public IntPtr Extra;
        }
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        public GlobalShortcut(IntPtr target, int identifier, HotkeySpec spec)
        {
            window = target; id = identifier;
            // Alt+Space is also a Windows system-menu shortcut. Capture this narrow
            // combination so it works without opening the foreground window's menu.
            if ((spec.Modifiers & 15) == 1 && spec.VirtualKey == 32) {
                callback = KeyboardHook;
                hook = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero) throw new UserError("shortcut", "无法启用 Alt+Space，请检查系统权限或改用其他快捷键。");
            } else {
                registered = Native.RegisterHotKey(window, id, spec.Modifiers, spec.VirtualKey);
                if (!registered) throw new UserError("shortcut", "快捷键已被其他软件占用，请换一个组合。");
            }
        }
        private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0) {
                var key = (KeyEvent)Marshal.PtrToStructure(data, typeof(KeyEvent));
                int kind = message.ToInt32();
                if (key.Key == 32) {
                    bool down = kind == 0x0100 || kind == 0x0104;
                    bool up = kind == 0x0101 || kind == 0x0105;
                    bool alt = (key.Flags & 0x20) != 0;
                    bool extraModifier = new[] { 16, 17, 91, 92 }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0);
                    if (down && (spaceDown || (alt && !extraModifier))) {
                        if (!spaceDown) { spaceDown = true; Native.PostMessage(window, 0x0312, new IntPtr(id), IntPtr.Zero); }
                        return new IntPtr(1);
                    }
                    if (up && spaceDown) { spaceDown = false; return new IntPtr(1); }
                }
            }
            return Native.CallNextHookEx(hook, code, message, data);
        }
        public void Dispose()
        {
            if (registered) { Native.UnregisterHotKey(window, id); registered = false; }
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            spaceDown = false;
        }
    }
    public sealed class OutsideClick : IDisposable
    {
        private IntPtr hook;
        private readonly Native.HookProc callback;
        public OutsideClick(Action outside)
        {
            uint own = (uint)Process.GetCurrentProcess().Id;
            callback = (code, message, data) => {
                if (code >= 0 && (message.ToInt32() == 0x0201 || message.ToInt32() == 0x0204 || message.ToInt32() == 0x0207 || message.ToInt32() == 0x020B)) {
                    var point = (Native.Point)Marshal.PtrToStructure(data, typeof(Native.Point));
                    uint process; Native.GetWindowThreadProcessId(Native.WindowFromPoint(point), out process);
                    if (process != own) outside();
                }
                return Native.CallNextHookEx(hook, code, message, data);
            };
        }
        public void Enable()
        {
            if (hook == IntPtr.Zero) hook = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0);
        }
        public void Dispose() { if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } }
    }
}
