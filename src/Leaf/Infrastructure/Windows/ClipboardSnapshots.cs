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
    public static class ClipboardSnapshots
    {
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

    }
}
