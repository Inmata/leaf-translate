using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Leaf
{
    internal sealed class LearningCoordinator
    {
        private readonly AppShell shell;
        private readonly WorkspaceController workspace;
        private readonly PopupPresenter view;
        private readonly LearningService learningService;
        private LocalStore Store { get { return shell.Store; } }
        private DiagnosticLog Log { get { return shell.Log; } }
        private readonly RequestGate wordGeneration = new RequestGate();
        private CancellationTokenSource wordCancellation, chatCancellation;
        private bool wordBusy, chatBusy;
        private TextPiece selectedWord;
        private WordCard selectedCard;
        private RetryOperation activeWord, activeFollowup;
        internal bool WordBusy { get { return wordBusy; } }
        internal bool ChatBusy { get { return chatBusy; } }
        internal TextPiece SelectedWord { get { return selectedWord; } set { selectedWord = value; } }
        internal WordCard SelectedCard { get { return selectedCard; } set { selectedCard = value; } }
        internal RetryOperation ActiveWord { get { return activeWord; } }
        internal RetryOperation ActiveFollowup { get { return activeFollowup; } }
        internal LearningCoordinator(AppShell shell, WorkspaceController workspace, PopupPresenter view, LearningService service) { this.shell = shell; this.workspace = workspace; this.view = view; learningService = service; }
        internal void Invalidate() { wordGeneration.Next(); }
        internal void Cancel()
        {
            if (wordCancellation != null) wordCancellation.Cancel();
            if (chatCancellation != null) chatCancellation.Cancel();
            wordBusy = chatBusy = false;
        }
        internal void ClearSelection() { selectedWord = null; selectedCard = null; }
        internal void ClearActiveRequests() { activeWord = null; activeFollowup = null; }
        internal void CollapseWord() { wordGeneration.Next(); if (wordCancellation != null) wordCancellation.Cancel(); wordBusy = false; ClearSelection(); }
        internal void DisposeRequests()
        {
            if (wordCancellation != null) { wordCancellation.Dispose(); wordCancellation = null; }
            if (chatCancellation != null) { chatCancellation.Dispose(); chatCancellation = null; }
        }

        public async Task SelectWordAsync(TextPiece word, bool force)
        {
            if (shell.IsExiting) return;
            if (workspace.Current == null || !workspace.Current.Completed || word.Length > 300) return;
            if (word.Start < 0 || word.Start + word.Length > workspace.Current.Source.Length || workspace.Current.Source.Substring(word.Start, word.Length) != word.Text) return;
            if (selectedWord != null && selectedWord.Key == word.Key && wordBusy && !force) return;
            if (wordCancellation != null) wordCancellation.Cancel();
            long version = wordGeneration.Next();
            selectedWord = word; selectedCard = null; view.Source.HighlightSource(); view.DrawTranslation(""); view.Topic(); view.ClearError();
            var record = workspace.Current;
            // History keeps its language/learning context, while new calls use the current saved API.
            var provider = Json.Copy(Store.Settings.Provider);
            string cardKey = workspace.IsDemo ? word.Key : CacheKeys.ForWord(record, word, provider);
            // The loading state is the same document as the finished card: the entry with one
            // short line under it, so nothing is swapped out when the card arrives.
            view.WordCardNotice = "正在查词…";
            view.ShowWordPanel(true);
            view.DrawWordCard();
            view.ShowExpandWordEntry(false);
            WordCard cached;
            if (!force && record.Cards.TryGetValue(cardKey, out cached)) { selectedCard = cached; wordBusy = false; view.DrawCard(cached); view.Busy(); return; }
            if (workspace.IsDemo) {
                selectedCard = WorkspaceController.DemoCard(word.Text); record.Cards[word.Key] = selectedCard; view.DrawCard(selectedCard); return;
            }
            if (workspace.BlockedByRecovery()) return;
            wordCancellation = new CancellationTokenSource(); var cancellation = wordCancellation;
            var operation = new RetryOperation {
                Kind = RequestKind.Word, RecordId = record.Id,
                Run = () => SelectWordAsync(word, true)
            };
            activeWord = operation;
            wordBusy = true; view.Busy();
            try {
                string payload = await learningService.RequestCardAsync(record, word, provider, shell.KeyFor(provider), cancellation.Token);
                if (workspace.Current != record || !wordGeneration.IsCurrent(version)) return;
                selectedCard = WordCardParser.Parse(payload, word.Text, record.Translation);
                if (record.Cards.Count >= 16) record.Cards.Remove(record.Cards.Keys.First());
                record.Cards[cardKey] = selectedCard;
                wordBusy = false; view.DrawCard(selectedCard); await workspace.SaveCurrentAsync(); view.Busy();
            } catch (OperationCanceledException) { }
            catch (Exception error) {
                Log.Event("word_card_failed", error);
                if (workspace.Current == record && wordGeneration.IsCurrent(version)) {
                    wordBusy = false; view.Busy();
                    view.WordCardNotice = "词卡暂未完成。"; view.DrawWordCard();
                    var bundle = new RetryBundle(new[] { operation });
                    view.ShowError(error is UserError ? error.Message : "查词没有完成，请重试。", () => { var ignored = workspace.RetryAsync(bundle); });
                }
            } finally {
                if (workspace.Current == record && wordGeneration.IsCurrent(version)) { wordBusy = false; view.Busy(); }
                cancellation.Dispose();
                if (ReferenceEquals(wordCancellation, cancellation)) wordCancellation = null;
                if (ReferenceEquals(activeWord, operation)) activeWord = null;
            }
        }

        public async Task SendChatAsync()
        {
            if (shell.IsExiting) return;
            if (workspace.Current == null || !workspace.Current.Completed || chatBusy) return;
            string question = view.QuestionDraft.Trim();
            if (question.Length == 0) return;
            if (workspace.IsDemo) { view.ShowError("这是演示内容。配置 API 后可进行真实追问。", shell.ShowSettingsPage); return; }
            if (workspace.BlockedByRecovery()) return;
            string topic = selectedWord == null ? "原句" : selectedWord.Text;
            await SendChatForAsync(workspace.Current, topic, selectedCard, question);
        }

        // The question, topic and card are fixed here; a retry repeats those, not whatever
        // the input happens to hold later. The saved provider is re-read for each attempt.
        internal async Task SendChatForAsync(TranslationRecord record, string topic, WordCard card, string question)
        {
            // This is the real send entry: both the public SendChatAsync and a stored retry
            // closure reach it, so the exit, session-identity and recovery guards live here.
            if (shell.IsExiting) return;
            if (record == null || chatBusy) return;
            if (!ReferenceEquals(workspace.Current, record)) return;
            if (workspace.BlockedByRecovery()) return;
            var provider = Json.Copy(Store.Settings.Provider);
            long version = workspace.generation.Next();
            chatCancellation = new CancellationTokenSource(); var cancellation = chatCancellation;
            var operation = new RetryOperation {
                Kind = RequestKind.Followup, RecordId = record.Id,
                Run = () => SendChatForAsync(record, topic, card, question)
            };
            activeFollowup = operation;
            chatBusy = true; view.Busy(); view.ClearError();
            var response = view.StartFollowup(topic, question);
            try {
                string result = await learningService.FollowupAsync(record, topic, card, question, provider, shell.KeyFor(provider),
                    partial => view.BeginInvoke(new Action(() => {
                        if (workspace.Current == record && workspace.generation.IsCurrent(version)) { response(partial); view.ScrollToBottom(); }
                    })), cancellation.Token);
                if (workspace.Current != record || !workspace.generation.IsCurrent(version)) return;
                record.Chat.Add(new ChatTurn { Role = "user", Content = question, Topic = topic });
                record.Chat.Add(new ChatTurn { Role = "assistant", Content = result, Topic = topic });
                while (record.Chat.Count > 24) record.Chat.RemoveRange(0, 2);
                record.Draft = view.CompleteQuestion(question);
                response(result); chatBusy = false; view.DrawChat(); await workspace.SaveCurrentAsync(); view.Busy();
                view.ScrollToBottom();
            } catch (OperationCanceledException) { }
            catch (Exception error) {
                Log.Event("followup_failed", error);
                if (workspace.Current == record && workspace.generation.IsCurrent(version)) {
                    chatBusy = false; view.DrawChat(); view.Busy();
                    var bundle = new RetryBundle(new[] { operation });
                    view.ShowError(error is UserError ? error.Message : "追问没有完成，请重试。", () => { var ignored = workspace.RetryAsync(bundle); });
                }
            } finally {
                if (workspace.Current == record && workspace.generation.IsCurrent(version)) { chatBusy = false; view.Busy(); }
                cancellation.Dispose();
                if (ReferenceEquals(chatCancellation, cancellation)) chatCancellation = null;
                if (ReferenceEquals(activeFollowup, operation)) activeFollowup = null;
            }
        }
    }
}
