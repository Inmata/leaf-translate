using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Leaf
{
    public sealed class WorkspaceController
    {
        private readonly AppShell shell;
        private readonly PopupPresenter view;
        private readonly TranslationService translationService;
        private readonly LearningCoordinator learning;
        private readonly SessionNavigation navigation;
        private readonly PairSwapController pairs;
        private LocalStore Store { get { return shell.Store; } }
        private DiagnosticLog Log { get { return shell.Log; } }
        public TranslationRecord Current { get; internal set; }
        private long currentHistoryEpoch;
        internal long HistoryEpoch { get { return currentHistoryEpoch; } set { currentHistoryEpoch = value; } }
        internal readonly RequestGate generation = new RequestGate();
        private CancellationTokenSource mainCancellation;
        private bool translating, demo;
        private RetryOperation activeTranslation;
        internal bool IsDemo { get { return demo; } }
        internal bool Translating { get { return translating; } }
        internal bool WordBusy { get { return learning.WordBusy; } }
        internal bool ChatBusy { get { return learning.ChatBusy; } }
        internal bool SwapBusy { get { return pairs.Busy; } }
        internal TextPiece SelectedWord { get { return learning.SelectedWord; } set { learning.SelectedWord = value; } }
        internal WordCard SelectedCard { get { return learning.SelectedCard; } set { learning.SelectedCard = value; } }
        internal bool CanReturnToPreviousSession { get { return navigation.CanReturnToPreviousSession; } }
        internal bool CanSwap { get { return pairs.CanSwap; } }
        internal WorkspaceController(AppShell shell, PopupPresenter view, TranslationService translationService, LearningService learningService)
        {
            this.shell = shell; this.view = view; this.translationService = translationService;
            learning = new LearningCoordinator(shell, this, view, learningService);
            navigation = new SessionNavigation(shell, this, view);
            pairs = new PairSwapController(shell, this, view);
        }
        internal void SetDemo(bool value) { demo = value; }
        internal void DisplayRecord() { learning.ClearSelection(); view.RenderRecord(); }
        internal Task SelectWordAsync(TextPiece word, bool force) { return learning.SelectWordAsync(word, force); }
        internal Task SendChatAsync() { return learning.SendChatAsync(); }
        internal void CollapseWord() { learning.CollapseWord(); }
        internal void ClearPreviousSession() { navigation.ClearPreviousSession(); }
        internal void ClearPair() { pairs.Clear(); }
        internal Task TranslateInternalSelectionAsync(string text) { return navigation.TranslateInternalSelectionAsync(text); }
        internal void ReturnToPreviousSession() { navigation.ReturnToPreviousSession(); }
        internal Task RequestSwapAsync() { return pairs.RequestSwapAsync(); }
        internal void CancelSwap() { pairs.CancelSwap(); }
        internal bool IsSwappedView(TranslationRecord record) { return pairs.IsSwappedView(record); }
        internal void DisposeRequests()
        {
            if (mainCancellation != null) { mainCancellation.Dispose(); mainCancellation = null; }
            learning.DisposeRequests();
        }

        public async Task TranslateAsync(string text, string sourceKind, bool force)
        {
            await TranslateAsync(text, sourceKind, force, SessionIntent.Root);
        }

        internal async Task TranslateAsync(string text, string sourceKind, bool force, SessionIntent intent)
        {
            if (shell.IsExiting) return;
            try { text = TextTools.ValidateInput(text); }
            catch (UserError error) { view.ShowError(error.Message, null); shell.ShowPopup(); return; }
            if (demo) { demo = false; Current = null; }
            string key = CacheKeys.For(text, Store.Settings);
            if (!force && Current != null && Current.CacheKey == key && (Current.Completed || translating)) {
                view.Source.RestoreCurrentPresentation(false);
                return;
            }
            if (!force) {
                var cached = Store.Find(key);
                if (cached != null) { shell.OpenRecord(cached); return; }
            }
            if (BlockedByRecovery()) return;
            CancelRequests();
            // A root conversation starts here: whatever return relation the reading page
            // held is gone, because the replaced session is no longer reachable from the
            // new conversation. Retry and internal children keep their anchor — unless the
            // retry/regenerate lands on the root itself, which then stays the root.
            if (intent == SessionIntent.Root) ClearPreviousSession();
            else if (intent == SessionIntent.Inherit && navigation.Previous != null && ReferenceEquals(Current, navigation.Previous.Record)) {
                // Regenerating or retrying the root replaces the root: the old record is no
                // longer a distinct page to return to, so the new one inherits the root
                // position and every internal anchor of the old record dies with it.
                ClearPreviousSession();
            }
            Current = TranslationRecord.Create(text, sourceKind, Store.Settings);
            currentHistoryEpoch = Store.HistoryEpoch;
            var record = Current; long version = generation.Next();
            mainCancellation = new CancellationTokenSource(); var cancellation = mainCancellation;
            // A new conversation replaces every older retry entry.
            learning.ClearActiveRequests();
            var operation = new RetryOperation {
                Kind = RequestKind.Translation, RecordId = record.Id,
                Run = () => TranslateAsync(record.Source, record.SourceKind, true, SessionIntent.Inherit)
            };
            activeTranslation = operation;
            translating = true; DisplayRecord(); shell.ShowPopup(); view.Busy();
            long lastProgress = 0;
            try {
                string result = await translationService.TranslateAsync(record, shell.KeyFor(record.Context.Provider),
                    partial => {
                        long now = DateTime.UtcNow.Ticks;
                        if (now - lastProgress < TimeSpan.TicksPerMillisecond * 40) return;
                        lastProgress = now;
                        view.BeginInvoke(new Action(() => {
                            if (generation.IsCurrent(version) && !record.Completed && translating) { record.Translation = partial; view.DrawTranslation(""); }
                        }));
                    }, cancellation.Token);
                // The result lands on the record only while this request is still the
                // current one: a cancelled or superseded response must never write over
                // the conversation the user is looking at now.
                if (!generation.IsCurrent(version)) return;
                record.Translation = result;
                record.Completed = true; translating = false;
                view.DrawTranslation(""); view.Busy();
                view.UpdateExpandWordEntry();
                await SaveCurrentAsync();
            } catch (OperationCanceledException) { }
            catch (Exception error) {
                Log.Event("translation_failed", error);
                if (generation.IsCurrent(version)) {
                    translating = false; view.Busy();
                    var bundle = new RetryBundle(new[] { operation });
                    view.ShowError(error is UserError ? error.Message : "翻译没有完成，请重试。", () => { var ignored = RetryAsync(bundle); });
                }
            } finally {
                if (generation.IsCurrent(version)) { translating = false; view.Busy(); }
                cancellation.Dispose();
                if (ReferenceEquals(mainCancellation, cancellation)) mainCancellation = null;
                if (ReferenceEquals(activeTranslation, operation)) activeTranslation = null;
            }
        }

        internal bool BlockedByRecovery()
        {
            if (!Store.NeedsExplicitRecovery) return false;
            view.ShowError("上次保存没有完成，请重新应用配置后重试。", shell.ShowSettingsPage);
            shell.ShowPopup();
            return true;
        }

        public void PopulateDemo()
        {
            demo = true;
            Current = TranslationRecord.Create("His combat prowess gives him an edge.", "剪贴板", Store.Settings);
            currentHistoryEpoch = Store.HistoryEpoch;
            Current.Translation = "他出色的战斗本领让他占据优势。"; Current.Completed = true;
            var word = TextTools.Pieces(Current.Source).First(p => p.Text == "prowess");
            Current.Cards[word.Key] = DemoCard("prowess");
            DisplayRecord(); SelectedWord = word; SelectedCard = Current.Cards[word.Key];
            view.ShowWordPanel(true); view.DrawCard(SelectedCard);
        }

        internal static WordCard DemoCard(string word)
        {
            return new WordCard {
                word = word, lemma = word, part_of_speech = "名词", meaning = "高超的本领；非凡的技艺",
                target_phrase = "本领", sections = new List<LearningSection> {
                    new LearningSection { title = "与 power 有什么区别？", content = "prowess 强调某个领域的高超能力。\npower 更广，可以指力量、权力或影响力。" },
                    new LearningSection { title = "放回这句话", content = "combat prowess 指战斗本领；an edge 在这里是「优势」。" }
                }
            };
        }
        internal void OpenRecordNow(TranslationRecord record, long epoch)
        {
            // An opened history record is a root session of its own: the return relation
            // the reading page held is replaced by the record being opened.
            CancelRequests(); demo = false; Current = record; currentHistoryEpoch = epoch;
            ClearPreviousSession();
            DisplayRecord(); shell.ShowPopup();
        }

        public void Forget(string id)
        {
            if (Current != null && (id == null || Current.Id == id)) {
                CancelRequests(); Current = null; ClearPreviousSession(); DisplayRecord();
            }
        }

        public async Task SaveCurrentAsync()
        {
            var record = Current;
            // The reverse view of a swapped pair is a view of the record that pair already has:
            // saving it would add a second history entry for the same two bodies and would touch
            // neither of that entry's own bodies.
            if (record == null || demo || IsSwappedView(record)) return;
            try {
                await Store.SaveAsync(record, currentHistoryEpoch).ConfigureAwait(true);
                shell.RefreshHistory();
            } catch (UserError error) {
                Log.Event("history_save_failed", error);
                view.ShowError(error.Message, RetryPersistence);
            }
        }

        internal async void RetryPersistence()
        {
            try {
                await Store.RetryFailedWritesAsync();
                await Store.FlushAsync();
                shell.RefreshHistory();
            } catch (UserError error) {
                Log.Event("history_save_failed", error);
                view.ShowError(error.Message, RetryPersistence);
            }
        }

        internal void CancelRequests()
        {
            generation.Next(); learning.Invalidate();
            // Stopping, a new session and a replaced store are all the same boundary for a flip
            // that is still identifying: it must not land on the page or store its target.
            CancelSwap();
            if (mainCancellation != null) mainCancellation.Cancel();
            learning.Cancel();
            translating = false; view.Busy();
        }

        // Records what was running before cancelling it, so the single retry entry can
        // repeat those operations instead of starting a new translation.
        public void StopForRetry()
        {
            var bundle = new RetryBundle(new[] { activeTranslation, learning.ActiveWord, learning.ActiveFollowup });
            CancelRequests();
            view.DrawChat();
            if (SelectedCard != null) view.DrawCard(SelectedCard);
            else if (SelectedWord != null) {
                SelectedWord = null; view.Source.HighlightSource(); view.Topic();
                view.ShowWordPanel(false);
            }
            view.Busy();
            if (bundle.Count == 0) { view.ShowError("已停止。", null); return; }
            view.ShowError("已停止。尚未完成的结果不会保存。", () => { var ignored = RetryAsync(bundle); });
        }

        internal async Task RetryAsync(RetryBundle bundle)
        {
            var record = Current;
            await bundle.RunAsync(record == null ? "" : record.Id);
        }

        internal async Task SaveCurrentOrThrowAsync()
        {
            var record = Current;
            if (record == null || demo || IsSwappedView(record)) return;
            await Store.SaveAsync(record, currentHistoryEpoch).ConfigureAwait(true);
        }
    }
}
