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
    // whether a backup is complete stays in ClipboardSnapshots.Materialize.
    public interface IClipboardFormatSource
    {
        string[] Formats { get; }
        // False when this format could not be read at all.
        bool TryRead(string format, out object value);
    }

    // The raw key injection the simulated copy performs, one key event per call, so the
    // injected sequence can be checked with a scripted injector and no input is sent.
    public interface IKeyInjector
    {
        // Injects one key event; false when Windows refused it, with the error it reported.
        bool Inject(ushort virtualKey, bool up, out int error);
        // Whether the desktop holds this key down right now. Read before a press so a key the
        // user was already holding is never pressed as this copy's own - and so never lifted.
        // It reports only the instant it is called: a key that comes down afterwards is not
        // something this can see.
        bool IsDown(ushort virtualKey);
    }

    // What one simulated copy did. Sent is false when a key event was refused, and Error is the
    // error of that event. CleanupFailed says a key this copy pressed itself could not be lifted
    // again afterwards, with the error Windows reported for the failing lift. That is the real
    // outcome of the cleanup attempt only: it never claims the desktop holds no key.
    public struct CopyInjection
    {
        public bool Sent;
        public int Error;
        public bool CleanupFailed;
        public int CleanupError;
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
        // Whether the keys the last SendCopy pressed itself could not all be lifted after it was
        // refused, and the error of the failing lift. The real cleanup outcome only; it does not
        // promise the desktop is clean. False when the copy was sent, which lifts nothing extra.
        bool KeyCleanupFailed { get; }
        int KeyCleanupError { get; }
        bool ModifiersHeld();
        void Pause(int milliseconds);
        bool SendCopy();
        string ReadClipboardText();
        ClipboardBackup Backup();
        void Restore(ClipboardBackup backup);
    }
}
