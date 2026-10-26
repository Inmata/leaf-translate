using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Leaf
{
    public sealed class SourceInteraction
    {
        private readonly AppShell shell;
        private readonly WorkspaceController workspace;
        private readonly PopupPresenter presenter;
        private Window Popup { get { return shell.Popup; } }
        internal bool fillingSource;
        internal long sourceRevision;
        private List<TextPiece> displayedPieces;
        private int[] displayToSource, sourceToDisplay;
        private int pendingHighlightStart = -1, pendingHighlightEnd = -1;
        internal readonly SourceHighlightLayer highlightLayer;
        internal SourceInteraction(AppShell shell, WorkspaceController workspace, PopupPresenter presenter)
        {
            this.shell = shell; this.workspace = workspace; this.presenter = presenter;
            highlightLayer = Ui.Get<SourceHighlightLayer>(Popup, "SourceHighlight");
        }
        public bool WordHighlighted { get { return pendingHighlightEnd > pendingHighlightStart; } }
        internal void Stop() { if (highlightLayer != null) highlightLayer.Stop(); }

        // Fills the read-only source box and builds its lookup tables once: word pieces
        // over the display text plus the offset maps against the record's own text.
        internal void FillSourceReading(string sourceText)
        {
            DetachWordHighlight();
            var box = Ui.Get<TextBox>(Popup, "SourceText");
            box.Text = sourceText ?? "";
            displayedPieces = null; displayToSource = null; sourceToDisplay = null;
            string display = box.Text;
            if (display.Length == 0) return;
            if (display != sourceText) BuildDisplayMap(sourceText ?? "", display);
            displayedPieces = TextTools.Pieces(display);
        }

        // The TextBox normalizes line breaks to "\r\n", so its offsets can drift from the
        // record's own text. Both directions are mapped once per fill; every later hit,
        // highlight or explained selection is a pure lookup.
        internal void BuildDisplayMap(string source, string display)
        {
            var forward = new int[display.Length + 1];
            var backward = new int[source.Length + 1];
            SourceOffsetMap.BuildOffsetMaps(source, display, forward, backward);
            displayToSource = forward; sourceToDisplay = backward;
        }

        internal int DisplayToSourceOffset(int index)
        {
            if (displayToSource == null) return Math.Max(0, Math.Min(index, Ui.Get<TextBox>(Popup, "SourceText").Text.Length));
            return displayToSource[Math.Max(0, Math.Min(index, displayToSource.Length - 1))];
        }

        internal int SourceToDisplayOffset(int index)
        {
            if (sourceToDisplay == null) return Math.Max(0, Math.Min(index, Ui.Get<TextBox>(Popup, "SourceText").Text.Length));
            return sourceToDisplay[Math.Max(0, Math.Min(index, sourceToDisplay.Length - 1))];
        }

        // The word piece under a display offset, mapped back to the record's own offsets,
        // or null when the offset is not inside a word.
        public TextPiece WordHitAt(int displayOffset)
        {
            if (displayedPieces == null || displayOffset < 0) return null;
            int lo = 0, hi = displayedPieces.Count - 1, found = -1;
            while (lo <= hi) {
                int mid = (lo + hi) / 2;
                var piece = displayedPieces[mid];
                if (displayOffset < piece.Start) hi = mid - 1;
                else if (displayOffset >= piece.Start + piece.Length) lo = mid + 1;
                else { found = mid; break; }
            }
            if (found < 0) return null;
            var hit = displayedPieces[found];
            if (!hit.IsWord) return null;
            if (displayToSource == null) return hit;
            int start = displayToSource[hit.Start];
            int end = displayToSource[hit.Start + hit.Length];
            return new TextPiece { Start = start, Length = end - start, Text = workspace.Current.Source.Substring(start, end - start), IsWord = true };
        }

        // The one selected piece is painted as a rounded band per line it touches. The
        // bands live in one background layer under the transparent reading box — behind
        // the glyphs, clipped to the box's viewport — and only that piece's own rectangles
        // are ever recomputed, on scroll or a real layout change.
        internal void HighlightSource()
        {
            if (workspace.SelectedWord == null) { DetachWordHighlight(); return; }
            var box = Ui.Get<TextBox>(Popup, "SourceText");
            if (box.Visibility != Visibility.Visible) { DetachWordHighlight(); return; }
            int start = SourceToDisplayOffset(workspace.SelectedWord.Start);
            int end = SourceToDisplayOffset(workspace.SelectedWord.Start + workspace.SelectedWord.Length);
            if (end <= start) { DetachWordHighlight(); return; }
            pendingHighlightStart = start; pendingHighlightEnd = end;
            if (highlightLayer != null) highlightLayer.Show(start, end);
        }

        internal void DetachWordHighlight()
        {
            pendingHighlightStart = -1; pendingHighlightEnd = -1;
            if (highlightLayer != null) highlightLayer.Clear();
        }

        // One path for explaining text from the keyboard and the context menu: a
        // non-whitespace selection explains the selected range; Enter additionally falls
        // back to the word under the caret. The box's display offsets map straight back
        // to the record's own text, so no guessing about line endings is needed.
        internal async Task ExplainSelectionOrCaretAsync(bool caretFallback)
        {
            var source = Ui.Get<TextBox>(Popup, "SourceText");
            if (workspace.Current == null || !workspace.Current.Completed) return;
            int start, end;
            if (source.SelectionLength > 0 && !string.IsNullOrWhiteSpace(source.SelectedText)) {
                start = DisplayToSourceOffset(source.SelectionStart);
                end = DisplayToSourceOffset(source.SelectionStart + source.SelectionLength);
            }
            else {
                if (!caretFallback) return;
                var piece = WordHitAt(source.CaretIndex);
                if (piece == null) return;
                await workspace.SelectWordAsync(piece, false);
                return;
            }
            if (end <= start) return;
            await workspace.SelectWordAsync(new TextPiece { Start = start, Length = end - start, Text = workspace.Current.Source.Substring(start, end - start), IsWord = true }, false);
        }

        public void BeginSourceEdit(bool focus)
        {
            shell.CancelCapture();
            // An editor the user has opened is an uncommitted original: an identification that is
            // still running must not move the page under the text being typed, nor store its own
            // target language while that text is undecided.
            workspace.CancelSwap();
            if (focus) sourceRevision++;
            var input = Ui.Get<TextBox>(Popup, "SourceInput");
            if (Ui.Get<Grid>(Popup, "SourceEditor").Visibility != Visibility.Visible) {
                fillingSource = true; input.Text = workspace.Current == null ? "" : workspace.Current.Source; fillingSource = false;
            }
            Ui.Visible(Ui.Get<TextBox>(Popup, "SourceText"), false);
            Ui.Visible(Ui.Get<Grid>(Popup, "SourceEditor"), true);
            Ui.Visible(Ui.Get<Button>(Popup, "EditSourceButton"), false);
            Ui.Get<TextBlock>(Popup, "TranslationCaption").Text = workspace.Current != null ? "上次译文" : "译文";
            Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), false);
            Ui.Visible(Ui.Get<Border>(Popup, "ChatPanel"), false);
            Ui.Visible(Ui.Get<Border>(Popup, "InputPanel"), false);
            presenter.Busy();
            Ui.Visible(Ui.Get<TextBox>(Popup, "SourcePlaceholder"), input.Text.Length == 0);
            // The submit arrow is the mouse entry point; empty input hides it instead of a tutorial line.
            Ui.Visible(Ui.Get<Button>(Popup, "TranslateButton"), !string.IsNullOrWhiteSpace(input.Text));
            if (focus) { if (shell.NativeEnabled) Popup.Activate(); input.Focus(); input.SelectAll(); }
        }

        // Brings the existing result back to the reading page without rebuilding it, so the
        // highlighted word, the conversation and any follow-up draft all survive.
        internal void RestoreCurrentPresentation(bool directInput)
        {
            if (workspace.Current == null) return;
            fillingSource = true;
            try { Ui.Get<TextBox>(Popup, "SourceInput").Text = workspace.Current.Source; }
            finally { fillingSource = false; }
            ShowSourceReadOnly();
            presenter.DrawTranslation(workspace.SelectedCard == null ? "" : workspace.SelectedCard.target_phrase);
            presenter.Topic();
            presenter.Busy();
            shell.ShowPopup();
            if (directInput) BeginSourceEdit(true);
        }

        internal void ShowSourceReadOnly()
        {
            Ui.Visible(Ui.Get<TextBox>(Popup, "SourceText"), true);
            Ui.Visible(Ui.Get<Grid>(Popup, "SourceEditor"), false);
            Ui.Visible(Ui.Get<Button>(Popup, "EditSourceButton"), true);
            Ui.Get<TextBlock>(Popup, "TranslationCaption").Text = "译文";
            Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), workspace.SelectedCard != null);
            Ui.Visible(Ui.Get<Border>(Popup, "ChatPanel"), workspace.Current != null && workspace.Current.Chat.Count > 0);
            presenter.Busy();
        }

        public async Task SubmitSourceAsync()
        {
            if (shell.IsExiting) return;
            shell.CancelCapture();
            sourceRevision++;
            string text = Ui.Get<TextBox>(Popup, "SourceInput").Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            await workspace.TranslateAsync(text, "输入", true);
        }

        // Reopens the source editor with a kept draft, exactly as the return entry does.
        internal void RestoreSourceEditor(string draft)
        {
            BeginSourceEdit(false);
            var input = Ui.Get<TextBox>(Popup, "SourceInput");
            fillingSource = true; input.Text = draft ?? ""; fillingSource = false;
            Ui.Visible(Ui.Get<TextBox>(Popup, "SourcePlaceholder"), input.Text.Length == 0);
            Ui.Visible(Ui.Get<Button>(Popup, "TranslateButton"), !string.IsNullOrWhiteSpace(input.Text));
        }
        // With no selection of its own page to translate, a shortcut in front of Leaf has no
        // foreign selection either. An editor the user has not committed to stays untouched;
        // otherwise the current result comes back. With no result at all the empty state
        // stays as it is: the input is entered through the explicit edit control, never
        // opened automatically.
        internal void HandleLeafSelectionShortcut(bool directInput)
        {
            if (workspace.Current == null) { MarkInconclusiveCapture(CaptureReason.None, false); shell.ShowPopup(); return; }
            if (HasPendingDraft()) { shell.ShowPopup(); return; }
            RestoreCurrentPresentation(false);
        }

        // A cleared editor is a draft action, not a new session; an unchanged prefill is not a draft.
        internal bool HasPendingDraft()
        {
            if (workspace.Current == null) return false;
            var input = Ui.Get<TextBox>(Popup, "SourceInput");
            if (Ui.Get<Grid>(Popup, "SourceEditor").Visibility != Visibility.Visible) return false;
            if (input.Text.Length == 0) return false;
            return input.Text != workspace.Current.Source;
        }

        // States the capture outcome on the source badge without touching the page: the
        // session, its drafts and any open editor stay exactly as they are.
        internal void MarkInconclusiveCapture(CaptureReason reason, bool clipboardMode)
        {
            bool inconclusive = reason != CaptureReason.Desktop && reason != CaptureReason.EmptySelection &&
                reason != CaptureReason.None;
            var badge = Ui.Get<TextBlock>(Popup, "SourceBadge");
            string mode = clipboardMode ? "剪贴板模式" : "选中模式";
            if (workspace.Current == null) badge.Text = inconclusive ? "未取得选区 · " + mode : mode;
            badge.ToolTip = inconclusive ? CaptureMessages.Hint(reason) : null;
        }

        // A definite failure still prepares the input surface it names; the specific reason
        // is reported by the caller.
        internal void PrepareManualInput(CaptureReason reason, bool clipboardMode, bool directInput)
        {
            MarkInconclusiveCapture(reason, clipboardMode);
            shell.ShowPopup();
            if (Ui.Get<Grid>(Popup, "SourceEditor").Visibility == Visibility.Visible &&
                Ui.Get<TextBox>(Popup, "SourceInput").Text.Length > 0) { presenter.Busy(); return; }
            BeginSourceEdit(directInput);
        }
    }
}
