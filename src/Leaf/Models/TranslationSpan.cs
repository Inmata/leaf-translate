using System;
using System.Collections.Generic;
using System.Text;

namespace Leaf
{
    public struct TranslationSpan
    {
        public string Text { get; private set; }
        public bool Bold { get; private set; }
        public TranslationSpan(string text, bool bold) : this() { Text = text; Bold = bold; }
    }
}
