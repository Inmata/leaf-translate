using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Leaf
{
    public static class LanguageNames
    {
        // One source line per entry, keyed by the canonical Chinese name Leaf uses as a target.
        // The aliases cover the common model outputs for the languages the target drop-down
        // offers, including their endonyms and usual Chinese/English names.
        private static readonly Dictionary<string, string[]> Languages = new Dictionary<string, string[]>(StringComparer.Ordinal) {
            { "中文", new[] { "中文", "汉语", "漢語", "普通话", "普通話", "国语", "國語", "华语", "華語", "简体中文", "簡體中文", "繁体中文", "繁體中文", "正体中文", "正體中文", "中國語", "中国语", "台灣中文", "台湾中文", "臺灣中文", "香港中文", "澳門中文", "澳门中文", "chinese", "zh", "zh-cn", "zh-tw", "zh-hans", "zh-hant", "zh-sg", "zh-hk", "zh-mo", "cmn", "mandarin", "putonghua", "guoyu" } },
            { "英语", new[] { "英语", "英文", "英語", "english", "englisch", "anglais", "en", "en-us", "en-gb", "en-au", "en-ca", "eng" } },
            { "日语", new[] { "日语", "日文", "日本語", "japanese", "ja", "jp", "jpn", "nihongo" } },
            { "韩语", new[] { "韩语", "韩文", "朝鲜语", "朝鮮語", "한국어", "한국말", "korean", "ko", "kor" } },
            { "法语", new[] { "法语", "法文", "français", "francais", "french", "fr", "fra", "fre" } },
            { "德语", new[] { "德语", "德文", "deutsch", "german", "de", "deu", "ger" } },
            { "西班牙语", new[] { "西班牙语", "西班牙文", "español", "espanol", "spanish", "es", "spa", "castellano" } },
            { "葡萄牙语", new[] { "葡萄牙语", "葡萄牙文", "português", "portugues", "portuguese", "pt", "por" } },
            { "俄语", new[] { "俄语", "俄文", "русский", "russian", "ru", "rus" } },
            { "意大利语", new[] { "意大利语", "意大利文", "italiano", "italian", "it", "ita" } },
            { "阿拉伯语", new[] { "阿拉伯语", "阿拉伯文", "العربية", "arabic", "ar", "ara" } },
            { "印地语", new[] { "印地语", "印地文", "हिन्दी", "हिंदी", "hindi", "hi", "hin" } },
            { "泰语", new[] { "泰语", "泰文", "ไทย", "thai", "th", "tha" } },
            { "越南语", new[] { "越南语", "越南文", "tiếng việt", "vietnamese", "vi", "vie" } }
        };

        // Region and script words that may qualify a language name. A qualifier is only accepted
        // when it sits next to an exact language name ("Simplified Chinese", "simplifiedchinese",
        // "American English"), so an unrelated word such as "Germany" is never folded into "German".
        private static readonly HashSet<string> Qualifiers = new HashSet<string>(StringComparer.Ordinal) {
            "simplified", "traditional", "american", "british", "us", "uk", "gb", "cn", "tw", "hk", "mo",
            "hans", "hant", "prc", "mainland", "taiwan", "hongkong", "macau", "singapore", "malaysia",
            "castilian", "latin", "brazilian", "european", "peninsular", "mexican", "argentinian",
            "colombian", "chilean", "peruvian", "venezuelan", "cuban", "canadian", "australian",
            "newzealand", "irish", "scottish", "welsh", "swiss", "austrian", "modern", "standard",
            "classical", "ancient", "medieval", "old", "middle", "high", "low", "literary", "mandarin",
            "canada", "france", "brazil", "portugal", "spain", "mexico", "argentina", "colombia", "chile",
            "peru", "venezuela", "cuba", "china", "japan", "korea", "india", "thailand", "vietnam",
            "russia", "italy", "germany", "austria", "egypt", "israel", "turkey", "iran", "iraq", "saudi",
            "morocco", "nigeria", "kenya", "poland", "greece", "sweden", "norway", "denmark", "finland",
            "netherlands", "belgium", "philippines", "indonesia",
            // Chinese script and region qualifiers, so 中文（简体）, 繁體中文（台灣）and
            // 简体中文（中国）fold into 中文 instead of becoming a different language. They are
            // accepted only next to an exact name; an unrelated parenthetical stays a stray word.
            "简体", "簡體", "繁体", "繁體", "正体", "正體", "台湾", "台灣", "臺灣", "香港",
            "澳门", "澳門", "中国", "中國", "大陆", "大陸", "新加坡", "马来西亚", "馬來西亞"
        };
        // Characters a name uses to join languages ("中文和英文", "汉语与英语"). Latin joins
        // such as "and"/"/" already split into whole words, so only the letters-only CJK join
        // characters have to be handled explicitly before a written-together answer is trusted.
        private const string Connectors = "和与與及或跟同";
        // Ways a model says "I cannot tell": a whole answer only, so a real name that merely
        // starts with a word or two is never swallowed. Boolean words and null are not languages
        // either even when a model writes them as a JSON string.
        private static readonly string[] UnknownMarkers = {
            "未知", "不确定", "无法判断", "无法确定", "不能确定", "无法识别", "不知道", "无法辨认",
            "unknown", "undetermined", "uncertain", "unsure", "unclear", "indeterminate", "not sure",
            "not a language", "no language", "none of the above", "none", "n/a", "na", "null", "nil",
            "true", "false", "yes", "no",
            // Answers that name no language at all: a mixture, a choice, a catch-all or a request
            // to detect. They are not a language the caller can translate into.
            "mixed", "mixedlanguage", "mixedlanguages", "multiplelanguages", "multilanguage", "multilingual",
            "other", "others", "auto", "automatic", "autodetect", "automaticdetection",
            "混合", "混合语言", "混合語言", "多种语言", "多種語言", "多语言", "多語言", "其他", "其它",
            "自动", "自動", "自动检测", "自動檢測", "自动识别", "自動識別", "不适用", "不適用"
        };
        // Longer refusals are matched anywhere in the answer, so "I cannot determine it" is still
        // a non-answer; the phrases are distinctive enough not to hit a real language name. The
        // Chinese refusals are matched as phrases too: 无法判断语言 / 无法确定原文语言 / 不确定的语言 /
        // 没有语言 say the same thing as "cannot determine" and must not become a language name.
        private static readonly string[] RefusalPhrases = {
            "cannotdetermine", "cantdetermine", "cannottell", "canttell", "cannottell", "donotknow",
            "dontknow", "unabletodetermine", "unabletotell", "notalanguage", "noneoftheabove",
            "insufficientinformation", "notenoughinformation", "notidentifiable",
            "无法判断", "无法确定", "不能确定", "无法识别", "无法辨认", "不确定", "没有语言",
            "無法判斷", "無法確定", "不能確定", "無法識別", "無法辨認", "不確定", "沒有語言"
        };
        private const int NameLimit = 40;

        // Chinese variants are one language here: 简体中文, 繁体中文 and 中文 all answer 中文, so a
        // flip into Chinese is refused as same-language instead of silently choosing a variant.
        // A qualified name folds in the same way: "Simplified Chinese" and "American English"
        // are Chinese and English, never a different language from the configured target.
        public static string Canonical(string name)
        {
            string compact = Normalize(name);
            if (compact.Length == 0) return "";
            bool other;
            string canonical = Classify(name, out other);
            return canonical ?? compact;
        }


        // Exactly one known language is recognized only when every word in the answer is either
        // that name or a qualifier next to it: "Chinese (Traditional)" and "French (Canada)" fold
        // into one language, while "English dictionary" is not a language answer at all. An answer
        // that names several languages is rejected instead of collapsing to whichever came first,
        // so "Chinese and English" never silently becomes Chinese. A single unfamiliar name is
        // kept as a custom language; Cantonese and 廣東話 therefore stay themselves instead of
        // being folded into Chinese.
        private static string Classify(string name, out bool custom)
        {
            custom = false;
            string compact = Normalize(name);
            if (compact.Length == 0) return null;
            string whole = Language(compact);
            if (whole != null) return whole;
            var found = new List<string>();
            bool stray = false;
            foreach (string token in Tokens(name)) {
                string matched = Language(token) ?? Qualified(token);
                if (matched == null) {
                    // A written-together answer can still name several languages: "中文和英文"
                    // and "汉语与英语" join two whole names with a CJK connector that the word
                    // splitter does not break. Only two or more distinct languages are refused;
                    // a single unfamiliar name stays a custom language.
                    var parts = Split(token);
                    if (parts.Count > 1) {
                        var joined = new List<string>();
                        foreach (string part in parts) {
                            string language = Language(part) ?? Qualified(part);
                            if (language != null && !joined.Contains(language)) joined.Add(language);
                        }
                        if (joined.Count > 1) {
                            foreach (string language in joined) if (!found.Contains(language)) found.Add(language);
                            continue;
                        }
                    }
                }
                if (matched != null) { if (!found.Contains(matched)) found.Add(matched); }
                else if (!Qualifiers.Contains(token)) stray = true;
            }
            if (found.Count == 1 && !stray) return found[0];
            if (found.Count == 0) { custom = true; return null; }
            return null;
        }


        // The canonical language for a lower-cased letter/digit run that is exactly one alias.
        private static string Language(string token)
        {
            if (token.Length == 0) return null;
            foreach (var language in Languages)
                foreach (string alias in language.Value)
                    if (alias.Length > 0 && Normalize(alias) == token) return language.Key;
            return null;
        }


        // A single written-together word is accepted only when one side is a known qualifier and
        // the other side is an exact language name ("SimplifiedChinese", "Taiwan" beside 中文).
        private static string Qualified(string token)
        {
            foreach (var language in Languages)
                foreach (string alias in language.Value) {
                    string known = Normalize(alias);
                    if (known.Length < 3 || token.Length <= known.Length) continue;
                    if (token.StartsWith(known, StringComparison.Ordinal) && Qualifiers.Contains(token.Substring(known.Length))) return language.Key;
                    if (token.EndsWith(known, StringComparison.Ordinal) && Qualifiers.Contains(token.Substring(0, token.Length - known.Length))) return language.Key;
                }
            return null;
        }


        // The letter/digit runs a written-together name is built from, split on the CJK join
        // characters. A token with no join character stays a single part and is never refused.
        private static List<string> Split(string token)
        {
            var parts = new List<string>();
            int start = 0;
            for (int i = 0; i < token.Length; i++) {
                if (Connectors.IndexOf(token[i]) < 0) continue;
                if (i > start) parts.Add(token.Substring(start, i - start));
                start = i + 1;
            }
            if (start < token.Length) parts.Add(token.Substring(start));
            return parts;
        }


        internal static bool SameLanguage(string left, string right)
        {
            string a = Canonical(left), b = Canonical(right);
            return a.Length > 0 && a == b;
        }


        // The name kept for a determined answer: a known language becomes its canonical Chinese
        // name, anything else keeps what the model wrote once it is clean and plausibly a name.
        internal static string Display(string value)
        {
            var text = new StringBuilder();
            foreach (char c in value ?? "") if (!char.IsControl(c) && c != '\u200b') text.Append(c);
            string clean = Regex.Replace(text.ToString(), @"\s+", " ").Trim();
            if (clean.Length == 0 || clean.Length > NameLimit || Uncertain(clean)) return "";
            bool custom;
            string canonical = Classify(clean, out custom);
            if (canonical != null) return canonical;
            return custom ? clean : "";
        }


        private static bool Uncertain(string name)
        {
            string text = Normalize(name);
            if (text.Length == 0) return true;
            // A bare number is not a language: Normalize keeps letters and digits, so an answer
            // with no letter at all ("42", "007") is a non-answer rather than a name.
            bool letter = false;
            foreach (char c in text) if (char.IsLetter(c)) { letter = true; break; }
            if (!letter) return true;
            // A whole normalized answer only: "n/a" normalizes to "na", and a prefix test would
            // have swallowed Navajo and Nahuatl as "cannot tell".
            foreach (string marker in UnknownMarkers)
                if (text == Normalize(marker)) return true;
            foreach (string phrase in RefusalPhrases)
                if (text.Contains(phrase)) return true;
            return false;
        }


        // Letters and digits only, lower-cased: "中文（繁体）" and "Chinese (Traditional)" fold
        // into one comparable run, while "?" or "未知。" strip down to something the uncertainty
        // check can judge. Hyphens separate too, so "en-US" and "en US" read the same.
        private static string Normalize(string name)
        {
            var text = new StringBuilder();
            foreach (char c in (name ?? "").ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) text.Append(c);
            return text.ToString();
        }


        // The lower-cased letter/digit runs of a name, so "English (US)" is {"english", "us"}
        // and "zh-cn" is {"zh", "cn"}. Matching whole words avoids the substring accidents that
        // made "Germany" look like German.
        private static IEnumerable<string> Tokens(string name)
        {
            var text = new StringBuilder();
            foreach (char c in (name ?? "").ToLowerInvariant()) {
                if (char.IsLetterOrDigit(c)) text.Append(c);
                else if (text.Length > 0) { yield return text.ToString(); text.Length = 0; }
            }
            if (text.Length > 0) yield return text.ToString();
        }
    }
}
