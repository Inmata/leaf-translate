using System;
using System.Threading.Tasks;

namespace Leaf
{
    internal sealed class SessionNavigation
    {
        private readonly AppShell shell;
        private readonly WorkspaceController workspace;
        private readonly PopupPresenter view;
        private SessionViewState previousSession;
        internal SessionViewState Previous { get { return previousSession; } }
        internal bool CanReturnToPreviousSession { get { return previousSession != null && workspace.Current != null && !ReferenceEquals(workspace.Current, previousSession.Record); } }
        internal SessionNavigation(AppShell shell, WorkspaceController workspace, PopupPresenter view) { this.shell = shell; this.workspace = workspace; this.view = view; }

        // DSH bridge: translate a selection made inside Leaf itself. Validation and the
        // recovery barrier run first; on failure the current session stays untouched.
        // The selection is placed relative to the sessions the user already has: the same
        // text as the open one reuses it, the root's own text comes back through the
        // return entry, and anything else becomes a child anchored to that root.
        public async Task TranslateInternalSelectionAsync(string text)
        {
            if (shell.IsExiting) return;
            try { text = TextTools.ValidateInput(text); }
            catch (UserError error) { view.ShowError(error.Message, null); shell.ShowPopup(); return; }
            if (workspace.BlockedByRecovery()) return;
            // The open session IS this selection: bringing it back costs no request and no
            // history entry, and an uncommitted draft that differs from the source survives.
            if (!workspace.IsDemo && workspace.Current != null && workspace.Current.Source == text) {
                bool editorOpen = view.SourceEditorOpen;
                string draft = editorOpen ? view.SourceDraft : null;
                view.Source.RestoreCurrentPresentation(false);
                if (editorOpen && draft != workspace.Current.Source) view.Source.RestoreSourceEditor(draft);
                return;
            }
            // The root itself is one return away; no new child is built over it.
            if (previousSession != null && !ReferenceEquals(workspace.Current, previousSession.Record) &&
                previousSession.Record.Source == text) {
                ReturnToPreviousSession();
                return;
            }
            // The first child keeps the session it replaces; a deeper one keeps the same
            // snapshot, so returning always goes back to the root A, never to an inner B.
            SavePreviousSessionSnapshot();
            await workspace.TranslateAsync(text, "选中文字", true, SessionIntent.InternalChild);
        }
        // Drops the return relation: a new root (an external capture, a manual submit, an
        // opened history record) or a cleared current session leaves nothing to go back to.
        // The swapped pair goes with it: its two views, the language the flip identified and
        // the target it restored all describe the session that is being replaced.
        internal void ClearPreviousSession()
        {
            previousSession = null;
            workspace.ClearPair();
            view.UpdateBackEntry();
        }
        // One in-memory layer: the session being replaced is kept so the light back entry
        // can restore it without another API call. Nothing is persisted. A child never
        // re-saves - A→B→C stays anchored to A - while leaving the root itself again,
        // after returning to it, refreshes the kept state.
        internal void SavePreviousSessionSnapshot()
        {
            if (previousSession != null && !ReferenceEquals(workspace.Current, previousSession.Record)) return;
            if (workspace.Current == null) { previousSession = null; view.UpdateBackEntry(); return; }
            previousSession = view.CaptureSessionState();
            view.UpdateBackEntry();
        }
        // Restores the kept session without any request: completed or partial results come
        // back as they were, and an unfinished one keeps its original retry entry.
        public void ReturnToPreviousSession()
        {
            var snapshot = previousSession;
            if (snapshot == null || shell.IsExiting || ReferenceEquals(workspace.Current, snapshot.Record)) return;
            workspace.CancelRequests();
            workspace.Current = snapshot.Record;
            // The restored session keeps the epoch it was captured under, so a clear or a
            // history disable during the sub-session still blocks its late save.
            workspace.HistoryEpoch = snapshot.HistoryEpoch;
            workspace.DisplayRecord();
            workspace.SelectedWord = snapshot.Word; workspace.SelectedCard = snapshot.Card;
            if (workspace.SelectedCard != null) { view.ShowWordPanel(true); view.DrawCard(workspace.SelectedCard); }
            else if (workspace.SelectedWord != null) view.Source.HighlightSource();
            view.Topic(); view.Busy();
            if (snapshot.ErrorVisible) view.ShowError(snapshot.ErrorText, snapshot.Retry);
            if (snapshot.SourceEditorOpen) view.Source.RestoreSourceEditor(snapshot.SourceDraft);
            if (snapshot.InputPanelOpen) { view.ShowFollowup(true); view.Topic(); view.Busy(); }
            view.RestoreReadingScroll(snapshot.BodyScrollOffset, snapshot.SourceScroll);
            shell.ShowPopup();
        }
    }
}
