using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Leaf
{
    // The native window facts that decide whether a window belongs to the captured one. Kept
    // behind an interface so the ownership rule can be checked without a desktop.
    public interface IWindowOwnership
    {
        // True when child hangs natively under parent.
        bool IsChildOf(IntPtr parent, IntPtr child);
        // The window class name, "" when Windows did not report one.
        string ClassName(IntPtr window);
    }

    // The ownership rule of the capture and copy paths: a window that hangs natively under the
    // captured window can belong to it even when its process is a different one. That is how a
    // WebView2 host window, its browser process and the page inside it stand to the window the
    // user selected text in, and it is the only exception to the same-process boundary.
    public static class WindowOwnership
    {
        // The window classes a WebView2 / WebView host uses for the window it embeds its
        // content in. Only a window carrying one of them may answer across processes.
        public static readonly string[] VerifiedHostClasses = {
            "WRY_WEBVIEW", "Chrome_WidgetWin_0", "Chrome_WidgetWin_1",
            "Chrome_RenderWidgetHostHWND", "Intermediate D3D Window"
        };
        // The one instance every production path shares.
        public static readonly IWindowOwnership Native = new NativeWindowOwnership();

        // The captured window itself or one of its native descendants. This is containment as
        // Windows reports it: it never reaches a window of another top-level window, of another
        // window of the same application, or the desktop.
        public static bool Contains(IWindowOwnership ownership, IntPtr captured, IntPtr candidate)
        {
            if (ownership == null || captured == IntPtr.Zero || candidate == IntPtr.Zero) return false;
            if (captured == candidate) return true;
            try { return ownership.IsChildOf(captured, candidate); } catch { return false; }
        }

        // A window that is natively inside the captured window and carries a verified WebView
        // host class. This is the identity that lets another process answer for the captured
        // window's selection or clipboard; a process id or a process tree never does.
        public static bool IsVerifiedHostWindow(IWindowOwnership ownership, IntPtr captured, IntPtr candidate)
        {
            if (captured == IntPtr.Zero || candidate == IntPtr.Zero || candidate == captured) return false;
            if (!Contains(ownership, captured, candidate)) return false;
            string name;
            try { name = ownership.ClassName(candidate); } catch { return false; }
            if (string.IsNullOrEmpty(name)) return false;
            foreach (string known in VerifiedHostClasses)
                if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    // The real native facts: IsChild walks the window hierarchy, GetClassName reads the class.
    public sealed class NativeWindowOwnership : IWindowOwnership
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);

        public bool IsChildOf(IntPtr parent, IntPtr child)
        {
            if (parent == IntPtr.Zero || child == IntPtr.Zero) return false;
            return IsChild(parent, child);
        }
        public string ClassName(IntPtr window)
        {
            if (window == IntPtr.Zero) return "";
            var name = new StringBuilder(256);
            int length = GetClassName(window, name, name.Capacity);
            return length <= 0 ? "" : name.ToString();
        }
    }
}
