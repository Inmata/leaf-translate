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
