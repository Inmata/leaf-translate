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
    public sealed class PopupTypography
    {
        private readonly AppShell shell;
        private readonly PopupPresenter presenter;
        private Window Popup { get { return shell.Popup; } }
        private bool interactiveResize, readingColumnMeasured;
        private double resizeFrozenScale = double.NaN;
        internal bool resizeTypographyPending;
        private static string readingColumnReport = "not measured";
        internal static string ReadingColumnReport { get { return readingColumnReport; } }
        internal PopupTypography(AppShell shell, PopupPresenter presenter) { this.shell = shell; this.presenter = presenter; }
        internal void Stop() { interactiveResize = false; resizeFrozenScale = double.NaN; resizeTypographyPending = false; }

        public static double TypographyScale(double width, double height)
        {
            return Math.Max(0.84, Math.Min(1, Math.Min(width / 456.0, height / 620.0)));
        }

        // The scale the reading page is built at right now: a hand-dragged edge keeps the one
        // the drag started from, and every other pass reads the window's own size.
        internal double CurrentTypographyScale()
        {
            return interactiveResize && !double.IsNaN(resizeFrozenScale)
                ? resizeFrozenScale : TypographyScale(Popup.Width, Popup.Height);
        }

        // The user has started dragging the window's edge. Moving the window raises the same
        // ends of the modal loop without ever sizing it, so only a sizing message reaches
        // here: the scale is frozen for the whole drag and the highlight stops drawing.
        internal void BeginInteractiveResize()
        {
            if (shell.IsDisposed || interactiveResize) return;
            interactiveResize = true;
            resizeFrozenScale = TypographyScale(Popup.Width, Popup.Height);
            if (presenter.Source.highlightLayer != null) presenter.Source.highlightLayer.Suspend();
        }

        // The drag is over at its final size: the highlight comes back, and the whole
        // typography ladder is written once for the size the window really has now. Every
        // later pass - the one a coalesced size change still raises - reads that same size.
        internal void EndInteractiveResize()
        {
            if (!interactiveResize) return;
            interactiveResize = false;
            resizeFrozenScale = double.NaN;
            if (presenter.Source.highlightLayer != null) presenter.Source.highlightLayer.Resume();
            UpdatePopupTypography();
        }

        // The popup is going away mid-drag: the frozen scale is released so the next show
        // reads the window's own size, and the pass the paused layer had queued is dropped
        // instead of painted on a window that is no longer shown. Nothing is redrawn here -
        // a window nobody is looking at is not worth a layout - so the release also records
        // that the reading page still owes itself one pass at that final size.
        internal void CancelInteractiveResize()
        {
            if (!interactiveResize) return;
            interactiveResize = false;
            resizeFrozenScale = double.NaN;
            resizeTypographyPending = true;
            if (presenter.Source.highlightLayer != null) presenter.Source.highlightLayer.CancelPending();
        }

        // What the interrupted drag left owing: the reading page is written once for the size
        // the window has now, and the highlight comes back from the selection that is current
        // - the one the check the reader is on really is - rather than from a range a paused
        // layer happened to keep.
        internal void ApplyPendingResizeTypography()
        {
            resizeTypographyPending = false;
            UpdatePopupTypography();
            presenter.Source.HighlightSource();
        }

        public void UpdatePopupTypography()
        {
            // Two states write nothing here. A hand-dragged window edge writes the ladder once,
            // when the drag ends, so every size change in between skips this pass entirely
            // instead of walking the paragraphs and controls per tick; content a drag builds -
            // a translation, a card - reads the frozen scale by itself. And a window closing
            // for good must not rebuild the reading page from a layout the close itself raised.
            if (interactiveResize || shell.IsDisposed) return;
            double scale = CurrentTypographyScale();
            // The ladder is fixed: a 19 dip original, a 22 dip translation and a 22 dip bold
            // card entry at full size, then 16 dip section headings and reading body over a
            // 14 dip auxiliary tier - the original and translation labels, the part of speech
            // and the source badge - that never shrinks; only reading text follows the
            // window's own 84% floor.
            var source = Ui.Get<TextBox>(Popup, "SourceText");
            source.FontSize = Math.Max(13, 19 * scale);
            source.MaxHeight = 100 * scale;
            var translation = Ui.Get<RichTextBox>(Popup, "TranslationText");
            translation.FontSize = translation.Document.FontSize = Math.Max(14, 22 * scale);
            foreach (var paragraph in translation.Document.Blocks.OfType<Paragraph>()) paragraph.LineHeight = 27 * scale;
            var editor = Ui.Get<TextBox>(Popup, "SourceInput");
            editor.FontSize = Math.Max(13, 18 * scale);
            SetTypography("ErrorText", 13, 21);
            // The learning card's own ladder - a 22 entry, 16 headings and reading body, a
            // 14 auxiliary line - lives in the one document it is built from. A real scale
            // change rewrites that ladder in place, keeping the document and the reader's
            // selection, so dragging the window's edge never re-parses the card on every tick;
            // only a card whose content changed - or one not built yet - is rebuilt. A hidden
            // card is left alone, and a refresh at the scale the document already has - this
            // method also runs for every chat message - keeps the document untouched.
            if (Ui.Get<Border>(Popup, "WordPanel").Visibility == Visibility.Visible) {
                if (double.IsNaN(presenter.Learning.wordCardScale)) presenter.Learning.DrawWordCard();
                else if (presenter.Learning.wordCardScale != scale) presenter.Learning.ApplyWordCardTypography(scale);
            }
            var chat = Ui.Get<StackPanel>(Popup, "ChatMessages").Children;
            for (int i = 0; i < chat.Count; i++) {
                bool label = i % 2 == 0;
                var text = chat[i] as TextBlock;
                if (text != null) {
                    text.FontSize = label ? 13 : Math.Max(13, 14 * scale);
                    if (!label) text.LineHeight = 24 * scale;
                    text.Margin = new Thickness(0, 0, 0, (label ? 7 : 20) * scale);
                    continue;
                }
            }
            double compact = (scale - 0.84) / 0.16;
            Ui.Get<Grid>(Popup, "PopupContent").Margin = new Thickness(22 + 8 * compact, 16, 22 + 8 * compact, 20 + 8 * compact);
            // The label and the original carry their own distance from the top through this
            // panel's one top inset: 14 dips at the window's 84% floor, rising to 24 at full
            // size - 14 + 10 per step, the same total a 20/24 top over a negative bottom used
            // to sum to. The panel never carries a negative inset, so the height it sums to -
            // and with it the translation and the learning card under it - stays where it was.
            Ui.Get<Border>(Popup, "SourcePanel").Padding = new Thickness(0, 14 + 10 * compact, 0, 0);
            Ui.Get<ScrollViewer>(Popup, "BodyScroll").Margin = new Thickness(0, 10 + 4 * compact, 0, 0);
            // The follow-up entry's footing is reading-page paper too, so it follows the
            // window down like every other inset: its full 36/16 at the full window and 16/8
            // at the 84% floor. This row is the last one that can give, and what it gives
            // back goes to the body above it - the page's only scrolling region. At the floor
            // the source panel, the entry and the input together take 280 of the reading
            // page's 292 dips on their own, so an expanded follow-up under the full footing
            // leaves the body about one dip to scroll in, and the next environment's slightly
            // taller text takes even that.
            Ui.Get<Grid>(Popup, "AskRow").Margin = new Thickness(0, 16 + 20 * compact, 0, 8 + 8 * compact);
            var input = Ui.Get<TextBox>(Popup, "QuestionInput"); input.FontSize = Math.Max(12, 14 * scale); input.Height = 66 * scale;
            AlignReadingColumn();
        }

        internal void AlignReadingColumn()
        {
            if (readingColumnMeasured) return;
            var page = Ui.Get<Grid>(Popup, "TranslatePage");
            var label = Ui.Get<TextBlock>(Popup, "SourceLabel");
            var source = Ui.Get<TextBox>(Popup, "SourceText");
            var translation = Ui.Get<RichTextBox>(Popup, "TranslationText");
            if (page.ActualWidth <= 0 || label.ActualWidth <= 0 || source.ActualWidth <= 0 || translation.ActualWidth <= 0) {
                readingColumnReport = "waiting for a laid-out page";
                return;
            }
            double labelOrigin = LeftInReadingPage(label, page);
            double sourceOrigin, translationOrigin;
            if (!TryTextOrigin(source, page, out sourceOrigin)) { readingColumnReport = "the original has no character rectangle yet"; return; }
            if (!TryTextOrigin(translation, page, out translationOrigin)) { readingColumnReport = "the translation has no character rectangle yet"; return; }
            double sourceInset = sourceOrigin - LeftInReadingPage(source, page);
            double flowInset = translationOrigin - LeftInReadingPage(translation, page);
            // The column is the label's own edge plus whatever a document host adds inside
            // itself; taking the larger inset is what keeps every margin non-negative.
            double column = labelOrigin + Math.Max(0, Math.Max(sourceInset, flowInset));
            NudgeLeft(label, column - labelOrigin);
            // The original and its band layer share one grid and move together; the editor grid
            // is that grid's sibling in the same stack and shares its left edge, and its box is
            // the same kind of control, so it takes the same nudge.
            double sourceShift = column - sourceOrigin;
            NudgeLeft(Ui.Get<Grid>(Popup, "SourceReading"), sourceShift);
            NudgeLeft(Ui.Get<Grid>(Popup, "SourceEditor"), sourceShift);
            // Both flows are RichTextBoxes built from the same style with a zero-padded
            // document, so the translation's measured inset is the card's inset too.
            double flowShift = column - translationOrigin;
            NudgeLeft(translation, flowShift);
            NudgeLeft(Ui.Get<RichTextBox>(Popup, "WordCard"), flowShift);
            var caption = Ui.Get<TextBlock>(Popup, "TranslationCaption");
            NudgeLeft(caption, column - LeftInReadingPage(caption, page));
            // The follow-up icon is the first thing inside its button, so the button's own left
            // padding is what stands between its edge and the icon.
            var ask = Ui.Get<Button>(Popup, "AskButton");
            NudgeLeft(ask, column - (LeftInReadingPage(ask, page) + ask.Padding.Left));
            readingColumnMeasured = true;
            readingColumnReport = "label " + labelOrigin.ToString("0.###") + ", original " + sourceOrigin.ToString("0.###") +
                " (inset " + sourceInset.ToString("0.###") + "), translation " + translationOrigin.ToString("0.###") +
                " (inset " + flowInset.ToString("0.###") + "), column " + column.ToString("0.###");
        }

        internal static void NudgeLeft(FrameworkElement element, double delta)
        {
            if (Math.Abs(delta) < 0.01) return;
            element.Margin = new Thickness(element.Margin.Left + delta, element.Margin.Top, element.Margin.Right, element.Margin.Bottom);
        }

        // Where a document host's first glyph starts, in the reading page's coordinates, or
        // false when the control has no laid-out text to ask. A box answers with the
        // insertion rectangle of its first index - a zero-width rectangle whose left edge is
        // exactly where its text begins - so a zero width is the normal answer there, not a
        // refusal; only a rectangle that was never produced is.
        internal static bool TryTextOrigin(TextBoxBase box, Visual page, out double origin)
        {
            origin = 0;
            Rect rect;
            var text = box as TextBox;
            if (text != null) {
                if (text.Text.Length == 0) return false;
                rect = text.GetRectFromCharacterIndex(0);
            }
            else {
                var flow = box as RichTextBox;
                if (flow == null) return false;
                rect = flow.Document.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            }
            if (rect.IsEmpty) return false;
            try {
                origin = box.TransformToAncestor(page).Transform(new Point(rect.Left, rect.Top)).X;
                return true;
            } catch (InvalidOperationException) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static double LeftInReadingPage(FrameworkElement element, Visual page)
        {
            return element.TransformToAncestor(page).Transform(new Point(0, 0)).X;
        }

        internal void SetTypography(string name, double font, double line)
        {
            var text = Ui.Get<TextBlock>(Popup, name); text.FontSize = font;
            if (line > 0) text.LineHeight = line;
        }
    }
}
