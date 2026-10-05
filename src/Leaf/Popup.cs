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
        private void InitializePopup()
        {
            Popup.Closing += (s, e) => { if (!exiting) { e.Cancel = true; HidePopup(); } };
            Popup.LocationChanged += (s, e) => QueuePlacementSave();
            Popup.SizeChanged += (s, e) => { UpdatePopupTypography(); QueuePlacementSave(); };
            Ui.Click(Popup, "HideButton", HidePopup);
            Ui.Click(Popup, "SettingsButton", OpenSettings);
            Ui.Click(Popup, "EditSourceButton", () => BeginSourceEdit(true));
            Ui.Click(Popup, "TranslateButton", async () => await SubmitSourceAsync());
            var sourceInput = Ui.Get<TextBox>(Popup, "SourceInput");
            sourceInput.TextChanged += (s, e) => {
                Ui.Visible(Ui.Get<TextBlock>(Popup, "SourcePlaceholder"), sourceInput.Text.Length == 0);
                Ui.Get<Button>(Popup, "TranslateButton").IsEnabled = !string.IsNullOrWhiteSpace(sourceInput.Text);
                if (!fillingSource) sourceRevision++;
                Ui.Get<TextBlock>(Popup, "SourceEditHint").Text = Current != null && sourceInput.Text != Current.Source ? "原文已修改 · Enter 翻译" : "Enter 翻译 · Shift+Enter 换行";
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
            Ui.Click(Popup, "CancelButton", () => {
                CancelRequests(); ShowError("已停止。尚未完成的结果不会保存。", () => {
                    if (Current != null) { var task = TranslateAsync(Current.Source, Current.SourceKind, true); }
                });
            });
            Ui.Click(Popup, "RetryButton", () => { var action = retry; ClearError(); if (action != null) action(); });
            Ui.Click(Popup, "SetupButton", OpenSettings);
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
                reading.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
                    async (s, e) => { e.Handled = true; await CopySelectionAsync(reading.Selection.Text); },
                    (s, e) => { e.CanExecute = !reading.Selection.IsEmpty; e.Handled = true; }));
            }
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
        public void BeginSourceEdit(bool focus)
        {
            if (focus) sourceRevision++;
            var input = Ui.Get<TextBox>(Popup, "SourceInput");
            if (Ui.Get<Grid>(Popup, "SourceEditor").Visibility != Visibility.Visible) {
                fillingSource = true; input.Text = Current == null ? "" : Current.Source; fillingSource = false;
            }
            Ui.Visible(Ui.Get<RichTextBox>(Popup, "SourceText"), false);
            Ui.Visible(Ui.Get<Grid>(Popup, "SourceEditor"), true);
            Ui.Visible(Ui.Get<Grid>(Popup, "SourceActions"), true);
            Ui.Visible(Ui.Get<Button>(Popup, "EditSourceButton"), false);
            Ui.Get<TextBlock>(Popup, "TranslationCaption").Text = Current != null ? "上次译文" : "译文";
            Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), false);
            Ui.Visible(Ui.Get<Border>(Popup, "ChatPanel"), false);
            Ui.Visible(Ui.Get<Border>(Popup, "InputPanel"), false);
            Busy();
            Ui.Visible(Ui.Get<TextBlock>(Popup, "SourcePlaceholder"), input.Text.Length == 0);
            Ui.Get<Button>(Popup, "TranslateButton").IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
            Ui.Get<TextBlock>(Popup, "SourceEditHint").Text = Current != null && input.Text != Current.Source ? "原文已修改 · Enter 翻译" : "Enter 翻译 · Shift+Enter 换行";
            if (focus) { if (NativeEnabled) Popup.Activate(); input.Focus(); input.SelectAll(); }
        }
        private void ShowSourceReadOnly()
        {
            Ui.Visible(Ui.Get<RichTextBox>(Popup, "SourceText"), true);
            Ui.Visible(Ui.Get<Grid>(Popup, "SourceEditor"), false);
            Ui.Visible(Ui.Get<Grid>(Popup, "SourceActions"), false);
            Ui.Visible(Ui.Get<Button>(Popup, "EditSourceButton"), true);
            Ui.Get<TextBlock>(Popup, "TranslationCaption").Text = "译文";
            Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), selectedCard != null);
            Ui.Visible(Ui.Get<Border>(Popup, "ChatPanel"), Current != null && Current.Chat.Count > 0);
            Busy();
        }
        public async Task SubmitSourceAsync()
        {
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
            SetTypography("WordMeaning", Math.Max(14, 16 * scale), 25 * scale);
            SetTypography("WordMeta", 12, 0);
            SetTypography("EmptyHint", 12, 0);
            SetTypography("ErrorText", Math.Max(12, 13 * scale), 21 * scale);
            foreach (string name in new[] { "LearningSections", "ChatMessages" }) {
                var children = Ui.Get<StackPanel>(Popup, name).Children.OfType<TextBlock>().ToArray();
                for (int i = 0; i < children.Length; i++) {
                    bool label = i % 2 == 0;
                    children[i].FontSize = label ? 13 : Math.Max(13, 14 * scale);
                    if (!label) children[i].LineHeight = 24 * scale;
                    children[i].Margin = new Thickness(0, 0, 0, (label ? 7 : 20) * scale);
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

        private void DisplayRecord()
        {
            selectedWord = null; selectedCard = null; wordLinks.Clear(); ClearError();
            var source = Ui.Get<RichTextBox>(Popup, "SourceText");
            source.Document.Blocks.Clear();
            Ui.Visible(Ui.Get<TextBlock>(Popup, "EmptyHint"), Current == null);
            Ui.Get<TextBlock>(Popup, "CopyStatus").Text = "";
            fillingSource = true; Ui.Get<TextBox>(Popup, "SourceInput").Text = Current == null ? "" : Current.Source; fillingSource = false;
            ShowSourceReadOnly();
            Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), false);
            Ui.Visible(Ui.Get<Border>(Popup, "InputPanel"), false);
            Ui.Visible(Ui.Get<Button>(Popup, "ExpandWordButton"), Current != null && Current.Completed && TextTools.IsWordInput(Current.Source));
            if (Current == null) {
                DrawTranslation(""); BeginSourceEdit(false);
                Ui.Get<TextBox>(Popup, "QuestionInput").Clear();
                Ui.Visible(Ui.Get<Border>(Popup, "ChatPanel"), false); Busy(); return;
            }
            Ui.Get<TextBlock>(Popup, "SourceBadge").Text = demo ? "演示内容 · 未调用 API" : "来自" + Current.SourceKind;
            var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 25 };
            foreach (var piece in TextTools.Pieces(Current.Source)) {
                if (!piece.IsWord) { paragraph.Inlines.Add(new Run(piece.Text)); continue; }
                var captured = piece;
                var link = new Hyperlink(new Run(piece.Text)) {
                    Focusable = false, ToolTip = "解释这个词", Style = (Style)Application.Current.Resources[typeof(Hyperlink)]
                };
                link.Click += async (s, e) => { e.Handled = true; await SelectWordAsync(captured, false); };
                wordLinks[piece.Key] = link; paragraph.Inlines.Add(link);
            }
            source.Document.Blocks.Add(paragraph);
            UpdatePopupTypography();
            DrawTranslation("");
            Ui.Get<TextBox>(Popup, "QuestionInput").Text = Current.Draft ?? "";
            DrawChat(); Topic(); Busy(); Ui.Get<ScrollViewer>(Popup, "BodyScroll").ScrollToTop();
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
        private void ClearError() { retry = null; Ui.Visible(Ui.Get<Border>(Popup, "ErrorPanel"), false); }
        private string KeyFor(ProviderProfile provider) { return CredentialReader != null ? CredentialReader(provider) : NativeEnabled ? Credentials.Read(provider.Id) : "test-key"; }

        public async Task TranslateAsync(string text, string sourceKind, bool force)
        {
            try { text = TextTools.ValidateInput(text); }
            catch (UserError error) { ShowError(error.Message, null); ShowPopup(); return; }
            if (demo) { demo = false; Current = null; }
            string key = CacheKeys.For(text, Store.Settings);
            if (!force && Current != null && Current.CacheKey == key && (Current.Completed || translating)) { ShowPopup(); return; }
            if (!force) {
                var cached = Store.Find(key);
                if (cached != null) { OpenRecord(cached); return; }
            }
            CancelRequests();
            Current = TranslationRecord.Create(text, sourceKind, Store.Settings);
            var record = Current; long version = generation.Next();
            mainCancellation = new CancellationTokenSource(); var cancellation = mainCancellation;
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
                SaveCurrent();
            } catch (OperationCanceledException) { }
            catch (Exception error) {
                Log.Event("translation_failed", error);
                if (generation.IsCurrent(version)) {
                    translating = false; Busy();
                    ShowError(error is UserError ? error.Message : "翻译没有完成，请重试。", async () => await TranslateAsync(text, sourceKind, true));
                }
            } finally {
                if (generation.IsCurrent(version)) { translating = false; Busy(); }
                cancellation.Dispose();
                if (ReferenceEquals(mainCancellation, cancellation)) mainCancellation = null;
            }
        }

        public async Task SelectWordAsync(TextPiece word, bool force)
        {
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
            Ui.Get<TextBlock>(Popup, "WordMeaning").Text = "正在查词…";
            Ui.Get<TextBlock>(Popup, "WordMeta").Text = "";
            Ui.Get<StackPanel>(Popup, "LearningSections").Children.Clear();
            Ui.Visible(Ui.Get<Border>(Popup, "WordPanel"), true);
            Ui.Visible(Ui.Get<Button>(Popup, "ExpandWordButton"), false);
            WordCard cached;
            if (!force && record.Cards.TryGetValue(cardKey, out cached)) { selectedCard = cached; wordBusy = false; DrawCard(cached); Busy(); return; }
            if (demo) {
                selectedCard = DemoCard(word.Text); record.Cards[word.Key] = selectedCard; DrawCard(selectedCard); return;
            }
            wordCancellation = new CancellationTokenSource(); var cancellation = wordCancellation;
            wordBusy = true; Busy();
            try {
                string payload = await Client.CompleteAsync(provider, KeyFor(provider), Prompts.Word(record, word), false, true, null, cancellation.Token);
                if (Current != record || !wordGeneration.IsCurrent(version)) return;
                selectedCard = WordCard.Parse(payload, word.Text, record.Translation);
                if (record.Cards.Count >= 16) record.Cards.Remove(record.Cards.Keys.First());
                record.Cards[cardKey] = selectedCard;
                wordBusy = false; DrawCard(selectedCard); SaveCurrent(); Busy();
            } catch (OperationCanceledException) { }
            catch (Exception error) {
                Log.Event("word_card_failed", error);
                if (Current == record && wordGeneration.IsCurrent(version)) {
                    wordBusy = false; Busy(); Ui.Get<TextBlock>(Popup, "WordMeaning").Text = "词卡暂未完成。";
                    ShowError(error is UserError ? error.Message : "查词没有完成，请重试。", async () => await SelectWordAsync(word, true));
                }
            } finally {
                if (Current == record && wordGeneration.IsCurrent(version)) { wordBusy = false; Busy(); }
                cancellation.Dispose();
                if (ReferenceEquals(wordCancellation, cancellation)) wordCancellation = null;
            }
        }

        private void DrawCard(WordCard card)
        {
            Ui.Get<TextBlock>(Popup, "WordMeaning").Text = card.meaning;
            Ui.Get<TextBlock>(Popup, "WordMeta").Text = string.Join(" · ", new[] {
                card.part_of_speech, string.IsNullOrEmpty(card.lemma) ? "" : "原形 " + card.lemma
            }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var panel = Ui.Get<StackPanel>(Popup, "LearningSections"); panel.Children.Clear();
            foreach (var section in card.sections) {
                panel.Children.Add(new TextBlock { Text = section.title, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Ui.Brush("Ink"), Margin = new Thickness(0, 0, 0, 7) });
                panel.Children.Add(new TextBlock { Text = section.content, FontSize = 14, Foreground = Ui.Brush("Secondary"), LineHeight = 24, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
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
            if (Current == null || !Current.Completed || chatBusy) return;
            var input = Ui.Get<TextBox>(Popup, "QuestionInput");
            string question = input.Text.Trim();
            if (question.Length == 0) return;
            if (demo) { ShowError("这是演示内容。配置 API 后可进行真实追问。", OpenSettings); return; }
            string topic = selectedWord == null ? "原句" : selectedWord.Text;
            var record = Current; var card = selectedCard;
            var provider = Json.Copy(Store.Settings.Provider);
            long version = generation.Next();
            chatCancellation = new CancellationTokenSource(); var cancellation = chatCancellation;
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
                if (input.Text.Trim() == question) input.Clear();
                record.Draft = input.Text;
                response.Text = result; chatBusy = false; DrawChat(); SaveCurrent(); Busy();
                Ui.Get<ScrollViewer>(Popup, "BodyScroll").ScrollToBottom();
            } catch (OperationCanceledException) { }
            catch (Exception error) {
                Log.Event("followup_failed", error);
                if (Current == record && generation.IsCurrent(version)) {
                    chatBusy = false; DrawChat(); Busy();
                    ShowError(error is UserError ? error.Message : "追问没有完成，请重试。", async () => await SendChatAsync());
                }
            } finally {
                if (Current == record && generation.IsCurrent(version)) { chatBusy = false; Busy(); }
                cancellation.Dispose();
                if (ReferenceEquals(chatCancellation, cancellation)) chatCancellation = null;
            }
        }

        public void PopulateDemo()
        {
            demo = true;
            Current = TranslationRecord.Create("His combat prowess gives him an edge.", "剪贴板", Store.Settings);
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
    }
}
