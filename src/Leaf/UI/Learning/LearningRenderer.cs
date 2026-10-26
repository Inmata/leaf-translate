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
    public sealed class LearningRenderer
    {
        private readonly Window Popup;
        private readonly WorkspaceController workspace;
        private readonly PopupPresenter presenter;
        internal string wordCardNotice;
        internal double wordCardScale = double.NaN;
        private static FontFamily PaperFontFamily { get { return (FontFamily)Application.Current.Resources["PaperFontFamily"]; } }
        internal LearningRenderer(Window popup, WorkspaceController workspace, PopupPresenter presenter)
        {
            Popup = popup; this.workspace = workspace; this.presenter = presenter;
        }

        // The column the floating collapse action occupies over the card's first line: the
        // button's own width - a 14 dip label with an 8,6 padding on each side - with enough
        // clearance left that the entry can never run under it. Only the first paragraph
        // reserves it; the rest of the document keeps the full reading column.
        private const double WordCardActionColumn = 88;
        // What each paragraph of the learning card is, carried on that paragraph's own Tag: a
        // window resize rewrites the same ladder in place instead of rebuilding the document,
        // and the role is how that pass knows which tier a paragraph belongs to.
        private const string CardEntryRole = "entry";
        private const string CardMeaningRole = "meaning";
        private const string CardMetaRole = "meta";
        private const string CardHeadingRole = "heading";
        private const string CardBodyRole = "body";

        // One place builds the whole learning card as a single read-only document: the entry,
        // the meaning, the part of speech and lemma, and every section heading and body, laid
        // out as paragraphs of one flow. A drag that starts on a heading therefore runs
        // straight on into the content under it, which separate controls could never do.
        // Only a card change or a loading or failed state rebuilds it - never a selection, a
        // scroll, a wheel notch, or a window resize, which rewrites the same ladder in place
        // (ApplyWordCardTypography) and so keeps the reader's selection. The ladder it lays out
        // is the fixed one UpdatePopupTypography documents: a 22 dip entry over a 16 dip
        // meaning, a 14 dip part-of-speech line, and 16 dip headings over 16/26 reading text.
        //
        // Gaps are carried by one side of each pair - the lower block's bottom margin, or the
        // section heading's top margin - so the same spacing comes out whether or not the flow
        // collapses two adjacent paragraph margins. Every paragraph starts at the same left
        // edge: the reading column is shared with the translation above it.
        internal void DrawWordCard()
        {
            double scale = presenter.CurrentTypographyScale();
            var document = new FlowDocument {
                PagePadding = new Thickness(0), FontFamily = PaperFontFamily,
                FontSize = Math.Max(13, 16 * scale)
            };
            // The floating collapse button sits over the card's first line, so that one
            // paragraph - whatever it turns out to be - keeps the button's column free.
            bool columnTaken = false;
            // The entry is the word every other piece of card state already points at, so a
            // restored session cannot show one word's meaning under another word's heading.
            string entry = workspace.SelectedWord == null ? null : workspace.SelectedWord.Text;
            if (!string.IsNullOrEmpty(entry))
                document.Blocks.Add(CardParagraph(entry, CardEntryRole, FontWeights.Bold,
                    Ui.Brush("Ink"), CardMargin(ref columnTaken)));
            string meaning = workspace.SelectedCard != null ? workspace.SelectedCard.meaning : wordCardNotice;
            if (!string.IsNullOrWhiteSpace(meaning))
                document.Blocks.Add(CardParagraph(meaning, CardMeaningRole, FontWeights.Normal,
                    Ui.Brush("Ink"), CardMargin(ref columnTaken)));
            if (workspace.SelectedCard != null) {
                string meta = string.Join(" · ", new[] {
                    workspace.SelectedCard.part_of_speech, string.IsNullOrEmpty(workspace.SelectedCard.lemma) ? "" : "原形 " + workspace.SelectedCard.lemma
                }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (meta.Length > 0)
                    document.Blocks.Add(CardParagraph(meta, CardMetaRole, FontWeights.Normal,
                        Ui.Brush("Muted"), CardMargin(ref columnTaken)));
                // The heading keeps the body size and separates itself by the dark ink, the
                // real bold face that ships in the executable, a 24 dip gap above it and an
                // 8 dip one under it; the entry above stays ordinary weight.
                foreach (var section in workspace.SelectedCard.sections ?? new List<LearningSection>()) {
                    if (!string.IsNullOrWhiteSpace(section.title))
                        document.Blocks.Add(CardParagraph(section.title, CardHeadingRole, FontWeights.Bold,
                            Ui.Brush("Ink"), CardMargin(ref columnTaken)));
                    if (!string.IsNullOrWhiteSpace(section.content))
                        document.Blocks.Add(CardParagraph(section.content, CardBodyRole, FontWeights.Normal,
                            Ui.Brush("Secondary"), CardMargin(ref columnTaken)));
                }
            }
            foreach (var paragraph in document.Blocks.OfType<Paragraph>()) ApplyCardParagraph(paragraph, scale);
            Ui.Get<RichTextBox>(Popup, "WordCard").Document = document;
            wordCardScale = scale;
        }

        // A window resize is not a content change: the card keeps its document - and with it
        // the reader's selection - and only its own ladder of sizes, line heights and gaps is
        // written again at the new scale. A changed card, a loading state or a failed lookup
        // still goes through DrawWordCard and rebuilds the document.
        internal void ApplyWordCardTypography(double scale)
        {
            var document = Ui.Get<RichTextBox>(Popup, "WordCard").Document;
            document.FontSize = Math.Max(13, 16 * scale);
            foreach (var paragraph in document.Blocks.OfType<Paragraph>()) ApplyCardParagraph(paragraph, scale);
            wordCardScale = scale;
        }

        // The card's ladder in one place, read both when the document is built and when a
        // resize rewrites it, so a rebuilt card and a re-scaled one can never drift apart.
        // What a paragraph is travels on its own Tag; the left, top and bottom edges come from
        // here, while the right edge - the collapse button's column on the first paragraph -
        // stays exactly as the builder set it.
        internal static void ApplyCardParagraph(Paragraph paragraph, double scale)
        {
            switch (paragraph.Tag as string) {
                case CardEntryRole:
                    paragraph.FontSize = Math.Max(14, 22 * scale);
                    CardSpacing(paragraph, 0, 16 * scale);
                    break;
                case CardMeaningRole:
                    paragraph.FontSize = Math.Max(13, 16 * scale);
                    paragraph.LineHeight = 26 * scale;
                    CardSpacing(paragraph, 0, 8);
                    break;
                case CardMetaRole:
                    paragraph.FontSize = 14;
                    CardSpacing(paragraph, 0, 0);
                    break;
                case CardHeadingRole:
                    paragraph.FontSize = Math.Max(13, 16 * scale);
                    CardSpacing(paragraph, 24, 8);
                    break;
                default:
                    paragraph.FontSize = Math.Max(12, 16 * scale);
                    paragraph.LineHeight = 26 * scale;
                    CardSpacing(paragraph, 0, 0);
                    break;
            }
        }

        internal static void CardSpacing(Paragraph paragraph, double top, double bottom)
        {
            paragraph.Margin = new Thickness(paragraph.Margin.Left, top, paragraph.Margin.Right, bottom);
        }

        // The card's first paragraph yields its right edge to the floating collapse button and
        // every later one takes the full column back; the other three edges belong to the
        // shared ladder and are written by ApplyCardParagraph.
        internal static Thickness CardMargin(ref bool columnTaken)
        {
            Thickness result = columnTaken ? new Thickness(0) : new Thickness(0, 0, WordCardActionColumn, 0);
            columnTaken = true;
            return result;
        }

        // Plain text into one card paragraph, tagged with what it is so a later resize can
        // rewrite the ladder above without touching the document: "\n" inside a Run paints a
        // line break, so paragraphs, surrogate pairs and combining marks survive without markup.
        internal static Paragraph CardParagraph(string text, string role, FontWeight weight, Brush foreground, Thickness margin)
        {
            var paragraph = new Paragraph { FontWeight = weight, Foreground = foreground, Margin = margin, Tag = role };
            paragraph.Inlines.Add(new Run(text ?? ""));
            return paragraph;
        }
    }
}
