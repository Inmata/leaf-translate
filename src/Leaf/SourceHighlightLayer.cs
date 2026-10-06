using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Leaf
{
    // One background visual under the transparent reading box: the selected piece is
    // drawn as rounded, padded bands BEHIND the glyphs — the box paints its own text
    // over them — and the drawing is clipped to the box's viewport. Scrolling and real
    // layout changes recompute only the piece's own bands, once per render cycle however
    // many ticks asked, and only after the box's layout has settled; layout ticks that
    // change nothing keep the cached drawing. A hand-dragged window edge is the one state
    // that asks for no geometry at all: the layer is suspended for the drag, paints
    // nothing, and refreshes the range that is current when it ends. A top-level type so
    // the XAML reader can bind it.
    public sealed class SourceHighlightLayer : FrameworkElement
    {
        private static readonly Brush Fill = CreateFill();
        private TextBox box;
        private ScrollViewer inner;
        private int start = -1, end = -1;
        private readonly List<Rect> bands = new List<Rect>();
        private double laidOutWidth = -1, laidOutHeight = -1;
        // A tick that needs new geometry leaves one request behind instead of querying: a
        // window drag fires SizeChanged and LayoutUpdated for every pass, and asking the box
        // for line rectangles each time reformats it. Many ticks therefore collapse into one
        // query per render cycle.
        private bool recomputeQueued;
        // Each queued pass carries the number it was requested under: a pass that a
        // suspension or a cancel has overtaken returns without asking the box anything.
        private int pass;
        // While the window's edge is being dragged the layer draws nothing and asks for
        // nothing. The range is kept, so the highlight comes back from whichever selection
        // is current when the drag ends.
        private bool suspended;
        // The popup is closing for good and this layer will never be looked at again: the
        // ticks the close itself raises - a size change, a last layout pass, a scroll reset -
        // are answered with nothing rather than with a geometry query into a dying box.
        private bool stopped;

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
            box.SizeChanged += (s, e) => RequestRecompute();
            box.LayoutUpdated += OnBoxLayout;
        }
        public void Show(int newStart, int newEnd)
        {
            start = newStart; end = newEnd;
            RequestRecompute();
        }
        public void Clear()
        {
            start = -1; end = -1;
            // The layer's own range is the pending request's protection too: the queued pass
            // re-reads it, so a highlight cleared before that pass runs is never painted back.
            // That holds while the layer is suspended as well: a clear there resets the range,
            // so the drag's end cannot paint the old piece again.
            if (bands.Count == 0) return;
            bands.Clear();
            InvalidateVisual();
        }
        // A hand-dragged window edge pauses the layer: the drawing is hidden — a render-only
        // change, the box's layout is untouched — and every size, layout, scroll and queued
        // pass is refused, because the geometry the box would answer with is the half-arranged
        // one a drag produces. The latest range survives the pause.
        internal void Suspend()
        {
            if (suspended) return;
            suspended = true;
            if (bands.Count == 0) return;
            bands.Clear();
            InvalidateVisual();
        }
        // The drag is over: the range that is current now — the one it started with, or a
        // newer one a word card produced during it — is refreshed once, under the layout the
        // box has settled into. A range a clear removed while suspended is not refreshed.
        internal void Resume()
        {
            if (!suspended) return;
            suspended = false;
            RequestRecompute();
        }
        // The popup is going away while the layer is suspended: the pass a tick had already
        // queued is abandoned instead of painted on a window nobody is looking at, and the
        // range stays for the next layout that needs it.
        internal void CancelPending()
        {
            suspended = false;
            recomputeQueued = false;
            pass++;
        }
        // The popup is closing for good: the layer draws nothing more, drops the pass it had
        // queued, and refuses every later request - including the ones the close's own last
        // layout and scroll ticks raise.
        internal void Stop()
        {
            stopped = true;
            suspended = false;
            recomputeQueued = false;
            pass++;
            if (bands.Count == 0) return;
            bands.Clear();
            InvalidateVisual();
        }
        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;
            RequestRecompute();
        }
        private void OnBoxLayout(object sender, EventArgs e)
        {
            // Layout ticks run for every ancestor pass; only an extent change — or the
            // first layout that gives the box real geometry — asks for new bands. A stopped
            // layer answers none of them, so a closing window's last passes stay silent.
            if (stopped || box == null || start < 0 || end <= start) return;
            if (bands.Count > 0 && box.ActualWidth == laidOutWidth && box.ActualHeight == laidOutHeight) return;
            RequestRecompute();
        }
        private void RequestRecompute()
        {
            // A suspended layer asks for nothing: the drag's own ticks would otherwise queue
            // a pass for every pass of the window's loop.
            if (stopped || suspended || recomputeQueued) return;
            recomputeQueued = true;
            int current = ++pass;
            // Waiting for render priority means the pass runs after the layout that produced
            // the geometry, so a resize reads each settled size once rather than a half-arranged
            // box on every tick.
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => {
                // An abandoned pass - the popup went away - and a suspended one both return
                // without asking the box for anything.
                if (current != pass) return;
                recomputeQueued = false;
                // A clear between the request and this pass owns the state now; the removed
                // highlight is not painted back.
                if (stopped || box == null || start < 0 || end <= start || suspended) return;
                Recompute();
            }));
        }
        private void Recompute()
        {
            if (box == null) return;
            // Only a box whose layout has settled is asked for line rectangles: a query into
            // a half-arranged box is what makes a drag stutter, and Bands would only find the
            // empty geometry it already knows about.
            if (!box.IsArrangeValid || box.ActualWidth <= 0) return;
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
