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
    // The real UI Automation tree behind IUiTree. Every call can fail on a foreign or busy
    // application, so each one is contained and simply yields no node.
    internal sealed class AutomationUiTree : IUiTree
    {
        public IUiNode Focused
        {
            get {
                AutomationElement element = null;
                try { element = AutomationElement.FocusedElement; } catch { }
                return element == null ? null : new AutomationUiNode(element);
            }
        }
        // The real keyboard focus of the foreground thread, read at call time.
        public IntPtr NativeFocusWindow { get { return Native.ForegroundFocusWindow(); } }
        public IUiNode Root(IntPtr window)
        {
            AutomationElement element = null;
            try { element = AutomationElement.FromHandle(window); } catch { }
            return element == null ? null : new AutomationUiNode(element);
        }
        public bool IsForeground(CaptureTarget target) { return WindowsSelectionProbe.ForegroundIs(target); }
    }

    internal sealed class AutomationUiNode : IUiNode
    {
        private readonly AutomationElement element;
        public AutomationUiNode(AutomationElement element) { this.element = element; }
        public int ProcessId { get { try { return element.Current.ProcessId; } catch { return 0; } } }
        public int ControlTypeId { get { try { return element.Current.ControlType.Id; } catch { return 0; } } }
        public long NativeWindowHandle { get { try { return element.Current.NativeWindowHandle; } catch { return 0; } } }
        public IUiNode Parent { get { return Wrap(TreeWalker.ControlViewWalker.GetParent(element)); } }
        public IUiNode FirstChild { get { return Wrap(TreeWalker.ControlViewWalker.GetFirstChild(element)); } }
        public IUiNode NextSibling { get { return Wrap(TreeWalker.ControlViewWalker.GetNextSibling(element)); } }
        // Control Name and Value are never read, and no whole document is fetched. A pattern
        // that cannot answer is reported as unknown instead of as an empty selection.
        public UiSelectionAnswer ReadSelection()
        {
            object pattern;
            try {
                pattern = WindowsSelectionProbe.ChooseTextPattern(
                    () => { object found; return element.TryGetCurrentPattern(TextPattern.Pattern, out found) ? found : null; },
                    // TextPattern2 is the same selection surface with extra caret calls, and this
                    // client library has no managed wrapper for it: it is asked for by its own
                    // pattern id, and only an answer the selection read can actually use counts.
                    // A client that does not know the id leaves the capability unknown.
                    () => {
                        var extension = ExtensionTextPattern;
                        if (extension == null) return null;
                        try { object found; return element.TryGetCurrentPattern(extension, out found) ? found : null; }
                        catch { return null; }
                    });
            } catch (Exception error) { return UiSelectionAnswer.Unknown(error.HResult); }
            if (pattern == null) return UiSelectionAnswer.NoPattern;
            var text = pattern as TextPattern;
            if (text == null) return UiSelectionAnswer.NoPattern;
            return WindowsSelectionProbe.ReadSelectionAnswer(
                () => text.GetSelection(),
                range => ((TextPatternRange)range).GetText(6001));
        }
        // TextPattern2 is not part of this framework's client library, so its pattern id is
        // resolved once and stays unknown when it cannot be.
        private static readonly AutomationPattern ExtensionTextPattern = LookupExtensionTextPattern();
        private static AutomationPattern LookupExtensionTextPattern()
        {
            try { return AutomationPattern.LookupById(10024); } catch { return null; }
        }
        private static IUiNode Wrap(AutomationElement element)
        {
            return element == null ? null : new AutomationUiNode(element);
        }
    }
}
