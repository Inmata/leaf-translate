using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Leaf
{
    public static class TranslationPromptBuilder
    {
        public static List<ChatTurn> Build(TranslationRecord record)
        {
            return new List<ChatTurn> {
                new ChatTurn { Role = "system", Content = PromptContext.Rules(record.Context) +
                    (TextTools.IsWordInput(record.Source) ?
                    "输入是词语。只给最合适的释义、词性；如果是变形，给原形。控制在三行以内，不写长篇教学。" :
                    "只返回自然准确的译文，保留必要段落。不加前言、解释、Markdown 或语言识别说明。") },
                new ChatTurn { Role = "user", Content = Json.Write(new { source_text = record.Source }) }
            };
        }
    }
}
