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
    // Why a translation starts, stated explicitly instead of guessed from the source kind:
    // a root replaces the return relation, an inherit keeps the current anchor (retry,
    // regenerate), and an internal child stays anchored to the root it was born from.
    internal enum SessionIntent
    {
        Root,
        Inherit,
        InternalChild
    }

    public sealed partial class AppShell
    {
        // How far the wheel routing walks the visual chain looking for the scroller under the
        // pointer before giving up and letting the page body take the notch.
        private const int MaxWheelDepth = 32;
        private bool fillingSource;
        private long sourceRevision;
        private DispatcherTimer copyFeedback;
        // The source text currently filled into the reading box; identical sources keep the
        // box, its scroll position and the word highlight instead of refilling it.
        private string displayedSource;
        // The reading box is a plain read-only TextBox: word pieces live over its display
        // text, and the offset maps translate between the box (which normalizes line breaks
        // to "\r\n") and the record's own text. Built once per fill, never per selection.
        private List<TextPiece> displayedPieces;
        private int[] displayToSource;
        private int[] sourceToDisplay;
        private SourceHighlightLayer highlightLayer;
        // The piece waiting to be painted, in the reading box's display offsets: the layer
        // keeps it and paints once the box has real geometry, so no re-attach pass exists.
        private int pendingHighlightStart = -1, pendingHighlightEnd = -1;
        private double bodyScrollBackup;
        private PreviousSessionSnapshot previousSession;

        private sealed class PreviousSessionSnapshot
        {
            public TranslationRecord Record;
            // The epoch the record was created under: a clear, a delete or a history disable
            // while the sub-session was on screen invalidates it, and returning must not
            // rewrite it under the newer epoch.
            public long HistoryEpoch;
            public TextPiece Word;
            public WordCard Card;
            public bool SourceEditorOpen;
            public string SourceDraft;
            public bool InputPanelOpen;
            public double SourceScroll;
            public double BodyScrollOffset;
            public bool ErrorVisible;
            public string ErrorText;
            public Action Retry;
        }

        private void InitializePopup()
        {
            Popup.Closing += (s, e) => { if (!exiting) { e.Cancel = true; var ignored = LeaveSettingsThenAsync(HidePopup); } };
            Popup.LocationChanged += (s, e) => QueuePlacementSave();
            Popup.SizeChanged += (s, e) => { UpdatePopupTypography(); QueuePlacementSave(); };
            Ui.Click(Popup, "HideButton", () => { var ignored = LeaveSettingsThenAsync(HidePopup); });
            Ui.Click(Popup, "SettingsButton", ShowSettingsPage);
            Ui.Click(Popup, "EditSourceButton", () => BeginSourceEdit(true));
            Ui.Click(Popup, "TranslateButton", async () => await SubmitSourceAsync());
            var sourceInput = Ui.Get<TextBox>(Popup, "SourceInput");
            sourceInput.TextChanged += (s, e) => {
                Ui.Visible(Ui.Get<TextBox>(Popup, "SourcePlaceholder"), sourceInput.Text.Length == 0);
                Ui.Visible(Ui.Get<Button>(Popup, "TranslateButton"), !string.IsNullOrWhiteSpace(sourceInput.Text));
                if (!fillingSource) { CancelCapture(); sourceRevision++; }
            };
            sourceInput.PreviewKeyDown += async (s, e) => {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; await SubmitSourceAsync(); }
                else if (e.Key == Key.Escape && Current != null) { e.Handled = true; ShowSourceReadOnly(); }
            };
            Ui.Get<TextBox>(Popup, "SourceText").PreviewKeyDown += (s, e) => {
                if (e.Key == Key.Back || e.Key == Key.Delete) {
                    e.Handled = true; BeginSourceEdit(true); sourceInput.Clear();
                }
                else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) {
                    // The keyboard reaches the same explanation the pointer has: Enter
                    // explains the selected range, or the word under the caret when
                    // nothing is selected. Navigation and copy stay untouched.
                    e.Handled = true;
                    var ignored = ExplainSelectionOrCaretAsync(true);
                }
            };
            Ui.Get<TextBox>(Popup, "SourceText").PreviewTextInput += (s, e) => {
                e.Handled = true; BeginSourceEdit(true); sourceInput.Text = e.Text; sourceInput.CaretIndex = sourceInput.Text.Length;
            };
            // A click without a selection explains the word under the pointer; a drag that
            // ends with a selection is the user's own selection and is never interpreted.
            int pointerDownOffset = -1;
            var sourceReading = Ui.Get<TextBox>(Popup, "SourceText");
            sourceReading.PreviewMouseLeftButtonDown += (s, e) => {
                pointerDownOffset = sourceReading.GetCharacterIndexFromPoint(e.GetPosition(sourceReading), true);
            };
            sourceReading.PreviewMouseLeftButtonUp += (s, e) => {
                if (e.ClickCount != 1 || sourceReading.SelectionLength > 0) return;
                int offset = sourceReading.GetCharacterIndexFromPoint(e.GetPosition(sourceReading), true);
                if (offset < 0 || offset != pointerDownOffset) return;
                var piece = WordHitAt(offset);
                if (piece == null) return;
                e.Handled = true;
                var ignored = SelectWordAsync(piece, false);
            };
            // The highlight paints in one background layer under the reading box; the
            // layer defers its own geometry until the box has real layout, so a highlight
            // recorded before the window ever renders needs no re-attach pass.
            highlightLayer = Ui.Get<SourceHighlightLayer>(Popup, "SourceHighlight");
            highlightLayer.Bind(sourceReading);
            Ui.Click(Popup, "PinButton", () => {
                pinned = !pinned;
                Ui.Get<Button>(Popup, "PinButton").ToolTip = pinned ? "取消置顶，窗口外点击会收起" : "置顶并保持显示";
                UpdateTopBarState();
            });
            Ui.Get<Grid>(Popup, "DragBar").MouseLeftButtonDown += (s, e) => {
                if (!NativeEnabled || (e.OriginalSource is DependencyObject && HasButtonParent((DependencyObject)e.OriginalSource))) return;
                try {
                    Popup.DragMove(); RememberPlacement();
                } catch (UserError error) { ShowError(error.Message, null); }
                catch (InvalidOperationException) { }
            };
            Ui.Click(Popup, "CancelButton", StopForRetry);
            Ui.Click(Popup, "RetryButton", () => { var action = retry; ClearError(); if (action != null) action(); });
            Ui.Click(Popup, "SetupButton", ShowSettingsPage);
            Ui.Click(Popup, "BackToPreviousButton", ReturnToPreviousSession);
            Ui.Click(Popup, "AskButton", () => {
                if (Current == null || !Current.Completed) return;
                Ui.Visible(Ui.Get<System.Windows.Controls.Border>(Popup, "InputPanel"), true);
                Topic(); Busy(); Ui.Get<TextBox>(Popup, "QuestionInput").Focus();
            });
            Ui.Click(Popup, "CloseQuestionButton", () => { Ui.Visible(Ui.Get<Border>(Popup, "InputPanel"), false); Busy(); });
            Ui.Click(Popup, "SendButton", async () => await SendChatAsync());
            Ui.Get<TextBox>(Popup, "QuestionInput").KeyDown += async (s, e) => {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) {
                    e.Handled = true; await SendChatAsync();
                }
            };
            Ui.Get<TextBox>(Popup, "QuestionInput").TextChanged += (s, e) => {
                if (Current != null) Current.Draft = Ui.Get<TextBox>(Popup, "QuestionInput").Text;
            };
            Ui.Click(Popup, "ExpandWordButton", async () => {
                if (Current == null || !Current.Completed) return;
                var piece = TextTools.Pieces(Current.Source).FirstOrDefault(p => p.IsWord);
                if (piece != null) await SelectWordAsync(piece, false);
            });
            Ui.Click(Popup, "BackToSentence", () => {
                wordGeneration.Next();
                if (wordCancellation != null) wordCancellation.Cancel();
                wordBusy = false; selectedWord = null; selectedCard = null;
                HighlightSource(); DrawTranslation(""); Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), false); Topic(); Busy();
            });
            var contextMenu = new ContextMenu();
            var explain = new MenuItem { Header = "解释选中片段" };
            explain.Click += async (s, e) => { await ExplainSelectionOrCaretAsync(false); };
            contextMenu.Items.Add(explain);
            var copySource = new MenuItem { Header = "复制选中内容", Command = ApplicationCommands.Copy };
            contextMenu.Items.Add(copySource);
            Ui.Get<TextBox>(Popup, "SourceText").ContextMenu = contextMenu;
            var translationMenu = new ContextMenu();
            translationMenu.Items.Add(new MenuItem { Header = "复制", Command = ApplicationCommands.Copy });
            translationMenu.Items.Add(new MenuItem { Header = "全选", Command = ApplicationCommands.SelectAll });
            var regenerate = new MenuItem { Header = "重新生成" };
            regenerate.Click += async (s, e) => { if (Current != null) await TranslateAsync(Current.Source, Current.SourceKind, true, SessionIntent.Inherit); };
            translationMenu.Items.Add(regenerate);
            Ui.Get<RichTextBox>(Popup, "TranslationText").ContextMenu = translationMenu;
            foreach (string name in new[] { "SourceText", "TranslationText" }) {
                var reading = Ui.Get<TextBoxBase>(Popup, name);
                reading.Tag = "LeafSelectableText";
                reading.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
                    async (s, e) => { e.Handled = true; await CopySelectionAsync(SelectionTextOf(reading)); },
                    (s, e) => { e.CanExecute = SelectionTextOf(reading).Length > 0; e.Handled = true; }));
            }
            foreach (string name in new[] { "WordMeaning", "WordMeta" })
                WireSelectableReading(Ui.Get<RichTextBox>(Popup, name));
            WireLearningText(Ui.Get<TextBox>(Popup, "WordTitle"));
            InitializeSettingsPage();
            WireWheelRouting();
            UpdateTopBarState();
            DisplayRecord();
            UpdatePopupTypography();
        }
        // The top bar carries one dark chip at a time: the settings entry while its page is
        // open, the pin while the window is really pinned. Leaving either state — the back
        // entry, the outside click, the window close or the pin's own click — puts the plain
        // square back, and nothing here takes focus.
        private void UpdateTopBarState()
        {
            SetTopBarState(Ui.Get<Button>(Popup, "SettingsButton"), IsSettingsPageOpen);
            SetTopBarState(Ui.Get<Button>(Popup, "PinButton"), pinned);
            SetTopBarState(Ui.Get<Button>(Popup, "HideButton"), false);
        }
        private void SetTopBarState(Button button, bool active)
        {
            button.Style = (Style)Popup.FindResource(active ? "PaperTopButtonActive" : "PaperTopButton");
        }
        // One wheel decision for the whole reading page, taken while the event is still
        // tunneling: the region under the pointer spends the notch itself while it can still
        // move, and at its own edge the same notch continues in the page's body scroller
        // instead of being swallowed. Deciding before any scroller has moved keeps one notch
        // from reaching two of them, and the region comes from the pointer's own hit target,
        // so hovering alone is enough - no click and no focus change. Nothing is rebuilt.
        private void WireWheelRouting()
        {
            var page = Ui.Get<Grid>(Popup, "TranslatePage");
            page.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler((s, e) => {
                var body = Ui.Get<ScrollViewer>(Popup, "BodyScroll");
                var inner = WheelTargetUnder(e.OriginalSource as DependencyObject, body);
                if (inner != null && CanSpendWheel(inner, e.Delta)) return;
                // The body's own offsets are read after its pending layout, so a burst of
                // notches cannot compute the same target twice from a stale offset.
                body.UpdateLayout();
                double target = body.VerticalOffset - e.Delta / 120.0 * 48.0;
                body.ScrollToVerticalOffset(Math.Max(0, Math.Min(target, body.ScrollableHeight)));
                // The notch is spent here whether or not the body could still move: the
                // region under the pointer already refused it, and leaving the event unhandled
                // would only let that region swallow it with no effect.
                e.Handled = true;
            }), false);
        }
        // The innermost scroller the pointer is over, never the body itself: the body is the
        // fallback every inner region hands its spent notch to.
        private static ScrollViewer WheelTargetUnder(DependencyObject origin, ScrollViewer body)
        {
            DependencyObject node = origin;
            for (int depth = 0; node != null && depth < MaxWheelDepth; depth++) {
                var viewer = node as ScrollViewer;
                if (viewer != null && !ReferenceEquals(viewer, body)) return viewer;
                node = VisualParentOf(node);
            }
            return null;
        }
        // Whether the region under the pointer can spend this notch itself. A scroller already
        // at its edge in that direction refuses it, so the wheel continues in the body.
        private static bool CanSpendWheel(ScrollViewer viewer, int delta)
        {
            double offset = viewer.VerticalOffset;
            return delta > 0 ? offset > 0.5 : offset < viewer.ScrollableHeight - 0.5;
        }
        private static DependencyObject VisualParentOf(DependencyObject node)
        {
            try { return VisualTreeHelper.GetParent(node); } catch { return null; }
        }
        private static bool HasButtonParent(DependencyObject item)
        {
            while (item != null) {
                if (item is Button) return true;
                item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
            }
            return false;
        }
        private async Task CopySelectionAsync(string text)
        {
            try {
                if (NativeEnabled) await Native.CopyTextAsync(text);
                Ui.Get<TextBlock>(Popup, "CopyStatus").Text = "已复制";
                if (copyFeedback == null) {
                    copyFeedback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    copyFeedback.Tick += (s, e) => { copyFeedback.Stop(); Ui.Get<TextBlock>(Popup, "CopyStatus").Text = ""; };
                }
                copyFeedback.Stop(); copyFeedback.Start();
            } catch (Exception error) { Log.Event("copy_failed", error); ShowError(error is UserError ? error.Message : "复制失败，请稍后再试。", null); }
        }
        // RichTextBox answers through its selection range, TextBox through its selected
        // text; both are copy sources on the reading page.
        private static string SelectionTextOf(TextBoxBase box)
        {
            var rich = box as RichTextBox;
            if (rich != null) return rich.Selection.Text;
            var text = box as TextBox;
            return text != null ? text.SelectedText : "";
        }
        // Fills the read-only source box and builds its lookup tables once: word pieces
        // over the display text plus the offset maps against the record's own text.
        private void FillSourceReading(string sourceText)
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
        private void BuildDisplayMap(string source, string display)
        {
            var forward = new int[display.Length + 1];
            var backward = new int[source.Length + 1];
            BuildOffsetMaps(source, display, forward, backward);
            displayToSource = forward; sourceToDisplay = backward;
        }
        // The pure map walk, split from the fill so the regression checks can ask for maps
        // over any text pair without a window: both arrays are sized one past their text,
        // every index is written and each sequence is monotone, so a lookup can never read
        // a default 0.
        public static void BuildOffsetMaps(string source, string display, int[] displayToSource, int[] sourceToDisplay)
        {
            int d = 0, s = 0;
            while (d < display.Length && s < source.Length) {
                displayToSource[d] = s; sourceToDisplay[s] = d;
                bool displayPair = display[d] == '\r' && d + 1 < display.Length && display[d + 1] == '\n';
                bool sourcePair = source[s] == '\r' && s + 1 < source.Length && source[s + 1] == '\n';
                if (displayPair && sourcePair) {
                    // The pair interiors keep both maps total and monotone: the box never
                    // queries them, but a gap would read as offset 0.
                    displayToSource[d + 1] = s + 1; sourceToDisplay[s + 1] = d + 1;
                    d += 2; s += 2;
                }
                else if (displayPair) {
                    // The box turned one source "\n" into a "\r\n" pair: the pair's tail
                    // closes that source line, and the next source char sits after the pair.
                    displayToSource[d + 1] = s + 1;
                    d += 2; s += 1;
                }
                else if (sourcePair) {
                    sourceToDisplay[s + 1] = d + 1;
                    displayToSource[d] = s + 1;
                    d += 1; s += 2;
                }
                else { d += 1; s += 1; }
            }
            for (; d <= display.Length; d++) displayToSource[d] = Math.Min(s, source.Length);
            for (; s <= source.Length; s++) sourceToDisplay[s] = Math.Min(d, display.Length);
        }
        private int DisplayToSourceOffset(int index)
        {
            if (displayToSource == null) return Math.Max(0, Math.Min(index, Ui.Get<TextBox>(Popup, "SourceText").Text.Length));
            return displayToSource[Math.Max(0, Math.Min(index, displayToSource.Length - 1))];
        }
        private int SourceToDisplayOffset(int index)
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
            return new TextPiece { Start = start, Length = end - start, Text = Current.Source.Substring(start, end - start), IsWord = true };
        }
        // A read-only learning control: selectable, copyable, and marked so the selection
        // source can discover it. Inputs (password, settings, the follow-up editor) are
        // never marked.
        private void WireSelectableReading(RichTextBox box)
        {
            box.Tag = "LeafSelectableText";
            box.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
                async (s, e) => { e.Handled = true; await CopySelectionAsync(box.Selection.Text); },
                (s, e) => { e.CanExecute = !box.Selection.IsEmpty; e.Handled = true; }));
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "复制", Command = ApplicationCommands.Copy });
            menu.Items.Add(new MenuItem { Header = "全选", Command = ApplicationCommands.SelectAll });
            box.ContextMenu = menu;
        }
        // A learning heading is ordinary paper text, not a control label: the same read-only
        // box as the reading area, so the word entry and every section title answer Ctrl+C
        // and the in-window selection shortcut with the text under the user's own selection.
        private TextBox NewLearningTitle(string text)
        {
            var box = new TextBox {
                Style = (Style)Popup.FindResource("PaperTitle"), Text = text ?? "",
                FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("Ink"), IsTabStop = false
            };
            WireLearningText(box);
            return box;
        }
        // The 复制/全选 menu and the shared selectable mark the reading boxes carry, for the
        // learning titles too: the word entry and every section heading are the same ordinary
        // paper text, so the in-window shortcut reads their selection exactly as it reads the
        // reading controls'.
        private static void WireLearningText(TextBox box)
        {
            box.Tag = "LeafSelectableText";
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "复制", Command = ApplicationCommands.Copy });
            menu.Items.Add(new MenuItem { Header = "全选", Command = ApplicationCommands.SelectAll });
            box.ContextMenu = menu;
        }
        private RichTextBox NewReadingBox()
        {
            var box = new RichTextBox { Style = (Style)Application.Current.Resources["ReadingText"], Foreground = Ui.Brush("Secondary") };
            box.Document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI") };
            WireSelectableReading(box);
            return box;
        }
        // Plain text into a reading box: "\n" inside a Run paints a line break, so
        // paragraphs, surrogate pairs and combining marks survive without markup.
        private static void SetReadingText(RichTextBox box, string text)
        {
            box.Document.Blocks.Clear();
            var paragraph = new Paragraph { Margin = new Thickness(0) };
            paragraph.Inlines.Add(new Run(text ?? ""));
            box.Document.Blocks.Add(paragraph);
        }
        private static void ApplyReadingTypography(RichTextBox box, double font, double line)
        {
            box.FontSize = box.Document.FontSize = font;
            if (line <= 0) return;
            foreach (var paragraph in box.Document.Blocks.OfType<Paragraph>()) paragraph.LineHeight = line;
        }
        public void BeginSourceEdit(bool focus)
        {
            CancelCapture();
            if (focus) sourceRevision++;
            var input = Ui.Get<TextBox>(Popup, "SourceInput");
            if (Ui.Get<Grid>(Popup, "SourceEditor").Visibility != Visibility.Visible) {
                fillingSource = true; input.Text = Current == null ? "" : Current.Source; fillingSource = false;
            }
            Ui.Visible(Ui.Get<TextBox>(Popup, "SourceText"), false);
            Ui.Visible(Ui.Get<Grid>(Popup, "SourceEditor"), true);
            Ui.Visible(Ui.Get<Button>(Popup, "EditSourceButton"), false);
            Ui.Get<TextBlock>(Popup, "TranslationCaption").Text = Current != null ? "上次译文" : "译文";
            Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), false);
            Ui.Visible(Ui.Get<Border>(Popup, "ChatPanel"), false);
            Ui.Visible(Ui.Get<Border>(Popup, "InputPanel"), false);
            Busy();
            Ui.Visible(Ui.Get<TextBox>(Popup, "SourcePlaceholder"), input.Text.Length == 0);
            // The submit arrow is the mouse entry point; empty input hides it instead of a tutorial line.
            Ui.Visible(Ui.Get<Button>(Popup, "TranslateButton"), !string.IsNullOrWhiteSpace(input.Text));
            if (focus) { if (NativeEnabled) Popup.Activate(); input.Focus(); input.SelectAll(); }
        }
        // Brings the existing result back to the reading page without rebuilding it, so the
        // highlighted word, the conversation and any follow-up draft all survive.
        private void RestoreCurrentPresentation(bool directInput)
        {
            if (Current == null) return;
            fillingSource = true;
            try { Ui.Get<TextBox>(Popup, "SourceInput").Text = Current.Source; }
            finally { fillingSource = false; }
            ShowSourceReadOnly();
            DrawTranslation(selectedCard == null ? "" : selectedCard.target_phrase);
            Topic();
            Busy();
            ShowPopup();
            if (directInput) BeginSourceEdit(true);
        }
        private void ShowSourceReadOnly()
        {
            Ui.Visible(Ui.Get<TextBox>(Popup, "SourceText"), true);
            Ui.Visible(Ui.Get<Grid>(Popup, "SourceEditor"), false);
            Ui.Visible(Ui.Get<Button>(Popup, "EditSourceButton"), true);
            Ui.Get<TextBlock>(Popup, "TranslationCaption").Text = "译文";
            Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), selectedCard != null);
            Ui.Visible(Ui.Get<Border>(Popup, "ChatPanel"), Current != null && Current.Chat.Count > 0);
            Busy();
        }
        public async Task SubmitSourceAsync()
        {
            if (exiting) return;
            CancelCapture();
            sourceRevision++;
            string text = Ui.Get<TextBox>(Popup, "SourceInput").Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            await TranslateAsync(text, "输入", true);
        }
        public static double TypographyScale(double width, double height)
        {
            return Math.Max(0.84, Math.Min(1, Math.Min(width / 456.0, height / 620.0)));
        }
        public void UpdatePopupTypography()
        {
            double scale = TypographyScale(Popup.Width, Popup.Height);
            // The ladder is fixed: 16 reading, 17 translation, 18 titles, 15 section titles
            // and a 13 auxiliary tier that never shrinks; only reading text follows the
            // window's own 84% floor.
            var source = Ui.Get<TextBox>(Popup, "SourceText");
            source.FontSize = Math.Max(13, 16 * scale);
            source.MaxHeight = 100 * scale;
            var translation = Ui.Get<RichTextBox>(Popup, "TranslationText");
            translation.FontSize = translation.Document.FontSize = Math.Max(14, 17 * scale);
            foreach (var paragraph in translation.Document.Blocks.OfType<Paragraph>()) paragraph.LineHeight = 27 * scale;
            var editor = Ui.Get<TextBox>(Popup, "SourceInput");
            editor.FontSize = Math.Max(13, 16 * scale);
            Ui.Get<TextBox>(Popup, "WordTitle").FontSize = Math.Max(14, 18 * scale);
            ApplyReadingTypography(Ui.Get<RichTextBox>(Popup, "WordMeaning"), Math.Max(13, 16 * scale), 25 * scale);
            ApplyReadingTypography(Ui.Get<RichTextBox>(Popup, "WordMeta"), 13, 0);
            SetTypography("ErrorText", 13, 21);
            // Learning sections: a 15 SemiBold title 8 over 16/25 reading content, with 24
            // between sections. Chat labels stay small and tight.
            var learning = Ui.Get<StackPanel>(Popup, "LearningSections").Children;
            for (int i = 0; i < learning.Count; i++) {
                var title = learning[i] as TextBox;
                if (title != null) {
                    title.FontSize = Math.Max(13, 15 * scale);
                    title.Margin = new Thickness(0, 0, 0, 8 * scale);
                    continue;
                }
                var reading = learning[i] as RichTextBox;
                if (reading == null) continue;
                ApplyReadingTypography(reading, Math.Max(12, 16 * scale), 25 * scale);
                reading.Margin = new Thickness(0, 0, 0, 24 * scale);
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
            Ui.Get<Grid>(Popup, "PopupContent").Margin = new Thickness(18 + 8 * compact, 12, 18 + 8 * compact, 16 + 8 * compact);
            Ui.Get<Border>(Popup, "SourcePanel").Padding = new Thickness(0, 8 + 4 * compact, 0, 6 + 6 * compact);
            Ui.Get<ScrollViewer>(Popup, "BodyScroll").Margin = new Thickness(0, 20 + 4 * compact, 0, 0);
            var input = Ui.Get<TextBox>(Popup, "QuestionInput"); input.FontSize = Math.Max(12, 14 * scale); input.Height = 66 * scale;
        }
        private void SetTypography(string name, double font, double line)
        {
            var text = Ui.Get<TextBlock>(Popup, name); text.FontSize = font;
            if (line > 0) text.LineHeight = line;
        }
        private static string SourceKindLabel(string kind)
        {
            // Internal selections are Leaf's own text on screen; clipboard and typed input
            // stay labeled as what they really are.
            return kind == "选中文字" ? "选中文本" : kind;
        }

        private void DisplayRecord()
        {
            selectedWord = null; selectedCard = null; ClearError();
            var source = Ui.Get<TextBox>(Popup, "SourceText");
            Ui.Get<TextBlock>(Popup, "CopyStatus").Text = "";
            fillingSource = true; Ui.Get<TextBox>(Popup, "SourceInput").Text = Current == null ? "" : Current.Source; fillingSource = false;
            ShowSourceReadOnly();
            Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), false);
            Ui.Visible(Ui.Get<Border>(Popup, "InputPanel"), false);
            Ui.Visible(Ui.Get<Button>(Popup, "ExpandWordButton"), Current != null && Current.Completed && TextTools.IsWordInput(Current.Source));
            if (Current == null) {
                if (displayedSource != null) { source.Clear(); displayedSource = null; }
                DrawTranslation(""); BeginSourceEdit(false);
                Ui.Get<TextBox>(Popup, "QuestionInput").Clear();
                Ui.Visible(Ui.Get<Border>(Popup, "ChatPanel"), false); Busy(); UpdateBackEntry(); return;
            }
            Ui.Get<TextBlock>(Popup, "SourceBadge").Text = demo ? "演示内容 · 未调用 API" : SourceKindLabel(Current.SourceKind);
            if (displayedSource != Current.Source) {
                // Filling the box and tokenizing it dominates large-record display; an
                // unchanged source reuses the box, its scroll position and any highlight.
                FillSourceReading(Current.Source);
                displayedSource = Current.Source;
            } else {
                HighlightSource();
            }
            UpdatePopupTypography();
            DrawTranslation("");
            Ui.Get<TextBox>(Popup, "QuestionInput").Text = Current.Draft ?? "";
            DrawChat(); Topic(); Busy(); Ui.Get<ScrollViewer>(Popup, "BodyScroll").ScrollToTop();
            UpdateBackEntry();
        }

        // The one selected piece is painted as a rounded band per line it touches. The
        // bands live in one background layer under the transparent reading box — behind
        // the glyphs, clipped to the box's viewport — and only that piece's own rectangles
        // are ever recomputed, on scroll or a real layout change.
        private void HighlightSource()
        {
            if (selectedWord == null) { DetachWordHighlight(); return; }
            var box = Ui.Get<TextBox>(Popup, "SourceText");
            if (box.Visibility != Visibility.Visible) { DetachWordHighlight(); return; }
            int start = SourceToDisplayOffset(selectedWord.Start);
            int end = SourceToDisplayOffset(selectedWord.Start + selectedWord.Length);
            if (end <= start) { DetachWordHighlight(); return; }
            pendingHighlightStart = start; pendingHighlightEnd = end;
            if (highlightLayer != null) highlightLayer.Show(start, end);
        }
        private void DetachWordHighlight()
        {
            pendingHighlightStart = -1; pendingHighlightEnd = -1;
            if (highlightLayer != null) highlightLayer.Clear();
        }
        // The highlight intent survives until explicitly cleared.
        public bool WordHighlighted { get { return pendingHighlightEnd > pendingHighlightStart; } }
        // One path for explaining text from the keyboard and the context menu: a
        // non-whitespace selection explains the selected range; Enter additionally falls
        // back to the word under the caret. The box's display offsets map straight back
        // to the record's own text, so no guessing about line endings is needed.
        private async Task ExplainSelectionOrCaretAsync(bool caretFallback)
        {
            var source = Ui.Get<TextBox>(Popup, "SourceText");
            if (Current == null || !Current.Completed) return;
            int start, end;
            if (source.SelectionLength > 0 && !string.IsNullOrWhiteSpace(source.SelectedText)) {
                start = DisplayToSourceOffset(source.SelectionStart);
                end = DisplayToSourceOffset(source.SelectionStart + source.SelectionLength);
            }
            else {
                if (!caretFallback) return;
                var piece = WordHitAt(source.CaretIndex);
                if (piece == null) return;
                await SelectWordAsync(piece, false);
                return;
            }
            if (end <= start) return;
            await SelectWordAsync(new TextPiece { Start = start, Length = end - start, Text = Current.Source.Substring(start, end - start), IsWord = true }, false);
        }

        private void DrawTranslation(string target)
        {
            var box = Ui.Get<RichTextBox>(Popup, "TranslationText");
            box.Document.Blocks.Clear();
            var block = new Paragraph { Margin = new Thickness(0), LineHeight = 27 * TypographyScale(Popup.Width, Popup.Height) };
            box.Document.Blocks.Add(block);
            string translation = Current == null ? "" : Current.Translation;
            if (string.IsNullOrWhiteSpace(translation)) { block.Inlines.Add(new Run(Current != null ? "正在翻译…" : "译文会显示在这里。")); return; }
            // The model answer is parsed into presentation spans: **paired** markers become
            // bold runs, escapes and code spans stay literal. Copy, history and the LLM
            // context keep the raw text; only this reading view shows the emphasis.
            var spans = TranslationPresentation.Parse(translation);
            string display = string.Concat(spans.Select(span => span.Text));
            int index = string.IsNullOrEmpty(target) ? -1 : display.IndexOf(target, StringComparison.Ordinal);
            if (index < 0) { AddTranslationRuns(block, spans, -1, -1); return; }
            AddTranslationRuns(block, spans, index, index + target.Length);
        }
        // One run per parsed span; the highlighted piece is cut out at display offsets so
        // the highlight always lands on the text the user actually reads.
        private static void AddTranslationRuns(Paragraph block, IReadOnlyList<TranslationSpan> spans, int highlightStart, int highlightEnd)
        {
            int offset = 0;
            foreach (var span in spans) {
                int spanStart = offset, spanEnd = offset + span.Text.Length;
                offset = spanEnd;
                if (spanEnd <= highlightStart || spanStart >= highlightEnd) { AddEmphasisRun(block, span.Text, span.Bold, false); continue; }
                int cutStart = Math.Max(spanStart, highlightStart) - spanStart;
                int cutEnd = Math.Min(spanEnd, highlightEnd) - spanStart;
                AddEmphasisRun(block, span.Text.Substring(0, cutStart), span.Bold, false);
                AddEmphasisRun(block, span.Text.Substring(cutStart, cutEnd - cutStart), span.Bold, true);
                AddEmphasisRun(block, span.Text.Substring(cutEnd), span.Bold, false);
            }
        }
        private static void AddEmphasisRun(Paragraph block, string text, bool bold, bool highlight)
        {
            if (text.Length == 0) return;
            var run = new Run(text);
            if (bold) run.FontWeight = FontWeights.Bold;
            if (highlight) run.Background = Ui.Brush("WordHighlight");
            block.Inlines.Add(run);
        }
        public void ShowError(string text, Action retryAction)
        {
            retry = retryAction;
            Ui.Get<TextBlock>(Popup, "ErrorText").Text = text;
            Ui.Visible(Ui.Get<Border>(Popup, "ErrorPanel"), true);
            Ui.Visible(Ui.Get<Button>(Popup, "RetryButton"), retryAction != null);
        }
        private void ClearError()
        {
            retry = null;
            Ui.Visible(Ui.Get<Border>(Popup, "ErrorPanel"), false);
            // Without an error there is no retry entry either, so a later check can read the
            // button's own state instead of the visibility of the panel hiding it.
            Ui.Visible(Ui.Get<Button>(Popup, "RetryButton"), false);
        }
        private string KeyFor(ProviderProfile provider)
        {
            if (CredentialReader != null) return CredentialReader(provider);
            if (!NativeEnabled || credentialProfiles == null) return "test-key";
            return credentialProfiles.Read(provider);
        }
        private bool BlockedByRecovery()
        {
            if (!Store.NeedsExplicitRecovery) return false;
            ShowError("上次保存没有完成，请重新应用配置后重试。", ShowSettingsPage);
            ShowPopup();
            return true;
        }
        public async Task TranslateAsync(string text, string sourceKind, bool force)
        {
            await TranslateAsync(text, sourceKind, force, SessionIntent.Root);
        }
        private async Task TranslateAsync(string text, string sourceKind, bool force, SessionIntent intent)
        {
            if (exiting) return;
            try { text = TextTools.ValidateInput(text); }
            catch (UserError error) { ShowError(error.Message, null); ShowPopup(); return; }
            if (demo) { demo = false; Current = null; }
            string key = CacheKeys.For(text, Store.Settings);
            if (!force && Current != null && Current.CacheKey == key && (Current.Completed || translating)) {
                RestoreCurrentPresentation(false);
                return;
            }
            if (!force) {
                var cached = Store.Find(key);
                if (cached != null) { OpenRecord(cached); return; }
            }
            if (BlockedByRecovery()) return;
            CancelRequests();
            // A root conversation starts here: whatever return relation the reading page
            // held is gone, because the replaced session is no longer reachable from the
            // new conversation. Retry and internal children keep their anchor — unless the
            // retry/regenerate lands on the root itself, which then stays the root.
            if (intent == SessionIntent.Root) { previousSession = null; UpdateBackEntry(); }
            else if (intent == SessionIntent.Inherit && previousSession != null && ReferenceEquals(Current, previousSession.Record)) {
                // Regenerating or retrying the root replaces the root: the old record is no
                // longer a distinct page to return to, so the new one inherits the root
                // position and every internal anchor of the old record dies with it.
                previousSession = null; UpdateBackEntry();
            }
            Current = TranslationRecord.Create(text, sourceKind, Store.Settings);
            currentHistoryEpoch = Store.HistoryEpoch;
            var record = Current; long version = generation.Next();
            mainCancellation = new CancellationTokenSource(); var cancellation = mainCancellation;
            // A new conversation replaces every older retry entry.
            activeWord = null; activeFollowup = null;
            var operation = new RetryOperation {
                Kind = RequestKind.Translation, RecordId = record.Id,
                Run = () => TranslateAsync(record.Source, record.SourceKind, true, SessionIntent.Inherit)
            };
            activeTranslation = operation;
            translating = true; DisplayRecord(); ShowPopup(); Busy();
            long lastProgress = 0;
            try {
                string result = await Client.CompleteAsync(record.Context.Provider, KeyFor(record.Context.Provider), Prompts.Translation(record), true, false,
                    partial => {
                        long now = DateTime.UtcNow.Ticks;
                        if (now - lastProgress < TimeSpan.TicksPerMillisecond * 40) return;
                        lastProgress = now;
                        Popup.Dispatcher.BeginInvoke(new Action(() => {
                            if (generation.IsCurrent(version) && !record.Completed && translating) { record.Translation = partial; DrawTranslation(""); }
                        }));
                    }, cancellation.Token);
                // The result lands on the record only while this request is still the
                // current one: a cancelled or superseded response must never write over
                // the conversation the user is looking at now.
                if (!generation.IsCurrent(version)) return;
                record.Translation = result;
                record.Completed = true; translating = false;
                DrawTranslation(""); Busy();
                Ui.Visible(Ui.Get<Button>(Popup, "ExpandWordButton"), TextTools.IsWordInput(record.Source));
                await SaveCurrentAsync();
            } catch (OperationCanceledException) { }
            catch (Exception error) {
                Log.Event("translation_failed", error);
                if (generation.IsCurrent(version)) {
                    translating = false; Busy();
                    var bundle = new RetryBundle(new[] { operation });
                    ShowError(error is UserError ? error.Message : "翻译没有完成，请重试。", () => { var ignored = RetryAsync(bundle); });
                }
            } finally {
                if (generation.IsCurrent(version)) { translating = false; Busy(); }
                cancellation.Dispose();
                if (ReferenceEquals(mainCancellation, cancellation)) mainCancellation = null;
                if (ReferenceEquals(activeTranslation, operation)) activeTranslation = null;
            }
        }

        public async Task SelectWordAsync(TextPiece word, bool force)
        {
            if (exiting) return;
            if (Current == null || !Current.Completed || word.Length > 300) return;
            if (word.Start < 0 || word.Start + word.Length > Current.Source.Length || Current.Source.Substring(word.Start, word.Length) != word.Text) return;
            if (selectedWord != null && selectedWord.Key == word.Key && wordBusy && !force) return;
            if (wordCancellation != null) wordCancellation.Cancel();
            long version = wordGeneration.Next();
            selectedWord = word; selectedCard = null; HighlightSource(); DrawTranslation(""); Topic(); ClearError();
            var record = Current;
            // History keeps its language/learning context, while new calls use the current saved API.
            var provider = Json.Copy(Store.Settings.Provider);
            string cardKey = demo ? word.Key : CacheKeys.ForWord(record, word, provider);
            Ui.Get<TextBox>(Popup, "WordTitle").Text = word.Text;
            SetReadingText(Ui.Get<RichTextBox>(Popup, "WordMeaning"), "正在查词…");
            SetReadingText(Ui.Get<RichTextBox>(Popup, "WordMeta"), "");
            Ui.Get<StackPanel>(Popup, "LearningSections").Children.Clear();
            Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), true);
            Ui.Visible(Ui.Get<Button>(Popup, "ExpandWordButton"), false);
            WordCard cached;
            if (!force && record.Cards.TryGetValue(cardKey, out cached)) { selectedCard = cached; wordBusy = false; DrawCard(cached); Busy(); return; }
            if (demo) {
                selectedCard = DemoCard(word.Text); record.Cards[word.Key] = selectedCard; DrawCard(selectedCard); return;
            }
            if (BlockedByRecovery()) return;
            wordCancellation = new CancellationTokenSource(); var cancellation = wordCancellation;
            var operation = new RetryOperation {
                Kind = RequestKind.Word, RecordId = record.Id,
                Run = () => SelectWordAsync(word, true)
            };
            activeWord = operation;
            wordBusy = true; Busy();
            try {
                string payload = await Client.CompleteAsync(provider, KeyFor(provider), Prompts.Word(record, word), false, true, null, cancellation.Token);
                if (Current != record || !wordGeneration.IsCurrent(version)) return;
                selectedCard = WordCard.Parse(payload, word.Text, record.Translation);
                if (record.Cards.Count >= 16) record.Cards.Remove(record.Cards.Keys.First());
                record.Cards[cardKey] = selectedCard;
                wordBusy = false; DrawCard(selectedCard); await SaveCurrentAsync(); Busy();
            } catch (OperationCanceledException) { }
            catch (Exception error) {
                Log.Event("word_card_failed", error);
                if (Current == record && wordGeneration.IsCurrent(version)) {
                    wordBusy = false; Busy(); SetReadingText(Ui.Get<RichTextBox>(Popup, "WordMeaning"), "词卡暂未完成。");
                    var bundle = new RetryBundle(new[] { operation });
                    ShowError(error is UserError ? error.Message : "查词没有完成，请重试。", () => { var ignored = RetryAsync(bundle); });
                }
            } finally {
                if (Current == record && wordGeneration.IsCurrent(version)) { wordBusy = false; Busy(); }
                cancellation.Dispose();
                if (ReferenceEquals(wordCancellation, cancellation)) wordCancellation = null;
                if (ReferenceEquals(activeWord, operation)) activeWord = null;
            }
        }

        private void DrawCard(WordCard card)
        {
            SetReadingText(Ui.Get<RichTextBox>(Popup, "WordMeaning"), card.meaning);
            SetReadingText(Ui.Get<RichTextBox>(Popup, "WordMeta"), string.Join(" · ", new[] {
                card.part_of_speech, string.IsNullOrEmpty(card.lemma) ? "" : "原形 " + card.lemma
            }.Where(x => !string.IsNullOrWhiteSpace(x))));
            var panel = Ui.Get<StackPanel>(Popup, "LearningSections"); panel.Children.Clear();
            foreach (var section in card.sections) {
                panel.Children.Add(NewLearningTitle(section.title));
                var content = NewReadingBox();
                SetReadingText(content, section.content);
                panel.Children.Add(content);
            }
            HighlightSource(); DrawTranslation(card.target_phrase); Topic();
            UpdatePopupTypography();
        }
        private void Topic()
        {
            Ui.Get<TextBlock>(Popup, "TopicLabel").Text = selectedWord == null ? "关于这段原文" : "关于「" + selectedWord.Text + "」";
        }
        private void DrawChat()
        {
            var panel = Ui.Get<StackPanel>(Popup, "ChatMessages"); panel.Children.Clear();
            Ui.Visible(Ui.Get<Border>(Popup, "ChatPanel"), Current != null && Current.Chat.Count > 0);
            if (Current == null) return;
            foreach (var turn in Current.Chat) AddChat(panel, turn.Role == "user" ? "你 · " + turn.Topic : "Leaf", turn.Content, turn.Role == "user");
        }
        private TextBlock AddChat(StackPanel panel, string label, string content, bool user)
        {
            panel.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Ui.Brush("Ink"), Margin = new Thickness(0, 0, 0, 7) });
            var text = new TextBlock { Text = content, FontSize = 14, Foreground = Ui.Brush(user ? "Ink" : "Secondary"), LineHeight = 24, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) };
            panel.Children.Add(text); UpdatePopupTypography(); return text;
        }
        public async Task SendChatAsync()
        {
            if (exiting) return;
            if (Current == null || !Current.Completed || chatBusy) return;
            var input = Ui.Get<TextBox>(Popup, "QuestionInput");
            string question = input.Text.Trim();
            if (question.Length == 0) return;
            if (demo) { ShowError("这是演示内容。配置 API 后可进行真实追问。", ShowSettingsPage); return; }
            if (BlockedByRecovery()) return;
            string topic = selectedWord == null ? "原句" : selectedWord.Text;
            await SendChatForAsync(Current, topic, selectedCard, question);
        }
        // The question, topic and card are fixed here; a retry repeats those, not whatever
        // the input happens to hold later. The saved provider is re-read for each attempt.
        private async Task SendChatForAsync(TranslationRecord record, string topic, WordCard card, string question)
        {
            // This is the real send entry: both the public SendChatAsync and a stored retry
            // closure reach it, so the exit, session-identity and recovery guards live here.
            if (exiting) return;
            if (record == null || chatBusy) return;
            if (!ReferenceEquals(Current, record)) return;
            if (BlockedByRecovery()) return;
            var provider = Json.Copy(Store.Settings.Provider);
            long version = generation.Next();
            chatCancellation = new CancellationTokenSource(); var cancellation = chatCancellation;
            var operation = new RetryOperation {
                Kind = RequestKind.Followup, RecordId = record.Id,
                Run = () => SendChatForAsync(record, topic, card, question)
            };
            activeFollowup = operation;
            chatBusy = true; Busy(); ClearError();
            var panel = Ui.Get<StackPanel>(Popup, "ChatMessages");
            Ui.Visible(Ui.Get<Border>(Popup, "ChatPanel"), true);
            AddChat(panel, "你 · " + topic, question, true);
            var response = AddChat(panel, "Leaf", "正在回答…", false);
            try {
                string result = await Client.CompleteAsync(provider, KeyFor(provider), Prompts.Followup(record, topic, card, question), true, false,
                    partial => Popup.Dispatcher.BeginInvoke(new Action(() => {
                        if (Current == record && generation.IsCurrent(version)) { response.Text = partial; Ui.Get<ScrollViewer>(Popup, "BodyScroll").ScrollToBottom(); }
                    })), cancellation.Token);
                if (Current != record || !generation.IsCurrent(version)) return;
                record.Chat.Add(new ChatTurn { Role = "user", Content = question, Topic = topic });
                record.Chat.Add(new ChatTurn { Role = "assistant", Content = result, Topic = topic });
                while (record.Chat.Count > 24) record.Chat.RemoveRange(0, 2);
                var input = Ui.Get<TextBox>(Popup, "QuestionInput");
                if (input.Text.Trim() == question) input.Clear();
                record.Draft = input.Text;
                response.Text = result; chatBusy = false; DrawChat(); await SaveCurrentAsync(); Busy();
                Ui.Get<ScrollViewer>(Popup, "BodyScroll").ScrollToBottom();
            } catch (OperationCanceledException) { }
            catch (Exception error) {
                Log.Event("followup_failed", error);
                if (Current == record && generation.IsCurrent(version)) {
                    chatBusy = false; DrawChat(); Busy();
                    var bundle = new RetryBundle(new[] { operation });
                    ShowError(error is UserError ? error.Message : "追问没有完成，请重试。", () => { var ignored = RetryAsync(bundle); });
                }
            } finally {
                if (Current == record && generation.IsCurrent(version)) { chatBusy = false; Busy(); }
                cancellation.Dispose();
                if (ReferenceEquals(chatCancellation, cancellation)) chatCancellation = null;
                if (ReferenceEquals(activeFollowup, operation)) activeFollowup = null;
            }
        }

        public void PopulateDemo()
        {
            demo = true;
            Current = TranslationRecord.Create("His combat prowess gives him an edge.", "剪贴板", Store.Settings);
            currentHistoryEpoch = Store.HistoryEpoch;
            Current.Translation = "他出色的战斗本领让他占据优势。"; Current.Completed = true;
            var word = TextTools.Pieces(Current.Source).First(p => p.Text == "prowess");
            Current.Cards[word.Key] = DemoCard("prowess");
            DisplayRecord(); selectedWord = word; selectedCard = Current.Cards[word.Key];
            Ui.Get<TextBox>(Popup, "WordTitle").Text = word.Text;
            Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), true); DrawCard(selectedCard);
        }
        private static WordCard DemoCard(string word)
        {
            return new WordCard {
                word = word, lemma = word, part_of_speech = "名词", meaning = "高超的本领；非凡的技艺",
                target_phrase = "本领", sections = new List<LearningSection> {
                    new LearningSection { title = "与 power 有什么区别？", content = "prowess 强调某个领域的高超能力。\npower 更广，可以指力量、权力或影响力。" },
                    new LearningSection { title = "放回这句话", content = "combat prowess 指战斗本领；an edge 在这里是「优势」。" }
                }
            };
        }

        // ---- Previous-session return -------------------------------------------------

        public bool CanReturnToPreviousSession
        {
            get { return previousSession != null && Current != null && !ReferenceEquals(Current, previousSession.Record); }
        }
        private void UpdateBackEntry() { Ui.Visible(Ui.Get<Button>(Popup, "BackToPreviousButton"), CanReturnToPreviousSession); }

        // DSH bridge: translate a selection made inside Leaf itself. Validation and the
        // recovery barrier run first; on failure the current session stays untouched.
        // The selection is placed relative to the sessions the user already has: the same
        // text as the open one reuses it, the root's own text comes back through the
        // return entry, and anything else becomes a child anchored to that root.
        public async Task TranslateInternalSelectionAsync(string text)
        {
            if (exiting) return;
            try { text = TextTools.ValidateInput(text); }
            catch (UserError error) { ShowError(error.Message, null); ShowPopup(); return; }
            if (BlockedByRecovery()) return;
            // The open session IS this selection: bringing it back costs no request and no
            // history entry, and an uncommitted draft that differs from the source survives.
            if (!demo && Current != null && Current.Source == text) {
                var editor = Ui.Get<Grid>(Popup, "SourceEditor");
                bool editorOpen = editor.Visibility == Visibility.Visible;
                string draft = editorOpen ? Ui.Get<TextBox>(Popup, "SourceInput").Text : null;
                RestoreCurrentPresentation(false);
                if (editorOpen && draft != Current.Source) RestoreSourceEditor(draft);
                return;
            }
            // The root itself is one return away; no new child is built over it.
            if (previousSession != null && !ReferenceEquals(Current, previousSession.Record) &&
                previousSession.Record.Source == text) {
                ReturnToPreviousSession();
                return;
            }
            // The first child keeps the session it replaces; a deeper one keeps the same
            // snapshot, so returning always goes back to the root A, never to an inner B.
            SavePreviousSessionSnapshot();
            await TranslateAsync(text, "选中文字", true, SessionIntent.InternalChild);
        }
        // Reopens the source editor with a kept draft, exactly as the return entry does.
        private void RestoreSourceEditor(string draft)
        {
            BeginSourceEdit(false);
            var input = Ui.Get<TextBox>(Popup, "SourceInput");
            fillingSource = true; input.Text = draft ?? ""; fillingSource = false;
            Ui.Visible(Ui.Get<TextBox>(Popup, "SourcePlaceholder"), input.Text.Length == 0);
            Ui.Visible(Ui.Get<Button>(Popup, "TranslateButton"), !string.IsNullOrWhiteSpace(input.Text));
        }
        // Drops the return relation: a new root (an external capture, a manual submit, an
        // opened history record) or a cleared current session leaves nothing to go back to.
        internal void ClearPreviousSession()
        {
            previousSession = null;
            UpdateBackEntry();
        }
        // One in-memory layer: the session being replaced is kept so the light back entry
        // can restore it without another API call. Nothing is persisted. A child never
        // re-saves - A→B→C stays anchored to A - while leaving the root itself again,
        // after returning to it, refreshes the kept state.
        private void SavePreviousSessionSnapshot()
        {
            if (previousSession != null && !ReferenceEquals(Current, previousSession.Record)) return;
            if (Current == null) { previousSession = null; UpdateBackEntry(); return; }
            var editor = Ui.Get<Grid>(Popup, "SourceEditor");
            bool editorOpen = editor.Visibility == Visibility.Visible;
            var sourceScroll = InnerScroll(Ui.Get<TextBox>(Popup, "SourceText"));
            previousSession = new PreviousSessionSnapshot {
                Record = Current,
                HistoryEpoch = currentHistoryEpoch,
                Word = selectedWord,
                Card = selectedCard,
                SourceEditorOpen = editorOpen,
                SourceDraft = editorOpen ? Ui.Get<TextBox>(Popup, "SourceInput").Text : null,
                InputPanelOpen = Ui.Get<Border>(Popup, "InputPanel").Visibility == Visibility.Visible,
                SourceScroll = sourceScroll != null ? sourceScroll.VerticalOffset : 0,
                BodyScrollOffset = Ui.Get<ScrollViewer>(Popup, "BodyScroll").VerticalOffset,
                ErrorVisible = Ui.Get<Border>(Popup, "ErrorPanel").Visibility == Visibility.Visible,
                ErrorText = Ui.Get<TextBlock>(Popup, "ErrorText").Text,
                Retry = retry
            };
            UpdateBackEntry();
        }
        // Restores the kept session without any request: completed or partial results come
        // back as they were, and an unfinished one keeps its original retry entry.
        public void ReturnToPreviousSession()
        {
            var snapshot = previousSession;
            if (snapshot == null || exiting || ReferenceEquals(Current, snapshot.Record)) return;
            CancelRequests();
            Current = snapshot.Record;
            // The restored session keeps the epoch it was captured under, so a clear or a
            // history disable during the sub-session still blocks its late save.
            currentHistoryEpoch = snapshot.HistoryEpoch;
            DisplayRecord();
            selectedWord = snapshot.Word; selectedCard = snapshot.Card;
            if (selectedCard != null) { Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), true); DrawCard(selectedCard); }
            else if (selectedWord != null) HighlightSource();
            Topic(); Busy();
            if (snapshot.ErrorVisible) ShowError(snapshot.ErrorText, snapshot.Retry);
            if (snapshot.SourceEditorOpen) RestoreSourceEditor(snapshot.SourceDraft);
            if (snapshot.InputPanelOpen) { Ui.Visible(Ui.Get<Border>(Popup, "InputPanel"), true); Topic(); Busy(); }
            RestoreReadingScroll(snapshot.BodyScrollOffset, snapshot.SourceScroll);
            ShowPopup();
        }
        internal static ScrollViewer InnerScroll(DependencyObject node)
        {
            for (int depth = 0; node != null && depth < 8; depth++) {
                var viewer = node as ScrollViewer;
                if (viewer != null) return viewer;
                int count = VisualTreeHelper.GetChildrenCount(node);
                if (count == 0) return null;
                node = VisualTreeHelper.GetChild(node, 0);
            }
            return null;
        }
        private void RestoreReadingScroll(double bodyOffset, double sourceOffset)
        {
            if (bodyOffset <= 0 && sourceOffset <= 0) return;
            Popup.Dispatcher.BeginInvoke(new Action(() => {
                var body = Ui.Get<ScrollViewer>(Popup, "BodyScroll");
                body.UpdateLayout();
                body.ScrollToVerticalOffset(Math.Min(bodyOffset, Math.Max(0, body.ScrollableHeight)));
                if (sourceOffset > 0) {
                    var sourceScroll = InnerScroll(Ui.Get<TextBox>(Popup, "SourceText"));
                    if (sourceScroll != null) {
                        sourceScroll.UpdateLayout();
                        sourceScroll.ScrollToVerticalOffset(Math.Min(sourceOffset, Math.Max(0, sourceScroll.ScrollableHeight)));
                    }
                }
            }), DispatcherPriority.Loaded);
        }
    }

}
