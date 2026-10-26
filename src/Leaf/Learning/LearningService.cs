using System;
using System.Threading;
using System.Threading.Tasks;

namespace Leaf
{
    public sealed class LearningService
    {
        private readonly LlmClient client;
        public LearningService(LlmClient client) { this.client = client; }
        public Task<string> RequestCardAsync(TranslationRecord record, TextPiece word, ProviderProfile provider,
            string key, CancellationToken cancellation)
        {
            return client.CompleteAsync(provider, key, LearningPromptBuilder.Word(record, word), false, true, null, cancellation);
        }
        public Task<string> FollowupAsync(TranslationRecord record, string topic, WordCard card, string question,
            ProviderProfile provider, string key, Action<string> progress, CancellationToken cancellation)
        {
            return client.CompleteAsync(provider, key, LearningPromptBuilder.Followup(record, topic, card, question),
                true, false, progress, cancellation);
        }
    }
}
