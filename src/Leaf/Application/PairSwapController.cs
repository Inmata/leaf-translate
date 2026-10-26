using System;
using System.Threading;
using System.Threading.Tasks;

namespace Leaf
{
    internal sealed class PairSwapController
    {
        private readonly AppShell shell;
        private readonly WorkspaceController workspace;
        private readonly PopupPresenter view;
        private LocalStore Store { get { return shell.Store; } }
        private DiagnosticLog Log { get { return shell.Log; } }
        private LanguageDetector detector;
        private SwapState swap;
        private CancellationTokenSource swapCancellation;
        private bool swapBusy;
        private readonly RequestGate swapGeneration = new RequestGate();
        internal bool Busy { get { return swapBusy; } }
        internal PairSwapController(AppShell shell, WorkspaceController workspace, PopupPresenter view) { this.shell = shell; this.workspace = workspace; this.view = view; }
        internal void Clear() { swap = null; }

        // What one of the pair's two views was showing besides its bodies. The bodies, the draft,
        // the word cards and the conversation belong to the record itself and travel with it; the
        // open word and the follow-up panel belong to the view, so each direction comes back as
        // the reader left it.
        private sealed class PairViewState
        {
            public TextPiece Word;
            public WordCard Card;
            public bool InputPanelOpen;
        }

        // The one pair the reading page can hold. Original and Reverse are the same two bodies
        // read in the two directions. The original language is identified once, on demand, and
        // kept here as session metadata - never in a setting of its own, and never offered to the
        // user to fill in - together with the target language that was in force before the flip
        // and the one the flip installed. Reverse is a view and not a record: it is never saved,
        // so the history entry the pair already has keeps its own source and translation and no
        // second entry appears for the same two bodies.
        private sealed class SwapState
        {
            public TranslationRecord Original;
            public TranslationRecord Reverse;
            public string SourceLanguage;
            public string TargetLanguage;
            public string ReverseTarget;
            public PairViewState OriginalView = new PairViewState();
            public PairViewState ReverseView = new PairViewState();
        }
        internal bool CanSwap
        {
            get {
                if (shell.IsExiting || shell.IsDisposed || workspace.IsDemo || swapBusy || shell.CaptureBusy) return false;
                var record = workspace.Current;
                if (record == null || !record.Completed) return false;
                if (string.IsNullOrWhiteSpace(record.Source) || string.IsNullOrWhiteSpace(record.Translation)) return false;
                if (workspace.Translating || workspace.WordBusy || workspace.ChatBusy) return false;
                return !view.SourceEditorOpen;
            }
        }
        internal bool IsSwappedView(TranslationRecord record)
        {
            return swap != null && record != null && ReferenceEquals(record, swap.Reverse);
        }

        internal LanguageDetector Detector()
        {
            if (detector == null) detector = new LanguageDetector(shell.Client);
            return detector;
        }

        // An entry the reading page is leaving makes a running identification irrelevant: its
        // answer must never reach the page, and its target language must never be stored.
        internal void CancelSwap()
        {
            swapGeneration.Next();
            var owner = swapCancellation;
            swapCancellation = null;
            swapBusy = false;
            if (owner == null) return;
            try { owner.Cancel(); } catch (ObjectDisposedException) { }
        }

        // Whether an identification that has just come back still describes the page: while it
        // ran, the session may have been replaced, edited, hidden or closed, and a late answer of
        // any of those must neither move the page nor store its own target language.
        internal bool IsSwapCurrent(long version, TranslationRecord record)
        {
            return !shell.IsExiting && !shell.IsDisposed && swapGeneration.IsCurrent(version) && ReferenceEquals(workspace.Current, record);
        }

        // The reading page's one flip entry, reached through the seam the button raises and again
        // through the retry a refused flip leaves behind. Retrying repeats exactly this
        // identification and this save, never the sentence's translation.
        internal async Task RequestSwapAsync()
        {
            if (!CanSwap) return;
            var record = workspace.Current;
            // The pair is only the page's own: a record this metadata does not describe is a
            // fresh pair, and a session that replaced the one it describes left nothing to reuse.
            var state = swap != null && (ReferenceEquals(record, swap.Original) || ReferenceEquals(record, swap.Reverse)) ? swap : null;
            if (state != null && ReferenceEquals(record, state.Reverse)) { await SwapBackAsync(state, record); return; }
            await SwapForwardAsync(state, record);
        }

        // The first flip identifies the original language once, through the service the user has
        // saved, and installs what was identified as the new target language. Every later flip of
        // the same pair reuses what that first one learned: neither the identification nor the
        // translation runs again.
        private async Task SwapForwardAsync(SwapState known, TranslationRecord record)
        {
            if (workspace.BlockedByRecovery()) return;
            long version = swapGeneration.Next();
            var owner = new CancellationTokenSource();
            swapCancellation = owner;
            swapBusy = true; view.Busy();
            try {
                string target = known != null ? known.ReverseTarget : null;
                string sourceLanguage = known != null ? known.SourceLanguage : null;
                string previousTarget = known != null ? known.TargetLanguage : null;
                if (target == null) {
                    // The original language is asked about the two bodies themselves, on the first
                    // flip only, through the currently saved service and its own key.
                    var provider = Json.Copy(Store.Settings.Provider);
                    var identified = await Detector().IdentifyAsync(record.Source, record.Translation,
                        record.Context.TargetLanguage, provider, shell.KeyFor(provider), owner.Token);
                    if (!IsSwapCurrent(version, record)) return;
                    if (identified == null || !identified.Determined) {
                        RefuseSwap(record, "没有识别出原文语言，已保留当前的目标语言。");
                        return;
                    }
                    if (identified.SameAsTarget) {
                        RefuseSwap(record, "原文已经是当前的目标语言，不需要交换。");
                        return;
                    }
                    target = TargetLanguages.ForPicker(identified.Language);
                    if (target.Length == 0) {
                        RefuseSwap(record, "没有识别出可用的语言，已保留当前的目标语言。");
                        return;
                    }
                    sourceLanguage = identified.Language;
                    previousTarget = record.Context.TargetLanguage;
                }
                // The flip commits only after the new target language is really stored: a save
                // that fails leaves the page and the stored target exactly where they were, and
                // the reading page never claims a target language that was not saved.
                if (!await SaveTargetLanguageAsync(target)) return;
                if (!IsSwapCurrent(version, record)) return;
                var state = known ?? new SwapState {
                    Original = record, SourceLanguage = sourceLanguage,
                    TargetLanguage = previousTarget, ReverseTarget = target,
                    Reverse = BuildReverseView(record, target)
                };
                // The view being left keeps its own open word and follow-up panel; the reverse
                // view starts without either, because the words and the questions asked about
                // the original text are not this text's.
                state.OriginalView = CaptureViewState();
                swap = state;
                ShowPairView(state.Reverse, state.ReverseView);
            } catch (OperationCanceledException) { }
            catch (Exception error) {
                Log.Event("swap_identify_failed", error);
                if (IsSwapCurrent(version, record))
                    RefuseSwap(record, error is UserError ? ((UserError)error).Message : "语言识别没有完成，已保留当前的目标语言。");
            } finally {
                if (ReferenceEquals(swapCancellation, owner)) swapCancellation = null;
                owner.Dispose();
                if (swapGeneration.IsCurrent(version)) { swapBusy = false; view.Busy(); }
            }
        }

        // The way back is the pair's own metadata: the target language the flip replaced is
        // restored and stored, the language is never identified again, and the original view comes
        // back with the word it had open, its conversation and its draft.
        private async Task SwapBackAsync(SwapState state, TranslationRecord record)
        {
            if (workspace.BlockedByRecovery()) return;
            long version = swapGeneration.Next();
            var owner = new CancellationTokenSource();
            swapCancellation = owner;
            swapBusy = true; view.Busy();
            try {
                if (!await SaveTargetLanguageAsync(state.TargetLanguage)) return;
                if (!IsSwapCurrent(version, record)) return;
                state.ReverseView = CaptureViewState();
                ShowPairView(state.Original, state.OriginalView);
            } finally {
                if (ReferenceEquals(swapCancellation, owner)) swapCancellation = null;
                owner.Dispose();
                if (swapGeneration.IsCurrent(version)) { swapBusy = false; view.Busy(); }
            }
        }

        // The new target language goes through the same atomic settings transaction the settings
        // page uses, and the flip waits for it. A commit that fails is reported in one short line
        // with a retry that only repeats this identification and this save.
        internal async Task<bool> SaveTargetLanguageAsync(string target)
        {
            try {
                var next = Json.Copy(Store.Settings);
                next.TargetLanguage = target;
                await shell.ApplySettingsAsync(SettingsUpdate.Preferences(next), null, null);
                return true;
            } catch (Exception error) {
                Log.Event("swap_target_save_failed", error);
                view.ShowError(error is UserError ? ((UserError)error).Message : "目标语言没有保存成功，已保留原来的设置。",
                    () => { var ignored = RequestSwapAsync(); });
                return false;
            }
        }

        // The reverse view of the pair: the two bodies exchanged, and the reading context carried
        // over with the newly identified target, so a lookup or a follow-up asked from here is
        // answered in the language the reader is now reading into. Its own cards and conversation
        // start empty - the original view's belong to the original text - and it is a view, never
        // a record: nothing saves it, so the pair's history entry keeps its own two bodies.
        internal static TranslationRecord BuildReverseView(TranslationRecord record, string target)
        {
            var context = Json.Copy(record.Context);
            context.TargetLanguage = target;
            var reverse = TranslationRecord.Create(record.Translation, record.SourceKind ?? "", context);
            reverse.Translation = record.Source;
            reverse.Completed = true;
            return reverse;
        }

        private PairViewState CaptureViewState()
        {
            return new PairViewState {
                Word = workspace.SelectedWord, Card = workspace.SelectedCard,
                InputPanelOpen = view.FollowupOpen
            };
        }

        // Moving between the pair's two views is the same reading-page entry the return entry
        // uses: the session changes, nothing is requested, and the view brings its own word, card,
        // conversation and draft back with it.
        private void ShowPairView(TranslationRecord record, PairViewState state)
        {
            workspace.Current = record;
            workspace.DisplayRecord();
            workspace.SelectedWord = state.Word; workspace.SelectedCard = state.Card;
            if (workspace.SelectedCard != null) { view.ShowWordPanel(true); view.DrawCard(workspace.SelectedCard); }
            else if (workspace.SelectedWord != null) view.Source.HighlightSource();
            view.Topic();
            if (state.InputPanelOpen) view.ShowFollowup(true);
            view.Busy();
            shell.ShowPopup();
        }

        // A refused or failed flip changes nothing at all: the page, the session and the stored
        // target language stay as they were, and one short line says why.
        internal void RefuseSwap(TranslationRecord record, string message)
        {
            if (shell.IsExiting || shell.IsDisposed || !ReferenceEquals(workspace.Current, record)) return;
            view.ShowError(message, () => { var ignored = RequestSwapAsync(); });
        }
    }
}
