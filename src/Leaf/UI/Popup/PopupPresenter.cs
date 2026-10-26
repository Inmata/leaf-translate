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
    public sealed class PopupPresenter
    {
        private readonly AppShell shell;
        private WorkspaceController workspace { get { return shell.Workspace; } }
        private Window Popup { get { return shell.Popup; } }
        internal SourceInteraction Source { get; private set; }
        internal LearningRenderer Learning { get; private set; }
        internal PopupTypography Typography { get; private set; }
        private TranslationRenderer translation;
        private DispatcherTimer copyFeedback;
        private string displayedSource;
        internal Action retry;
        internal string WordCardNotice { get { return Learning.wordCardNotice; } set { Learning.wordCardNotice = value; } }
        internal void DrawWordCard() { Learning.DrawWordCard(); }
        internal PopupPresenter(AppShell shell) { this.shell = shell; }
        internal void Initialize()
        {
            Source = new SourceInteraction(shell, workspace, this);
            Learning = new LearningRenderer(Popup, workspace, this);
            Typography = new PopupTypography(shell, this);
            translation = new TranslationRenderer(Popup, workspace, this);
            new PopupView(shell, workspace, this).InitializePopup();
        }
        internal double CurrentTypographyScale() { return Typography.CurrentTypographyScale(); }
        public void UpdatePopupTypography() { Typography.UpdatePopupTypography(); }
        internal void DrawTranslation(string target) { translation.DrawTranslation(target); }
        internal void BeginInvoke(Action action) { Popup.Dispatcher.BeginInvoke(action); }
        internal string QuestionDraft { get { return Ui.Get<TextBox>(Popup, "QuestionInput").Text; } set { Ui.Get<TextBox>(Popup, "QuestionInput").Text = value; } }
        internal string SourceDraft { get { return Ui.Get<TextBox>(Popup, "SourceInput").Text; } }
        internal bool SourceEditorOpen { get { return Ui.Get<Grid>(Popup, "SourceEditor").Visibility == Visibility.Visible; } }
        internal bool FollowupOpen { get { return Ui.Get<Border>(Popup, "InputPanel").Visibility == Visibility.Visible; } }
        internal void ShowWordPanel(bool show) { Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), show); }
        internal void ShowFollowup(bool show) { Ui.Visible(Ui.Get<Border>(Popup, "InputPanel"), show); }
        internal void ShowExpandWordEntry(bool show) { Ui.Visible(Ui.Get<Button>(Popup, "ExpandWordButton"), show); }
        internal void ScrollToBottom() { Ui.Get<ScrollViewer>(Popup, "BodyScroll").ScrollToBottom(); }
        internal string CompleteQuestion(string question) { var input = Ui.Get<TextBox>(Popup, "QuestionInput"); if (input.Text.Trim() == question) input.Clear(); return input.Text; }
        internal Action<string> StartFollowup(string topic, string question)
        {
            var panel = Ui.Get<StackPanel>(Popup, "ChatMessages");
            Ui.Visible(Ui.Get<Border>(Popup, "ChatPanel"), true);
            AddChat(panel, "你 · " + topic, question, true);
            var response = AddChat(panel, "Leaf", "正在回答…", false);
            return text => response.Text = text;
        }
        internal SessionViewState CaptureSessionState()
        {
            var sourceScroll = InnerScroll(Ui.Get<TextBox>(Popup, "SourceText"));
            return new SessionViewState {
                Record = workspace.Current, HistoryEpoch = workspace.HistoryEpoch, Word = workspace.SelectedWord, Card = workspace.SelectedCard,
                SourceEditorOpen = SourceEditorOpen, SourceDraft = SourceEditorOpen ? SourceDraft : null, InputPanelOpen = FollowupOpen,
                SourceScroll = sourceScroll != null ? sourceScroll.VerticalOffset : 0, BodyScrollOffset = Ui.Get<ScrollViewer>(Popup, "BodyScroll").VerticalOffset,
                ErrorVisible = Ui.Get<Border>(Popup, "ErrorPanel").Visibility == Visibility.Visible, ErrorText = Ui.Get<TextBlock>(Popup, "ErrorText").Text, Retry = retry
            };
        }
        internal void Stop() { Typography.Stop(); Source.Stop(); if (copyFeedback != null) copyFeedback.Stop(); }

        // The top bar carries one dark chip at a time: the settings entry while its page is
        // open, the pin while the window is really pinned. Leaving either state — the back
        // entry, the outside click, the window close or the pin's own click — puts the plain
        // square back, and nothing here takes focus.
        internal void UpdateTopBarState()
        {
            SetTopBarState(Ui.Get<Button>(Popup, "SettingsButton"), shell.IsSettingsPageOpen);
            SetTopBarState(Ui.Get<Button>(Popup, "PinButton"), shell.pinned);
            SetTopBarState(Ui.Get<Button>(Popup, "HideButton"), false);
        }

        internal void SetTopBarState(Button button, bool active)
        {
            button.Style = (Style)Popup.FindResource(active ? "PaperTopButtonActive" : "PaperTopButton");
        }

        internal async Task CopySelectionAsync(string text)
        {
            try {
                if (shell.NativeEnabled) await ClipboardAccess.CopyTextAsync(text);
                Ui.Get<TextBlock>(Popup, "CopyStatus").Text = "已复制";
                if (copyFeedback == null) {
                    copyFeedback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    copyFeedback.Tick += (s, e) => { copyFeedback.Stop(); Ui.Get<TextBlock>(Popup, "CopyStatus").Text = ""; };
                }
                copyFeedback.Stop(); copyFeedback.Start();
            } catch (Exception error) { shell.Log.Event("copy_failed", error); ShowError(error is UserError ? error.Message : "复制失败，请稍后再试。", null); }
        }

        // RichTextBox answers through its selection range, TextBox through its selected
        // text; both are copy sources on the reading page.
        internal static string SelectionTextOf(TextBoxBase box)
        {
            var rich = box as RichTextBox;
            if (rich != null) return rich.Selection.Text;
            var text = box as TextBox;
            return text != null ? text.SelectedText : "";
        }

        // A read-only learning control: selectable, copyable, and marked so the selection
        // source can discover it. Inputs (password, settings, the follow-up editor) are
        // never marked.
        internal void WireSelectableReading(RichTextBox box)
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

        internal static string SourceKindLabel(string kind)
        {
            // Internal selections are Leaf's own text on screen; clipboard and typed input
            // stay labeled as what they really are.
            return kind == "选中文字" ? "选中文本" : kind;
        }

        // The word-expansion entry belongs to a finished word session and steps aside only
        // while that word's card is open; collapsing the card puts it back, and the card the
        // session already holds is what a second expansion reuses. One place keeps the three
        // states - a new record, a finished translation and a collapsed card - in agreement.
        internal void UpdateExpandWordEntry()
        {
            Ui.Visible(Ui.Get<Button>(Popup, "ExpandWordButton"),
                workspace.Current != null && workspace.Current.Completed && TextTools.IsWordInput(workspace.Current.Source));
        }

        internal void RenderRecord()
        {
            ClearError();
            var source = Ui.Get<TextBox>(Popup, "SourceText");
            Ui.Get<TextBlock>(Popup, "CopyStatus").Text = "";
            Source.fillingSource = true; Ui.Get<TextBox>(Popup, "SourceInput").Text = workspace.Current == null ? "" : workspace.Current.Source; Source.fillingSource = false;
            Source.ShowSourceReadOnly();
            Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), false);
            Ui.Visible(Ui.Get<Border>(Popup, "InputPanel"), false);
            UpdateExpandWordEntry();
            if (workspace.Current == null) {
                if (displayedSource != null) { source.Clear(); displayedSource = null; }
                DrawTranslation("");
                Ui.Get<TextBox>(Popup, "QuestionInput").Clear();
                Ui.Visible(Ui.Get<Border>(Popup, "ChatPanel"), false); Busy(); UpdateBackEntry(); return;
            }
            Ui.Get<TextBlock>(Popup, "SourceBadge").Text = workspace.IsDemo ? "演示内容 · 未调用 API" : SourceKindLabel(workspace.Current.SourceKind);
            if (displayedSource != workspace.Current.Source) {
                // Filling the box and tokenizing it dominates large-record display; an
                // unchanged source reuses the box, its scroll position and any highlight.
                Source.FillSourceReading(workspace.Current.Source);
                displayedSource = workspace.Current.Source;
            } else {
                Source.HighlightSource();
            }
            UpdatePopupTypography();
            DrawTranslation("");
            Ui.Get<TextBox>(Popup, "QuestionInput").Text = workspace.Current.Draft ?? "";
            DrawChat(); Topic(); Busy(); Ui.Get<ScrollViewer>(Popup, "BodyScroll").ScrollToTop();
            UpdateBackEntry();
        }

        internal void DrawCard(WordCard card)
        {
            Learning.wordCardNotice = null;
            Learning.DrawWordCard();
            Source.HighlightSource(); DrawTranslation(card.target_phrase); Topic();
        }

        internal void Topic()
        {
            Ui.Get<TextBlock>(Popup, "TopicLabel").Text = workspace.SelectedWord == null ? "关于这段原文" : "关于「" + workspace.SelectedWord.Text + "」";
        }

        internal void DrawChat()
        {
            var panel = Ui.Get<StackPanel>(Popup, "ChatMessages"); panel.Children.Clear();
            Ui.Visible(Ui.Get<Border>(Popup, "ChatPanel"), workspace.Current != null && workspace.Current.Chat.Count > 0);
            if (workspace.Current == null) return;
            foreach (var turn in workspace.Current.Chat) AddChat(panel, turn.Role == "user" ? "你 · " + turn.Topic : "Leaf", turn.Content, turn.Role == "user");
        }

        internal TextBlock AddChat(StackPanel panel, string label, string content, bool user)
        {
            panel.Children.Add(new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Ui.Brush("Ink"), Margin = new Thickness(0, 0, 0, 7) });
            var text = new TextBlock { Text = content, FontSize = 14, Foreground = Ui.Brush(user ? "Ink" : "Secondary"), LineHeight = 24, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) };
            panel.Children.Add(text); UpdatePopupTypography(); return text;
        }

        public void ShowError(string text, Action retryAction)
        {
            retry = retryAction;
            Ui.Get<TextBlock>(Popup, "ErrorText").Text = text;
            Ui.Visible(Ui.Get<Border>(Popup, "ErrorPanel"), true);
            Ui.Visible(Ui.Get<Button>(Popup, "RetryButton"), retryAction != null);
        }

        internal void ClearError()
        {
            retry = null;
            Ui.Visible(Ui.Get<Border>(Popup, "ErrorPanel"), false);
            // Without an error there is no retry entry either, so a later check can read the
            // button's own state instead of the visibility of the panel hiding it.
            Ui.Visible(Ui.Get<Button>(Popup, "RetryButton"), false);
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

        internal void RestoreReadingScroll(double bodyOffset, double sourceOffset)
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

        internal void UpdateBackEntry() { Ui.Visible(Ui.Get<Button>(Popup, "BackToPreviousButton"), workspace.CanReturnToPreviousSession); }

        // The control keeps its place in the paper and only its usable state changes, so nothing
        // about the reading page's layout moves with it.
        internal void UpdateSwapEntry()
        {
            var button = Ui.Get<Button>(Popup, "SwapButton");
            if (button != null) button.IsEnabled = workspace.CanSwap;
        }
        internal void Busy()
        {
            Ui.Visible(Ui.Get<Grid>(Popup, "BusyPanel"), workspace.Translating || workspace.WordBusy || workspace.ChatBusy || workspace.SwapBusy);
            Ui.Get<TextBlock>(Popup, "BusyLabel").Text = workspace.SwapBusy ? "正在识别原文语言…" : workspace.Translating ? "正在翻译…" : workspace.WordBusy ? "正在查词…" : "正在回答…";
            Ui.Get<Button>(Popup, "SendButton").IsEnabled = workspace.Current != null && workspace.Current.Completed && !workspace.ChatBusy;
            Ui.Get<Button>(Popup, "AskButton").IsEnabled = workspace.Current != null && workspace.Current.Completed;
            Ui.Visible(Ui.Get<Button>(Popup, "AskButton"), workspace.Current != null && Ui.Get<Grid>(Popup, "SourceEditor").Visibility != Visibility.Visible && Ui.Get<Border>(Popup, "InputPanel").Visibility != Visibility.Visible);
            // The flip follows the same one state: it is usable exactly while a finished pair sits
            // on the page with nothing running and nothing being edited.
            UpdateSwapEntry();
        }
    }
}
