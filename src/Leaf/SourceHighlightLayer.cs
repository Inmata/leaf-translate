using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Leaf
{
    // One background visual under the transparent reading box: the selected piece is
    // drawn as rounded, padded bands BEHIND the glyphs — the box paints its own text
    // over them — and the drawing is clipped to the box's viewport. Scrolling and real
    // layout changes recompute only the piece's own bands; layout ticks that change
    // nothing keep the cached drawing. A top-level type so the XAML reader can bind it.
    public sealed class SourceHighlightLayer : FrameworkElement
    {
        private static readonly Brush Fill = CreateFill();
        private TextBox box;
        private ScrollViewer inner;
        private int start = -1, end = -1;
        private readonly List<Rect> bands = new List<Rect>();
        private double laidOutWidth = -1, laidOutHeight = -1;

        public SourceHighlightLayer() { IsHitTestVisible = false; ClipToBounds = true; }

        private static Brush CreateFill()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0xF4, 0xEA, 0xCD));
            brush.Freeze(); return brush;
        }
        public void Bind(TextBox reading)
        {
            box = reading;
            // The inner scroll host only exists once the template is applied; scrolling is
            // what shifts the bands, so it is re-subscribed whenever the box comes alive.
            box.Loaded += (s, e) => {
                var scroll = AppShell.InnerScroll(box);
                if (scroll == null || ReferenceEquals(scroll, inner)) return;
                if (inner != null) inner.ScrollChanged -= OnScrollChanged;
                inner = scroll;
                inner.ScrollChanged += OnScrollChanged;
            };
            box.SizeChanged += (s, e) => Recompute();
            box.LayoutUpdated += OnBoxLayout;
        }
        public void Show(int newStart, int newEnd)
        {
            start = newStart; end = newEnd;
            Recompute();
        }
        public void Clear()
        {
            start = -1; end = -1;
            if (bands.Count == 0) return;
            bands.Clear();
            InvalidateVisual();
        }
        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;
            Recompute();
        }
        private void OnBoxLayout(object sender, EventArgs e)
        {
            // Layout ticks run for every ancestor pass; only an extent change — or the
            // first layout that gives the box real geometry — may recompute the bands.
            if (box == null || start < 0 || end <= start) return;
            if (bands.Count > 0 && box.ActualWidth == laidOutWidth && box.ActualHeight == laidOutHeight) return;
            Recompute();
        }
        private void Recompute()
        {
            if (box == null) return;
            laidOutWidth = box.ActualWidth; laidOutHeight = box.ActualHeight;
            var next = Bands().ToList();
            if (next.Count == bands.Count && next.Zip(bands, (a, b) => a.Equals(b)).All(equal => equal)) return;
            bands.Clear(); bands.AddRange(next);
            InvalidateVisual();
        }
        // One band per line the piece touches: 左右 3、上下 2,圆角 4,贴合纸面留白。The
        // band ends at the trailing edge of the line's last real glyph, so the final
        // character keeps its own width and a break pair — which has no glyph — never
        // poisons the geometry. Every line query is gated by the real line count: before
        // the first layout the line APIs have nothing to read, and asking for the index
        // past the last line throws instead of returning -1 — the cause of the logged
        // ArgumentOutOfRangeException on every selection reaching the final line. A pass
        // that finds no formatted lines or empty rectangles ends itself; it never throws
        // into the learning request, and it only ever walks the piece's own lines.
        private IEnumerable<Rect> Bands()
        {
            string text = box.Text;
            int from = Math.Max(0, Math.Min(start, text.Length));
            int to = Math.Max(from, Math.Min(end, text.Length));
            if (to <= from || box.ActualWidth <= 0) yield break;
            int lineCount = box.LineCount;
            if (lineCount <= 0) yield break; // no formatted lines yet; wait for a later tick
            int line = box.GetLineIndexFromCharacterIndex(from);
            if (line < 0 || line >= lineCount) yield break;
            while (line < lineCount) {
                int lineStart = box.GetCharacterIndexFromLineIndex(line);
                if (lineStart < 0) yield break; // layout lost its lines mid-way; wait
                // Only the index of an existing line is queried; the last line is detected
                // by the line count rather than by an out-of-range request.
                int next = line + 1 < lineCount ? box.GetCharacterIndexFromLineIndex(line + 1) : -1;
                int lineEnd = next < 0 ? text.Length : next;
                int pieceStart = Math.Max(from, lineStart);
                int pieceEnd = Math.Min(to, lineEnd);
                if (pieceEnd > pieceStart) {
                    var head = box.GetRectFromCharacterIndex(pieceStart);
                    if (head.IsEmpty || head.Height <= 0) yield break; // geometry not ready
                    int tailIndex = pieceEnd - 1;
                    while (tailIndex > pieceStart && tailIndex < text.Length && (text[tailIndex] == '\r' || text[tailIndex] == '\n')) tailIndex--;
                    var tail = box.GetRectFromCharacterIndex(tailIndex, true);
                    if (tail.IsEmpty) yield break;
                    double width = Math.Max(4, tail.Location.X + tail.Width - head.Location.X);
                    yield return new Rect(head.Location.X - 3, head.Location.Y - 2, width + 6, head.Height + 4);
                }
                if (next < 0 || to <= lineEnd) break;
                line++;
            }
        }
        protected override void OnRender(DrawingContext context)
        {
            foreach (var rect in bands) context.DrawRoundedRectangle(Fill, null, rect, 4, 4);
        }
    }
}
