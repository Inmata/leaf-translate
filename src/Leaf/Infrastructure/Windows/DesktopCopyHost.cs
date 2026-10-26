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
    // Everything the copy worker does to the desktop. The worker's decisions are the
    // part worth checking, so they run against this seam instead of the real clipboard.
    internal sealed class DesktopCopyHost : ICopyHost, IKeyInjector
    {
        private int sendError;
        private bool cleanupFailed;
        private int cleanupError;
        public IntPtr ForegroundWindow { get { return Native.GetForegroundWindow(); } }
        public IntPtr FocusedWindow { get { return Native.ForegroundFocusWindow(); } }
        public uint ClipboardSequence { get { return Native.GetClipboardSequenceNumber(); } }
        public uint ClipboardOwnerProcess { get { return OwnerProcess(); } }
        public IntPtr ClipboardOwnerWindow { get { return Native.GetClipboardOwner(); } }
        public int SendError { get { return sendError; } }
        public bool KeyCleanupFailed { get { return cleanupFailed; } }
        public int KeyCleanupError { get { return cleanupError; } }
        public bool ModifiersHeld() { return ModifierKeysDown(); }
        public bool IsDown(ushort virtualKey) { return KeyDown(virtualKey); }
        public void Pause(int milliseconds) { Thread.Sleep(milliseconds); }
        // One key event per call so a refused step can be undone precisely: see
        // CopyInput.InjectCopy. Only this copy's own keys are ever lifted again.
        public bool SendCopy()
        {
            var copy = CopyInput.InjectCopy(this);
            sendError = copy.Sent ? 0 : copy.Error;
            cleanupFailed = copy.CleanupFailed;
            cleanupError = copy.CleanupError;
            return copy.Sent;
        }
        public bool Inject(ushort virtualKey, bool up, out int error)
        {
            var inputs = new[] { KeyInput(virtualKey, up) };
            uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.Input)));
            error = sent == inputs.Length ? 0 : Marshal.GetLastWin32Error();
            return sent == inputs.Length;
        }
        public string ReadClipboardText()
        {
            if (!Native.IsClipboardFormatAvailable(13)) return "";
            return Forms.Clipboard.GetText(Forms.TextDataFormat.UnicodeText);
        }
        // A delayed-rendering proxy belongs to the source application, so the formats are
        // materialised now: after this copy the proxy could no longer be rendered. A
        // clipboard that cannot be read completely stays "unknown" instead of empty.
        public ClipboardBackup Backup()
        {
            return ClipboardSnapshots.BackupFrom(Forms.Clipboard.GetDataObject());
        }
        public void Restore(ClipboardBackup backup)
        {
            if (backup == null) throw new ArgumentNullException("backup");
            if (backup.IsEmpty) { Forms.Clipboard.Clear(); return; }
            Forms.Clipboard.SetDataObject(backup.Data, true, 0, 0);
        }
        private static uint OwnerProcess()
        {
            IntPtr owner = Native.GetClipboardOwner();
            if (owner == IntPtr.Zero) return 0;
            uint process; Native.GetWindowThreadProcessId(owner, out process);
            return process;
        }
        private static bool KeyDown(ushort key) { return (Native.GetAsyncKeyState(key) & 0x8000) != 0; }
        private static bool ModifierKeysDown() { return new ushort[] { 16, 17, 18, 91, 92 }.Any(KeyDown); }
        private static Native.Input KeyInput(ushort key, bool up)
        {
            return new Native.Input { Type = 1, Data = new Native.InputUnion { Keyboard = new Native.KeyboardInput { Key = key, Flags = up ? 2u : 0u } } };
        }

    }
}
