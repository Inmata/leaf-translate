using System;
using System.Threading;
using System.Threading.Tasks;

namespace Leaf
{
    public sealed class TranslationService
    {
        private readonly LlmClient client;
        public TranslationService(LlmClient client) { this.client = client; }
        public Task<string> TranslateAsync(TranslationRecord record, string key, Action<string> progress, CancellationToken cancellation)
        {
            return client.CompleteAsync(record.Context.Provider, key, TranslationPromptBuilder.Build(record),
                true, false, progress, cancellation);
        }
    }
}
