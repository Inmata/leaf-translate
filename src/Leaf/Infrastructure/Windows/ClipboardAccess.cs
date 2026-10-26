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
    public static class ClipboardAccess
    {
        private static int clipboardWriting;
        private static int captureRunning;
        internal static bool TryBeginCapture() { return Interlocked.CompareExchange(ref captureRunning, 1, 0) == 0; }
        internal static void EndCapture() { Interlocked.Exchange(ref captureRunning, 0); }

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
            if (!Native.IsClipboardFormatAvailable(13) && !Native.IsClipboardFormatAvailable(1)) return Task.FromResult("");
            return StaCapture(() => Forms.Clipboard.GetText(Forms.TextDataFormat.UnicodeText), false);
        }
        private sealed class StaCaptureResult { public string Text; public Exception Error; }
        private static async Task<string> StaCapture(Func<string> action, bool selection)
        {
            if (!TryBeginCapture())
                throw new UserError("clipboard_busy", "取词尚未完成，可直接输入或稍后再试。");
            var result = new TaskCompletionSource<StaCaptureResult>();
            var worker = new Thread(() => {
                var captured = new StaCaptureResult();
                try { captured.Text = action(); } catch (Exception error) { captured.Error = error; }
                finally { EndCapture(); result.TrySetResult(captured); }
            }) { IsBackground = true, Name = "Leaf capture" };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
            if (await Task.WhenAny(result.Task, Task.Delay(selection ? 1600 : 600)) != result.Task)
                throw new UserError("clipboard_busy", "取词暂时不可用，可直接输入或稍后再试。");
            var value = await result.Task;
            if (value.Error != null) throw value.Error is UserError ? value.Error : new UserError("clipboard_busy", "剪贴板暂时不可用，可直接输入或稍后再试。");
            return value.Text;
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
            if (SelectionCopy.Stopped(cancellation)) return SelectionCaptureResult.NoText(CaptureStatus.Cancelled, CaptureReason.Cancelled);
            try {
                string text = await ClipboardTextAsync();
                return DirectClipboardResult(text);
            } catch (OperationCanceledException) {
                return SelectionCaptureResult.NoText(CaptureStatus.Cancelled, CaptureReason.Cancelled);
            } catch (UserError error) {
                var steps = new List<CaptureStep>();
                return SelectionCopy.CopyFailure(error, steps);
            } catch (Exception error) {
                var steps = new List<CaptureStep>();
                return SelectionCopy.CopyFailure(error, steps);
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
    }
}
