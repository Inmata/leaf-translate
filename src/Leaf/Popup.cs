using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Leaf
{
    public sealed partial class AppShell
    {
        private readonly Dictionary<string, Hyperlink> wordLinks = new Dictionary<string, Hyperlink>();
        private bool fillingSource;
        private long sourceRevision;
        private DispatcherTimer copyFeedback;
        // The source paragraph currently built into the reading box; identical sources
        // reuse it instead of rebuilding thousands of piece objects.
        private string displayedSource;
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
            Ui.Get<RichTextBox>(Popup, "SourceText").PreviewKeyDown += (s, e) => {
                if (e.Key == Key.Back || e.Key == Key.Delete) {
                    e.Handled = true; BeginSourceEdit(true); sourceInput.Clear();
                }
            };
            Ui.Get<RichTextBox>(Popup, "SourceText").PreviewTextInput += (s, e) => {
                e.Handled = true; BeginSourceEdit(true); sourceInput.Text = e.Text; sourceInput.CaretIndex = sourceInput.Text.Length;
            };
            Ui.Click(Popup, "PinButton", () => {
                pinned = !pinned;
                var button = Ui.Get<Button>(Popup, "PinButton");
                button.Foreground = pinned ? Ui.Brush("Accent") : Ui.Brush("Secondary");
                button.Background = pinned ? Ui.Brush("Soft") : Brushes.Transparent;
                button.ToolTip = pinned ? "取消置顶，窗口外点击会收起" : "置顶并保持显示";
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
            explain.Click += async (s, e) => {
                var source = Ui.Get<RichTextBox>(Popup, "SourceText");
                string text = source.Selection.Text.Trim();
                if (Current == null || !Current.Completed || text.Length == 0) return;
                string prefix = new TextRange(source.Document.ContentStart, source.Selection.Start).Text.Replace("\r\n", "\n");
                string normalized = Current.Source.Replace("\r\n", "\n");
                int guess = Math.Min(prefix.Length, normalized.Length);
                string selected = text.Replace("\r\n", "\n");
                int found = normalized.IndexOf(selected, Math.Max(0, guess - 1), StringComparison.Ordinal);
                if (found < 0) return;
                int originalOffset = 0, normalizedOffset = 0;
                while (originalOffset < Current.Source.Length && normalizedOffset < found) {
                    if (Current.Source[originalOffset] == '\r' && originalOffset + 1 < Current.Source.Length && Current.Source[originalOffset + 1] == '\n') originalOffset++;
                    originalOffset++; normalizedOffset++;
                }
                int endOffset = originalOffset, selectedOffset = 0;
                while (endOffset < Current.Source.Length && selectedOffset < selected.Length) {
                    if (Current.Source[endOffset] == '\r' && endOffset + 1 < Current.Source.Length && Current.Source[endOffset + 1] == '\n') endOffset++;
                    endOffset++; selectedOffset++;
                }
                await SelectWordAsync(new TextPiece { Start = originalOffset, Length = endOffset - originalOffset, Text = Current.Source.Substring(originalOffset, endOffset - originalOffset), IsWord = true }, false);
            };
            contextMenu.Items.Add(explain);
            var copySource = new MenuItem { Header = "复制选中内容", Command = ApplicationCommands.Copy };
            contextMenu.Items.Add(copySource);
            Ui.Get<RichTextBox>(Popup, "SourceText").ContextMenu = contextMenu;
            var translationMenu = new ContextMenu();
            translationMenu.Items.Add(new MenuItem { Header = "复制", Command = ApplicationCommands.Copy });
            translationMenu.Items.Add(new MenuItem { Header = "全选", Command = ApplicationCommands.SelectAll });
            var regenerate = new MenuItem { Header = "重新生成" };
            regenerate.Click += async (s, e) => { if (Current != null) await TranslateAsync(Current.Source, Current.SourceKind, true); };
            translationMenu.Items.Add(regenerate);
            Ui.Get<RichTextBox>(Popup, "TranslationText").ContextMenu = translationMenu;
            foreach (string name in new[] { "SourceText", "TranslationText" }) {
                var reading = Ui.Get<RichTextBox>(Popup, name);
                reading.Tag = "LeafSelectableText";
                reading.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
                    async (s, e) => { e.Handled = true; await CopySelectionAsync(reading.Selection.Text); },
                    (s, e) => { e.CanExecute = !reading.Selection.IsEmpty; e.Handled = true; }));
            }
            foreach (string name in new[] { "WordMeaning", "WordMeta" })
                WireSelectableReading(Ui.Get<RichTextBox>(Popup, name));
            InitializeSettingsPage();
            DisplayRecord();
            UpdatePopupTypography();
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
        private RichTextBox NewReadingBox()
        {
            var box = new RichTextBox { Style = (Style)Application.Current.Resources["ReadingText"], Foreground = Ui.Brush("Secondary") };
            box.Document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI") };
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
            Ui.Visible(Ui.Get<RichTextBox>(Popup, "SourceText"), false);
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
            Ui.Visible(Ui.Get<RichTextBox>(Popup, "SourceText"), true);
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
            var source = Ui.Get<RichTextBox>(Popup, "SourceText");
            source.FontSize = source.Document.FontSize = 16 * scale;
            source.MaxHeight = 100 * scale;
            foreach (var paragraph in source.Document.Blocks.OfType<Paragraph>()) paragraph.LineHeight = 25 * scale;
            var translation = Ui.Get<RichTextBox>(Popup, "TranslationText");
            translation.FontSize = translation.Document.FontSize = Math.Max(14, 17 * scale);
            foreach (var paragraph in translation.Document.Blocks.OfType<Paragraph>()) paragraph.LineHeight = 27 * scale;
            Ui.Get<TextBox>(Popup, "SourceInput").FontSize = Math.Max(14, 16 * scale);
            SetTypography("WordTitle", Math.Max(14, 18 * scale), 0);
            ApplyReadingTypography(Ui.Get<RichTextBox>(Popup, "WordMeaning"), Math.Max(14, 16 * scale), 25 * scale);
            ApplyReadingTypography(Ui.Get<RichTextBox>(Popup, "WordMeta"), 12, 0);
            SetTypography("ErrorText", Math.Max(12, 13 * scale), 21 * scale);
            // Label/content pairs alternate in these panels; content is a reading box.
            foreach (string name in new[] { "LearningSections", "ChatMessages" }) {
                var children = Ui.Get<StackPanel>(Popup, name).Children;
                for (int i = 0; i < children.Count; i++) {
                    bool label = i % 2 == 0;
                    var text = children[i] as TextBlock;
                    if (text != null) {
                        text.FontSize = label ? 13 : Math.Max(13, 14 * scale);
                        if (!label) text.LineHeight = 24 * scale;
                        text.Margin = new Thickness(0, 0, 0, (label ? 7 : 20) * scale);
                        continue;
                    }
                    var reading = children[i] as RichTextBox;
                    if (reading == null) continue;
                    ApplyReadingTypography(reading, Math.Max(13, 14 * scale), 24 * scale);
                    reading.Margin = new Thickness(0, 0, 0, 20 * scale);
                }
            }
            double compact = (scale - 0.84) / 0.16;
            Ui.Get<Grid>(Popup, "PopupContent").Margin = new Thickness(18 + 8 * compact, 12, 18 + 8 * compact, 16 + 8 * compact);
            Ui.Get<Border>(Popup, "SourcePanel").Padding = new Thickness(0, 8 + 4 * compact, 0, 6 + 6 * compact);
            Ui.Get<ScrollViewer>(Popup, "BodyScroll").Margin = new Thickness(0, 16 + 4 * compact, 0, 0);
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
            var source = Ui.Get<RichTextBox>(Popup, "SourceText");
            Ui.Get<TextBlock>(Popup, "CopyStatus").Text = "";
            fillingSource = true; Ui.Get<TextBox>(Popup, "SourceInput").Text = Current == null ? "" : Current.Source; fillingSource = false;
            ShowSourceReadOnly();
            Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), false);
            Ui.Visible(Ui.Get<Border>(Popup, "InputPanel"), false);
            Ui.Visible(Ui.Get<Button>(Popup, "ExpandWordButton"), Current != null && Current.Completed && TextTools.IsWordInput(Current.Source));
            if (Current == null) {
                if (displayedSource != null) { source.Document.Blocks.Clear(); wordLinks.Clear(); displayedSource = null; }
                DrawTranslation(""); BeginSourceEdit(false);
                Ui.Get<TextBox>(Popup, "QuestionInput").Clear();
                Ui.Visible(Ui.Get<Border>(Popup, "ChatPanel"), false); Busy(); UpdateBackEntry(); return;
            }
            Ui.Get<TextBlock>(Popup, "SourceBadge").Text = demo ? "演示内容 · 未调用 API" : "来自" + SourceKindLabel(Current.SourceKind);
            if (displayedSource != Current.Source) {
                // Building the per-word links dominates large-record display (measured at
                // ~80 ms for a 6000-char record); an unchanged source reuses the built
                // paragraph, links, selection and scroll position as they are.
                source.Document.Blocks.Clear();
                wordLinks.Clear();
                var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 25 };
                foreach (var piece in TextTools.Pieces(Current.Source)) {
                    if (!piece.IsWord) { paragraph.Inlines.Add(new Run(piece.Text)); continue; }
                    var captured = piece;
                    // Reachable and activatable from the keyboard, so Tab + Enter explains a word.
                    var link = new Hyperlink(new Run(piece.Text)) {
                        Focusable = true, ToolTip = "解释这个词", Style = (Style)Application.Current.Resources[typeof(Hyperlink)]
                    };
                    link.Click += async (s, e) => { e.Handled = true; await SelectWordAsync(captured, false); };
                    wordLinks[piece.Key] = link; paragraph.Inlines.Add(link);
                }
                source.Document.Blocks.Add(paragraph);
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

        private void HighlightSource()
        {
            foreach (var item in wordLinks) {
                if (selectedWord != null && item.Key == selectedWord.Key) {
                    item.Value.Background = Ui.Brush("WordHighlight"); item.Value.Foreground = Ui.Brush("Ink");
                } else {
                    item.Value.ClearValue(TextElement.BackgroundProperty); item.Value.ClearValue(TextElement.ForegroundProperty);
                }
            }
        }

        private void DrawTranslation(string target)
        {
            var box = Ui.Get<RichTextBox>(Popup, "TranslationText");
            box.Document.Blocks.Clear();
            var block = new Paragraph { Margin = new Thickness(0), LineHeight = 27 * TypographyScale(Popup.Width, Popup.Height) };
            box.Document.Blocks.Add(block);
            string translation = Current == null ? "" : Current.Translation;
            if (string.IsNullOrWhiteSpace(translation)) { block.Inlines.Add(new Run(Current != null ? "正在翻译…" : "译文会显示在这里。")); return; }
            int index = string.IsNullOrEmpty(target) ? -1 : translation.IndexOf(target, StringComparison.Ordinal);
            if (index < 0) { block.Inlines.Add(new Run(translation)); return; }
            block.Inlines.Add(new Run(translation.Substring(0, index)));
            block.Inlines.Add(new Run(target) { Background = Ui.Brush("WordHighlight") });
            block.Inlines.Add(new Run(translation.Substring(index + target.Length)));
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
            Current = TranslationRecord.Create(text, sourceKind, Store.Settings);
            currentHistoryEpoch = Store.HistoryEpoch;
            var record = Current; long version = generation.Next();
            mainCancellation = new CancellationTokenSource(); var cancellation = mainCancellation;
            // A new conversation replaces every older retry entry.
            activeWord = null; activeFollowup = null;
            var operation = new RetryOperation {
                Kind = RequestKind.Translation, RecordId = record.Id,
                Run = () => TranslateAsync(record.Source, record.SourceKind, true)
            };
            activeTranslation = operation;
            translating = true; DisplayRecord(); ShowPopup(); Busy();
            long lastProgress = 0;
            try {
                record.Translation = await Client.CompleteAsync(record.Context.Provider, KeyFor(record.Context.Provider), Prompts.Translation(record), true, false,
                    partial => {
                        long now = DateTime.UtcNow.Ticks;
                        if (now - lastProgress < TimeSpan.TicksPerMillisecond * 40) return;
                        lastProgress = now;
                        Popup.Dispatcher.BeginInvoke(new Action(() => {
                            if (generation.IsCurrent(version) && !record.Completed && translating) { record.Translation = partial; DrawTranslation(""); }
                        }));
                    }, cancellation.Token);
                if (!generation.IsCurrent(version)) return;
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
            Ui.Get<TextBlock>(Popup, "WordTitle").Text = word.Text;
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
                panel.Children.Add(new TextBlock { Text = section.title, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Ui.Brush("Ink"), Margin = new Thickness(0, 0, 0, 7) });
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
            Ui.Get<TextBlock>(Popup, "WordTitle").Text = word.Text;
            Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), true); DrawCard(selectedCard);
        }
        private static WordCard DemoCard(string word)
        {
            return new WordCard {
                word = word, lemma = word, part_of_speech = "名词", meaning = "高超的本领；非凡的技艺",
                target_phrase = "本领", sections = new List<LearningSection> {
                    new LearningSection { title = "近义词比较", content = "prowess 强调在某个领域展现出的高超能力。\npower 更广，可以指力量、权力或影响力。" },
                    new LearningSection { title = "语境", content = "combat prowess 指战斗本领；an edge 在这里是「优势」，不是物体的边缘。" }
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
        public async Task TranslateInternalSelectionAsync(string text)
        {
            if (exiting) return;
            try { text = TextTools.ValidateInput(text); }
            catch (UserError error) { ShowError(error.Message, null); ShowPopup(); return; }
            if (BlockedByRecovery()) return;
            SavePreviousSessionSnapshot();
            await TranslateAsync(text, "选中文字", true);
        }
        // One in-memory layer: the session being replaced is kept so the light back entry
        // can restore it without another API call. Nothing is persisted.
        private void SavePreviousSessionSnapshot()
        {
            if (Current == null) { previousSession = null; UpdateBackEntry(); return; }
            var editor = Ui.Get<Grid>(Popup, "SourceEditor");
            bool editorOpen = editor.Visibility == Visibility.Visible;
            var sourceScroll = InnerScroll(Ui.Get<RichTextBox>(Popup, "SourceText"));
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
            if (snapshot.SourceEditorOpen) {
                BeginSourceEdit(false);
                var input = Ui.Get<TextBox>(Popup, "SourceInput");
                fillingSource = true; input.Text = snapshot.SourceDraft ?? ""; fillingSource = false;
                Ui.Visible(Ui.Get<TextBox>(Popup, "SourcePlaceholder"), input.Text.Length == 0);
                Ui.Visible(Ui.Get<Button>(Popup, "TranslateButton"), !string.IsNullOrWhiteSpace(input.Text));
            }
            if (snapshot.InputPanelOpen) { Ui.Visible(Ui.Get<Border>(Popup, "InputPanel"), true); Topic(); Busy(); }
            RestoreReadingScroll(snapshot.BodyScrollOffset, snapshot.SourceScroll);
            ShowPopup();
        }
        private static ScrollViewer InnerScroll(DependencyObject node)
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
                    var sourceScroll = InnerScroll(Ui.Get<RichTextBox>(Popup, "SourceText"));
                    if (sourceScroll != null) {
                        sourceScroll.UpdateLayout();
                        sourceScroll.ScrollToVerticalOffset(Math.Min(sourceOffset, Math.Max(0, sourceScroll.ScrollableHeight)));
                    }
                }
            }), DispatcherPriority.Loaded);
        }
    }
}
