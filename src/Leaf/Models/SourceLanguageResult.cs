using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Leaf
{
    // What the detector found for the original text.
    //
    // Language is a name written in Chinese, ready to be used as Settings.TargetLanguage
    // ("英语", "日语", "中文"). It is empty whenever Determined is false: an undetermined answer
    // is a normal result, not a guess, and callers treat it as a non-success.
    // SameAsTarget says the detected language is the one the text was already translated into,
    // so translating it there again would be the same language talking to itself.
    public sealed class SourceLanguageResult
    {
        public string Language { get; set; }
        public bool Determined { get; set; }
        public bool SameAsTarget { get; set; }
    }
}
