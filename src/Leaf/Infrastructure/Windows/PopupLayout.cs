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
}
