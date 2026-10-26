using System;

namespace Leaf
{
    internal sealed class SessionViewState
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
}
