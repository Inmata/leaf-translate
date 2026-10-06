using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Leaf
{
    public enum RequestKind { Translation, Word, Followup }

    // What one piece of work was: retrying repeats exactly this, never the whole sentence.
    // It stores no key; a retry reads the currently saved provider and credential.
    public sealed class RetryOperation
    {
        public RequestKind Kind { get; set; }
        public string RecordId { get; set; }
        public Func<Task> Run { get; set; }
    }

    public sealed class RetryBundle
    {
        private readonly List<RetryOperation> operations;
        public RetryBundle(IEnumerable<RetryOperation> items)
        {
            operations = (items ?? new RetryOperation[0]).Where(x => x != null).ToList();
        }
        public int Count { get { return operations.Count; } }
        // Only work that still belongs to the conversation on screen may run again.
        public async Task RunAsync(string currentId)
        {
            await Task.WhenAll(operations.Where(x => x.RecordId == currentId)
                .Select(x => x.Run()).ToArray());
        }
    }
}
