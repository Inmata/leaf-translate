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
    internal sealed class TranslationRenderer
    {
        private readonly Window Popup;
        private readonly WorkspaceController workspace;
        private readonly PopupPresenter presenter;
        internal TranslationRenderer(Window popup, WorkspaceController workspace, PopupPresenter presenter) { Popup = popup; this.workspace = workspace; this.presenter = presenter; }

        internal void DrawTranslation(string target)
        {
            var box = Ui.Get<RichTextBox>(Popup, "TranslationText");
            box.Document.Blocks.Clear();
            var block = new Paragraph { Margin = new Thickness(0), LineHeight = 27 * presenter.CurrentTypographyScale() };
            box.Document.Blocks.Add(block);
            string translation = workspace.Current == null ? "" : workspace.Current.Translation;
            if (string.IsNullOrWhiteSpace(translation)) { block.Inlines.Add(new Run(workspace.Current != null ? "正在翻译…" : "译文会显示在这里。")); return; }
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
        internal static void AddTranslationRuns(Paragraph block, IReadOnlyList<TranslationSpan> spans, int highlightStart, int highlightEnd)
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

        internal static void AddEmphasisRun(Paragraph block, string text, bool bold, bool highlight)
        {
            if (text.Length == 0) return;
            var run = new Run(text);
            if (bold) run.FontWeight = FontWeights.Bold;
            if (highlight) run.Background = Ui.Brush("WordHighlight");
            block.Inlines.Add(run);
        }
    }
}
