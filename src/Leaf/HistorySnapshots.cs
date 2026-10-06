using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Leaf
{
    // Immutable-enough history snapshots: records are deep-copied before background work,
    // and the whole list is encoded exactly once per committed write using UTF-8 byte counts.
    public static class HistorySnapshots
    {
        public const long SoftLimit = 4L * 1024 * 1024;
        public const long HardLimit = 32L * 1024 * 1024;
        private static long encodeCalls;
        public static long EncodeCalls { get { return Interlocked.Read(ref encodeCalls); } }
        public static void ResetEncodeCalls() { Interlocked.Exchange(ref encodeCalls, 0); }

        public sealed class HistoryPayload
        {
            public List<TranslationRecord> Records;
            public string Json;
            public long Utf8Bytes;
            public bool OversizedSingle;
        }

        public static TranslationRecord Copy(TranslationRecord record)
        {
            if (record == null) return null;
            var copy = new TranslationRecord {
                Id = record.Id, CacheKey = record.CacheKey, Source = record.Source, SourceKind = record.SourceKind,
                Translation = record.Translation, UpdatedUtcTicks = record.UpdatedUtcTicks,
                Context = ConversationContext.Snapshot(record.Context), Draft = record.Draft,
                Completed = record.Completed
            };
            copy.Cards = new Dictionary<string, WordCard>();
            if (record.Cards != null) foreach (var pair in record.Cards) copy.Cards[pair.Key] = CopyCard(pair.Value);
            copy.Chat = new List<ChatTurn>();
            if (record.Chat != null) foreach (var turn in record.Chat)
                copy.Chat.Add(turn == null ? null : new ChatTurn { Role = turn.Role, Content = turn.Content, Topic = turn.Topic });
            return copy;
        }
        private static WordCard CopyCard(WordCard card)
        {
            if (card == null) return null;
            return new WordCard {
                word = card.word, lemma = card.lemma, part_of_speech = card.part_of_speech,
                meaning = card.meaning, target_phrase = card.target_phrase,
                sections = (card.sections ?? new List<LearningSection>())
                    .Select(section => section == null ? null :
                        new LearningSection { title = section.title, content = section.content }).ToList()
            };
        }

        public static HistoryPayload Encode(List<TranslationRecord> source, int limit)
        {
            Interlocked.Increment(ref encodeCalls);
            var kept = new List<TranslationRecord>();
            var parts = new List<string>();
            long bytes = 2;
            foreach (var record in source.OrderByDescending(x => x.UpdatedUtcTicks).Take(limit)) {
                string json;
                try { json = Json.Write(record); }
                catch (Exception) { throw new UserError("length", "单条历史记录过大，无法安全保存。请缩小输入后重试。"); }
                long next = bytes + Encoding.UTF8.GetByteCount(json) + (parts.Count == 0 ? 0 : 1);
                if (parts.Count > 0 && next > SoftLimit) break;
                kept.Add(record); parts.Add(json); bytes = next;
            }
            return new HistoryPayload {
                Records = kept,
                Json = "[" + string.Join(",", parts.ToArray()) + "]",
                Utf8Bytes = bytes,
                OversizedSingle = bytes > SoftLimit
            };
        }
    }
}
