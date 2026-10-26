using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Leaf
{
    public static class CacheKeys
    {
        public static string ForWord(TranslationRecord record, TextPiece word, ProviderProfile provider)
        {
            var context = Json.Copy(record.Context);
            context.Providers = new List<ProviderProfile> { Json.Copy(provider) };
            context.ProviderId = provider.Id;
            // The word-card prompt version is part of the key: a cached card written by an
            // older teaching style never satisfies a new lookup. The translation key in For
            // carries no such version and stays unchanged.
            return word.Key + ":" + For(Json.Write(new {
                prompt = LearningPromptBuilder.WordPromptVersion,
                record.Source, record.Translation, word.Start, word.Length
            }), context);
        }
        public static string For(string text, Settings settings)
        {
            var state = new {
                text = text.Trim(), provider = settings.Provider.Id,
                endpoint = settings.Provider.BaseUrl.TrimEnd('/'), model = settings.Provider.Model,
                thinking = settings.Provider.ThinkingMode ?? "auto", effort = settings.Provider.ReasoningEffort ?? "low",
                output_limit = settings.Provider.MaxOutputTokens,
                target = settings.TargetLanguage, scene = settings.Scene,
                detail = settings.Scene == "通用" ? "" : settings.SceneDetail,
                gameType = settings.Scene == "游戏" ? settings.GameTextType : "", style = settings.Style,
                learning = settings.Learning.Where(x => x.Enabled).Select(x => new { x.Id, x.Instruction }).ToArray()
            };
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Json.Write(state)))).Replace("-", "");
        }
    }
}
