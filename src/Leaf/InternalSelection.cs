using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Leaf
{
    // What one in-window selection read produced. Unavailable is the answer for a page or a
    // control this read may not speak for, and it is never a hidden "nothing is selected".
    public enum InternalSelectionStatus { Text, Empty, Unavailable }

    // Why a read answered the way it did. Typed only: no control content and no free text.
    public enum InternalSelectionReason
    {
        None, NoPage, WindowInactive, NoFocus, FocusOutsidePage, ContentFocus, HiddenPage,
        SensitiveControl, NotSelectable, EmptySelection
    }

    public sealed class InternalSelectionResult
    {
        private InternalSelectionResult(InternalSelectionStatus status, InternalSelectionReason reason, string text, string source)
        {
            Status = status; Reason = reason; Text = text ?? ""; Source = source ?? "";
        }
        public InternalSelectionStatus Status { get; private set; }
        public InternalSelectionReason Reason { get; private set; }
        // Never null: only a Text answer carries anything.
        public string Text { get; private set; }
        // The whitelisted source name the answer came from, "" when it came from nowhere.
        public string Source { get; private set; }
        public int TextLength { get { return Text.Length; } }
        public static InternalSelectionResult Of(string text, string source)
        {
            return new InternalSelectionResult(InternalSelectionStatus.Text, InternalSelectionReason.None, text, source);
        }
        public static InternalSelectionResult Empty(string source)
        {
            return new InternalSelectionResult(InternalSelectionStatus.Empty, InternalSelectionReason.EmptySelection, "", source);
        }
        public static InternalSelectionResult Unavailable(InternalSelectionReason reason)
        {
            return new InternalSelectionResult(InternalSelectionStatus.Unavailable, reason, "", "");
        }
        // The shortcut's answer when there is no page to read from at all.
        public static readonly InternalSelectionResult NoPage = Unavailable(InternalSelectionReason.NoPage);
    }

    // The read the shortcut uses while the popup itself is in front.
    public interface IInternalSelectionReader
    {
        InternalSelectionResult Read();
    }

    // Reads the selection of the control that really has the keyboard focus on this window's
    // translate page: the original editor, the selectable original or the translation, plus a
    // reading control that opts in with the shared tag. It never falls back to another
    // control's older selection, never touches UI Automation, the clipboard or a simulated
    // copy, and never reads a password, the follow-up input or a settings field.
    public sealed class InternalSelectionReader : IInternalSelectionReader
    {
        public const string InputName = "SourceInput";
        public const string SourceName = "SourceText";
        public const string TranslationName = "TranslationText";
        public const string SelectableTag = "LeafSelectableText";
        // Settings and conversation fields are never a translation source, whatever the page
        // around them looks like.
        private static readonly string[] SensitiveNames = {
            "QuestionInput", "ApiKeyInput", "EndpointInput", "ModelInput", "OutputLimitInput",
            "TargetInput", "SceneDetailInput", "CustomNameInput", "CustomInstructionInput",
            "PresetNameInput", "ShortcutInput", "HistoryLimitInput"
        };
        private const int MaxParentDepth = 128;
        private readonly FrameworkElement page;

        public InternalSelectionReader(FrameworkElement page) { this.page = page; }

        // The production entry point: the real keyboard focus of this window's page. Must run
        // on the UI thread, and only while this window really is the active one.
        public InternalSelectionResult Read()
        {
            var window = page as Window;
            if (window == null || !window.IsVisible) return InternalSelectionResult.NoPage;
            if (!window.IsActive) return InternalSelectionResult.Unavailable(InternalSelectionReason.WindowInactive);
            return ReadFrom(Keyboard.FocusedElement);
        }

        // The same read against a given focus, so its rules can be checked with real controls
        // without a shown window. Must run on the UI thread.
        public InternalSelectionResult ReadFrom(object focused)
        {
            if (page == null) return InternalSelectionResult.NoPage;
            if (focused == null) return InternalSelectionResult.Unavailable(InternalSelectionReason.NoFocus);
            var element = focused as DependencyObject;
            if (element == null) return InternalSelectionResult.Unavailable(InternalSelectionReason.NotSelectable);
            if (!IsOnPage(element)) return InternalSelectionResult.Unavailable(InternalSelectionReason.FocusOutsidePage);
            // A focus that sits in document content - a hyperlink or another inline - is not a
            // selection the user made in that editor, so it never inherits the editor's older
            // selection.
            if (element is FrameworkContentElement) return InternalSelectionResult.Unavailable(InternalSelectionReason.ContentFocus);
            if (!(element is FrameworkElement)) return InternalSelectionResult.Unavailable(InternalSelectionReason.NotSelectable);
            var control = (FrameworkElement)element;
            string name = ControlName(control);
            if (IsSensitive(control, name)) return InternalSelectionResult.Unavailable(InternalSelectionReason.SensitiveControl);
            // A collapsed page keeps its controls' older selections; they are not what the user
            // just selected.
            if (!IsOnVisiblePage(control)) return InternalSelectionResult.Unavailable(InternalSelectionReason.HiddenPage);
            string source = AllowedSource(control, name);
            if (source == null) return InternalSelectionResult.Unavailable(InternalSelectionReason.NotSelectable);
            string text = SelectedTextOf(control);
            if (text == null) return InternalSelectionResult.Unavailable(InternalSelectionReason.NotSelectable);
            text = text.Trim();
            return text.Length == 0 ? InternalSelectionResult.Empty(source) : InternalSelectionResult.Of(text, source);
        }

        // The named translation-page sources, plus a reading control that opts in with the
        // shared tag. Nothing else on the page can answer, however it was focused.
        private static string AllowedSource(FrameworkElement control, string name)
        {
            if (!(control is TextBoxBase)) return null;
            if (name == InputName) return InputName;
            if (name == SourceName) return SourceName;
            if (name == TranslationName) return TranslationName;
            if (SelectableTag.Equals(control.Tag as string, StringComparison.Ordinal)) return SelectableTag;
            return null;
        }
        private static bool IsSensitive(FrameworkElement control, string name)
        {
            if (control is PasswordBox) return true;
            foreach (string sensitive in SensitiveNames)
                if (string.Equals(sensitive, name, StringComparison.Ordinal)) return true;
            return false;
        }
        private static string ControlName(FrameworkElement control)
        {
            try { return control.Name ?? ""; } catch { return ""; }
        }
        // The selection of the control itself. A RichTextBox answers through its selection
        // range, a TextBox through its selected text; nothing else has a selection to give.
        private static string SelectedTextOf(FrameworkElement control)
        {
            var rich = control as RichTextBox;
            if (rich != null) return rich.Selection == null ? null : rich.Selection.Text;
            var text = control as TextBox;
            return text == null ? null : text.SelectedText;
        }
        // Another window's control is not this page's selection, so its chain has to reach the
        // page this reader was built for.
        private bool IsOnPage(DependencyObject element)
        {
            DependencyObject current = element;
            for (int depth = 0; current != null && depth < MaxParentDepth; depth++) {
                if (ReferenceEquals(current, page)) return true;
                current = ParentOf(current);
            }
            return false;
        }
        // The visibility of the control's own chain decides whether its page is the one this
        // window shows. The chain is walked instead of trusting IsVisible, so a window that was
        // never rendered still answers for its own layout.
        private bool IsOnVisiblePage(DependencyObject element)
        {
            DependencyObject current = element;
            for (int depth = 0; current != null && depth < MaxParentDepth; depth++) {
                var framework = current as FrameworkElement;
                if (framework != null && framework.Visibility != Visibility.Visible) return false;
                if (ReferenceEquals(current, page)) return true;
                current = ParentOf(current);
            }
            return false;
        }
        private static DependencyObject ParentOf(DependencyObject element)
        {
            try {
                var logical = LogicalTreeHelper.GetParent(element);
                if (logical != null) return logical;
            } catch { }
            try { return VisualTreeHelper.GetParent(element); } catch { return null; }
        }
    }
}
