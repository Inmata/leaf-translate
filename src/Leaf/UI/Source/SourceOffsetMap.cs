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
    public static class SourceOffsetMap
    {
        // The pure map walk, split from the fill so the regression checks can ask for maps
        // over any text pair without a window: both arrays are sized one past their text,
        // every index is written and each sequence is monotone, so a lookup can never read
        // a default 0.
        public static void BuildOffsetMaps(string source, string display, int[] displayToSource, int[] sourceToDisplay)
        {
            int d = 0, s = 0;
            while (d < display.Length && s < source.Length) {
                displayToSource[d] = s; sourceToDisplay[s] = d;
                bool displayPair = display[d] == '\r' && d + 1 < display.Length && display[d + 1] == '\n';
                bool sourcePair = source[s] == '\r' && s + 1 < source.Length && source[s + 1] == '\n';
                if (displayPair && sourcePair) {
                    // The pair interiors keep both maps total and monotone: the box never
                    // queries them, but a gap would read as offset 0.
                    displayToSource[d + 1] = s + 1; sourceToDisplay[s + 1] = d + 1;
                    d += 2; s += 2;
                }
                else if (displayPair) {
                    // The box turned one source "\n" into a "\r\n" pair: the pair's tail
                    // closes that source line, and the next source char sits after the pair.
                    displayToSource[d + 1] = s + 1;
                    d += 2; s += 1;
                }
                else if (sourcePair) {
                    sourceToDisplay[s + 1] = d + 1;
                    displayToSource[d] = s + 1;
                    d += 1; s += 2;
                }
                else { d += 1; s += 1; }
            }
            for (; d <= display.Length; d++) displayToSource[d] = Math.Min(s, source.Length);
            for (; s <= source.Length; s++) sourceToDisplay[s] = Math.Min(d, display.Length);
        }
    }
}
