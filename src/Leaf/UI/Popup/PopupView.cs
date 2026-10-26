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
    internal sealed class PopupView
    {
        private const int MaxWheelDepth = 32;
        private readonly AppShell shell;
        private readonly WorkspaceController workspace;
        private readonly PopupPresenter presenter;
        private Window Popup { get { return shell.Popup; } }
        internal PopupView(AppShell shell, WorkspaceController workspace, PopupPresenter presenter) { this.shell = shell; this.workspace = workspace; this.presenter = presenter; }

        internal void InitializePopup()
        {
            Ui.Click(Popup, "SwapButton", () => { shell.RaiseSwapRequested(); });
            // The reading page's own flip entry. It is one subscriber among any others, so a check
            // can watch the raise without replacing what the button really does.
            Popup.Closing += (s, e) => { if (!shell.IsExiting) { e.Cancel = true; var ignored = shell.LeaveSettingsThenAsync(shell.HidePopup); } };
            Popup.LocationChanged += (s, e) => shell.QueuePlacementSave();
            Popup.SizeChanged += (s, e) => { presenter.UpdatePopupTypography(); shell.QueuePlacementSave(); };
            Ui.Click(Popup, "HideButton", () => { var ignored = shell.LeaveSettingsThenAsync(shell.HidePopup); });
            Ui.Click(Popup, "SettingsButton", shell.ToggleSettingsPage);
            Ui.Click(Popup, "EditSourceButton", () => presenter.Source.BeginSourceEdit(true));
            Ui.Click(Popup, "TranslateButton", async () => await presenter.Source.SubmitSourceAsync());
            var sourceInput = Ui.Get<TextBox>(Popup, "SourceInput");
            sourceInput.TextChanged += (s, e) => {
                Ui.Visible(Ui.Get<TextBox>(Popup, "SourcePlaceholder"), sourceInput.Text.Length == 0);
                Ui.Visible(Ui.Get<Button>(Popup, "TranslateButton"), !string.IsNullOrWhiteSpace(sourceInput.Text));
                if (!presenter.Source.fillingSource) { shell.CancelCapture(); presenter.Source.sourceRevision++; }
            };
            sourceInput.PreviewKeyDown += async (s, e) => {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; await presenter.Source.SubmitSourceAsync(); }
                else if (e.Key == Key.Escape && workspace.Current != null) { e.Handled = true; presenter.Source.ShowSourceReadOnly(); }
            };
            Ui.Get<TextBox>(Popup, "SourceText").PreviewKeyDown += (s, e) => {
                if (e.Key == Key.Back || e.Key == Key.Delete) {
                    e.Handled = true; presenter.Source.BeginSourceEdit(true); sourceInput.Clear();
                }
                else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) {
                    // The keyboard reaches the same explanation the pointer has: Enter
                    // explains the selected range, or the word under the caret when
                    // nothing is selected. Navigation and copy stay untouched.
                    e.Handled = true;
                    var ignored = presenter.Source.ExplainSelectionOrCaretAsync(true);
                }
            };
            Ui.Get<TextBox>(Popup, "SourceText").PreviewTextInput += (s, e) => {
                e.Handled = true; presenter.Source.BeginSourceEdit(true); sourceInput.Text = e.Text; sourceInput.CaretIndex = sourceInput.Text.Length;
            };
            // A click without a selection explains the word under the pointer; a drag that
            // ends with a selection is the user's own selection and is never interpreted.
            // The click is not swallowed: the up event is left unhandled so the box keeps
            // its own mouse handling — capture release, caret placement, its own click
            // interpretation — and the explanation is deferred until that handling has
            // finished, reading the state it leaves behind instead of replacing it.
            int pointerDownOffset = -1;
            var sourceReading = Ui.Get<TextBox>(Popup, "SourceText");
            sourceReading.PreviewMouseLeftButtonDown += (s, e) => {
                pointerDownOffset = sourceReading.GetCharacterIndexFromPoint(e.GetPosition(sourceReading), true);
            };
            sourceReading.PreviewMouseLeftButtonUp += (s, e) => {
                // A double click is the box's own word selection, and a drag that already
                // ended in a range is the user's own selection; neither is interpreted.
                if (e.ClickCount != 1 || sourceReading.SelectionLength > 0) return;
                int offset = sourceReading.GetCharacterIndexFromPoint(e.GetPosition(sourceReading), true);
                if (offset < 0 || offset != pointerDownOffset) return;
                var piece = presenter.Source.WordHitAt(offset);
                if (piece == null) return;
                // The click belongs to the record it was read from: a session replaced
                // before the deferred step runs — a capture, a retry or a return — drops
                // the stale explanation, and a range that grew after the up event is still
                // the user's own selection. The exit, completion and in-flight-request
                // boundaries stay SelectWordAsync's own.
                var clicked = workspace.Current;
                sourceReading.Dispatcher.BeginInvoke(new Action(() => {
                    if (shell.IsExiting || !ReferenceEquals(workspace.Current, clicked)) return;
                    if (sourceReading.SelectionLength > 0) return;
                    var ignored = workspace.SelectWordAsync(piece, false);
                }));
            };
            // The highlight paints in one background layer under the reading box; the
            // layer defers its own geometry until the box has real layout, so a highlight
            // recorded before the window ever renders needs no re-attach pass.
            var highlightLayer = presenter.Source.highlightLayer;
            highlightLayer.Bind(sourceReading);
            Ui.Click(Popup, "PinButton", () => {
                shell.pinned = !shell.pinned;
                Ui.Get<Button>(Popup, "PinButton").ToolTip = shell.pinned ? "取消置顶，窗口外点击会收起" : "置顶并保持显示";
                presenter.UpdateTopBarState();
            });
            Ui.Get<Grid>(Popup, "DragBar").MouseLeftButtonDown += (s, e) => {
                if (!shell.NativeEnabled || (e.OriginalSource is DependencyObject && HasButtonParent((DependencyObject)e.OriginalSource))) return;
                try {
                    Popup.DragMove(); shell.RememberPlacement();
                } catch (UserError error) { presenter.ShowError(error.Message, null); }
                catch (InvalidOperationException) { }
            };
            Ui.Click(Popup, "CancelButton", workspace.StopForRetry);
            Ui.Click(Popup, "RetryButton", () => { var action = presenter.retry; presenter.ClearError(); if (action != null) action(); });
            Ui.Click(Popup, "SetupButton", shell.ShowSettingsPage);
            Ui.Click(Popup, "BackToPreviousButton", workspace.ReturnToPreviousSession);
            Ui.Click(Popup, "AskButton", () => {
                if (workspace.Current == null || !workspace.Current.Completed) return;
                Ui.Visible(Ui.Get<System.Windows.Controls.Border>(Popup, "InputPanel"), true);
                presenter.Topic(); presenter.Busy(); Ui.Get<TextBox>(Popup, "QuestionInput").Focus();
            });
            Ui.Click(Popup, "CloseQuestionButton", () => { Ui.Visible(Ui.Get<Border>(Popup, "InputPanel"), false); presenter.Busy(); });
            Ui.Click(Popup, "SendButton", async () => await workspace.SendChatAsync());
            Ui.Get<TextBox>(Popup, "QuestionInput").KeyDown += async (s, e) => {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) {
                    e.Handled = true; await workspace.SendChatAsync();
                }
            };
            Ui.Get<TextBox>(Popup, "QuestionInput").TextChanged += (s, e) => {
                if (workspace.Current != null) workspace.Current.Draft = Ui.Get<TextBox>(Popup, "QuestionInput").Text;
            };
            Ui.Click(Popup, "ExpandWordButton", async () => {
                if (workspace.Current == null || !workspace.Current.Completed) return;
                var piece = TextTools.Pieces(workspace.Current.Source).FirstOrDefault(p => p.IsWord);
                if (piece != null) await workspace.SelectWordAsync(piece, false);
            });
            Ui.Click(Popup, "BackToSentence", () => {
                workspace.CollapseWord();
                presenter.Learning.wordCardNotice = null;
                presenter.Source.HighlightSource(); presenter.DrawTranslation(""); Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), false);
                // Collapsing returns to the sentence, so the finished word session gets its
                // expansion entry back; a second expansion reuses the card the session already
                // holds, and the translation, session and follow-up draft are untouched.
                presenter.UpdateExpandWordEntry();
                presenter.Topic(); presenter.Busy();
            });
            var contextMenu = new ContextMenu();
            var explain = new MenuItem { Header = "解释选中片段" };
            explain.Click += async (s, e) => { await presenter.Source.ExplainSelectionOrCaretAsync(false); };
            contextMenu.Items.Add(explain);
            var copySource = new MenuItem { Header = "复制选中内容", Command = ApplicationCommands.Copy };
            contextMenu.Items.Add(copySource);
            Ui.Get<TextBox>(Popup, "SourceText").ContextMenu = contextMenu;
            var translationMenu = new ContextMenu();
            translationMenu.Items.Add(new MenuItem { Header = "复制", Command = ApplicationCommands.Copy });
            translationMenu.Items.Add(new MenuItem { Header = "全选", Command = ApplicationCommands.SelectAll });
            var regenerate = new MenuItem { Header = "重新生成" };
            regenerate.Click += async (s, e) => { if (workspace.Current != null) await workspace.TranslateAsync(workspace.Current.Source, workspace.Current.SourceKind, true, SessionIntent.Inherit); };
            translationMenu.Items.Add(regenerate);
            Ui.Get<RichTextBox>(Popup, "TranslationText").ContextMenu = translationMenu;
            foreach (string name in new[] { "SourceText", "TranslationText" }) {
                var reading = Ui.Get<TextBoxBase>(Popup, name);
                reading.Tag = "LeafSelectableText";
                reading.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
                    async (s, e) => { e.Handled = true; await presenter.CopySelectionAsync(PopupPresenter.SelectionTextOf(reading)); },
                    (s, e) => { e.CanExecute = PopupPresenter.SelectionTextOf(reading).Length > 0; e.Handled = true; }));
            }
            // One marked control now carries the whole learning card; the internal selection
            // read takes the user's own range straight from it, with no automation and no
            // simulated copy.
            presenter.WireSelectableReading(Ui.Get<RichTextBox>(Popup, "WordCard"));
            shell.InitializeSettingsPage();
            WireWheelRouting();
            // The reading column is measured on the first layout that really happens: the
            // window's own Loaded event, or the first typography refresh that finds the page
            // already arranged. Whichever comes first wins and the second is a no-op.
            Popup.Loaded += (s, e) => presenter.Typography.AlignReadingColumn();
            presenter.UpdateTopBarState();
            workspace.DisplayRecord();
            presenter.UpdatePopupTypography();
        }

        // One wheel decision for the whole reading page, taken while the event is still
        // tunneling: the region under the pointer spends the notch itself while it can still
        // move, and at its own edge the same notch continues in the page's body scroller
        // instead of being swallowed. Deciding before any scroller has moved keeps one notch
        // from reaching two of them, and the region comes from the pointer's own hit target,
        // so hovering alone is enough - no click and no focus change. Nothing is rebuilt.
        internal void WireWheelRouting()
        {
            var page = Ui.Get<Grid>(Popup, "TranslatePage");
            page.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler((s, e) => {
                var body = Ui.Get<ScrollViewer>(Popup, "BodyScroll");
                var inner = WheelTargetUnder(page.InputHitTest(Mouse.GetPosition(page)) as DependencyObject, body);
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
        internal static ScrollViewer WheelTargetUnder(DependencyObject origin, ScrollViewer body)
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
        internal static bool CanSpendWheel(ScrollViewer viewer, int delta)
        {
            double offset = viewer.VerticalOffset;
            return delta > 0 ? offset > 0.5 : offset < viewer.ScrollableHeight - 0.5;
        }

        internal static DependencyObject VisualParentOf(DependencyObject node)
        {
            try { return VisualTreeHelper.GetParent(node); } catch { return null; }
        }

        internal static bool HasButtonParent(DependencyObject item)
        {
            while (item != null) {
                if (item is Button) return true;
                item = item is Visual ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
            }
            return false;
        }
    }
}
