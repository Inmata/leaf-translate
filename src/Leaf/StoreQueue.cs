using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Leaf
{
    // Serial background queue shared by settings and history writes. Actions never observe the
    // WPF synchronization context; failed operations are tracked until an explicit retry resolves them.
    public sealed class StoreQueue
    {
        private readonly object sync = new object();
        private Task tail = Task.FromResult(0);
        private long sequence;
        private readonly List<KeyValuePair<long, Exception>> failures = new List<KeyValuePair<long, Exception>>();
        public long FailureWatermark { get { lock (sync) return sequence; } }
        public int FailureCount { get { lock (sync) return failures.Count; } }
        public Task Enqueue(Action action)
        {
            if (action == null) throw new ArgumentNullException("action");
            lock (sync) {
                long id = ++sequence;
                tail = tail.ContinueWith(previous => {
                    if (previous != null && previous.IsFaulted) { var observed = previous.Exception; }
                    try { action(); }
                    catch (Exception error) {
                        lock (sync) failures.Add(new KeyValuePair<long, Exception>(id, error));
                        throw;
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                tail.ContinueWith(faulted => { var observed = faulted.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return tail;
            }
        }
        public async Task FlushAsync()
        {
            Task pending; long through;
            lock (sync) { pending = tail; through = sequence; }
            try { await pending.ConfigureAwait(false); } catch { }
            lock (sync) {
                if (failures.Any(f => f.Key <= through))
                    throw new UserError("storage", "本地保存尚未完成，请重试保存。");
            }
        }
        public void AcknowledgeFailures(long through)
        {
            lock (sync) failures.RemoveAll(f => f.Key <= through);
        }
    }
}
