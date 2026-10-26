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
    public sealed class ChatTurn
    {
        public string Role { get; set; }
        public string Content { get; set; }
        public string Topic { get; set; }
    }

    public sealed class TranslationRecord
    {
        public string Id { get; set; }
        public string CacheKey { get; set; }
        public string Source { get; set; }
        public string SourceKind { get; set; }
        public string Translation { get; set; }
        public long UpdatedUtcTicks { get; set; }
        public Settings Context { get; set; }
        public Dictionary<string, WordCard> Cards { get; set; }
        public List<ChatTurn> Chat { get; set; }
        public string Draft { get; set; }
        public bool Completed { get; set; }
        public string Preview { get { return Source == null ? "" : Source.Replace("\r", " ").Replace("\n", " "); } }
        public string TimeLabel { get { return new DateTime(UpdatedUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("MM/dd HH:mm"); } }
        public static TranslationRecord Create(string text, string sourceKind, Settings context)
        {
            return new TranslationRecord {
                Id = Guid.NewGuid().ToString("N"), CacheKey = CacheKeys.For(text, context),
                Source = text, SourceKind = sourceKind, Translation = "", Context = Json.Copy(context),
                UpdatedUtcTicks = DateTime.UtcNow.Ticks, Cards = new Dictionary<string, WordCard>(),
                Chat = new List<ChatTurn>(), Draft = ""
            };
        }
        // Repairs one loaded record in place. Returns false for records that cannot be
        // recovered individually; the caller keeps the rest and quarantines the original.
        public bool TryNormalizeLoaded()
        {
            if (!Completed || string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Source) || Context == null ||
                UpdatedUtcTicks <= 0 || UpdatedUtcTicks > DateTime.MaxValue.Ticks)
                return false;
            try { Context.NormalizeLoaded(); }
            catch { return false; }
            Cards = Cards ?? new Dictionary<string, WordCard>();
            Chat = Chat ?? new List<ChatTurn>();
            Draft = Draft ?? "";
            SourceKind = SourceKind ?? "";
            Translation = Translation ?? "";
            var cards = new Dictionary<string, WordCard>();
            foreach (var pair in Cards) {
                var card = pair.Value;
                if (card == null || string.IsNullOrWhiteSpace(card.meaning)) continue;
                card.sections = (card.sections ?? new List<LearningSection>()).Where(s => s != null).ToList();
                foreach (var section in card.sections) {
                    section.title = section.title ?? ""; section.content = section.content ?? "";
                }
                cards[pair.Key] = card;
            }
            Cards = cards;
            var chat = new List<ChatTurn>(); ChatTurn pending = null;
            foreach (var turn in Chat) {
                if (turn == null || (turn.Role != "user" && turn.Role != "assistant")) continue;
                if (turn.Role == "user") { pending = turn; continue; }
                if (pending != null) { chat.Add(pending); chat.Add(turn); pending = null; }
            }
            Chat = chat;
            if (string.IsNullOrWhiteSpace(CacheKey)) CacheKey = CacheKeys.For(Source, Context);
            return true;
        }
    }
}
