using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Leaf
{
    // Opt-in, isolated benchmark. No user settings, credentials, clipboard or network.
    public static class Performance
    {
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters
        {
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }
        [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr handle, out IoCounters value);
        [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint type);
        [StructLayout(LayoutKind.Sequential)] private struct MemoryCounters
        {
            public uint Size, PageFaults;
            public UIntPtr PeakWorking, Working, PeakPaged, Paged, PeakNonPaged, NonPaged, Pagefile, PeakPagefile, Private;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ThreadEntry
        {
            public uint Size, Usage, Id, Owner; public int Priority, Delta; public uint Flags;
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("psapi.dll")] private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters counters, uint size);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern bool GetProcessHandleCount(IntPtr process, out uint count);
        [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint process);
        [DllImport("kernel32.dll")] private static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry entry);
        [DllImport("kernel32.dll")] private static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry entry);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        private static MemoryCounters Memory()
        {
            var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf(typeof(MemoryCounters)) };
            if (!GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size)) throw new InvalidOperationException("Memory counters unavailable.");
            return counters;
        }
        private static long CpuTicks()
        {
            long created, exited, kernel, user;
            if (!GetProcessTimes(GetCurrentProcess(), out created, out exited, out kernel, out user)) throw new InvalidOperationException("CPU counters unavailable.");
            return kernel + user;
        }
        private static int Threads()
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(4, 0); if (snapshot == new IntPtr(-1)) return -1;
            try {
                int count = 0; uint pid = GetCurrentProcessId(); var entry = new ThreadEntry { Size = (uint)Marshal.SizeOf(typeof(ThreadEntry)) };
                if (Thread32First(snapshot, ref entry)) do { if (entry.Owner == pid) count++; } while (Thread32Next(snapshot, ref entry));
                return count;
            } finally { CloseHandle(snapshot); }
        }
        public static int Run(string root)
        {
            var startup = Stopwatch.StartNew();
            string folder = Path.Combine(Path.GetFullPath(root), "run-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            Ui.InitializeTheme();
            int exit = 0;
            var task = Scenarios(folder, startup);
            task.ContinueWith(t => app.Dispatcher.BeginInvoke(new Action(() => {
                if (t.IsFaulted) {
                    exit = 1; File.WriteAllText(Path.Combine(folder, "failure.txt"), t.Exception.GetBaseException().ToString());
                }
                app.Shutdown();
            })));
            app.Run(); return exit;
        }
        private static async Task Scenarios(string folder, Stopwatch startup)
        {
            var store = new LocalStore(Path.Combine(folder, "data")); var settings = Settings.Defaults();
            string prefix = "benchmark-" + Guid.NewGuid().ToString("N") + "-";
            foreach (var provider in settings.Providers) provider.Id = prefix + provider.Id;
            settings.ProviderId = settings.Providers[0].Id;
            settings.Provider.Model = "benchmark-fixture"; settings.Provider.BaseUrl = "https://benchmark.invalid/v1";
            settings.Shortcut = "Ctrl+Alt+Shift+F12"; await store.SaveSettingsAsync(SettingsUpdate.Full(settings));
            var phases = new List<object>(); long ready;
            using (var shell = new AppShell(store, true, new LlmClient(new FixtureHandler()))) {
                shell.CredentialReader = p => "isolated-benchmark-fixture";
                shell.Start(true, false); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); ready = startup.ElapsedMilliseconds;
                if (shell.Popup.IsVisible) throw new InvalidOperationException("Benchmark shortcut could not be registered; no idle measurement was taken.");
                await Task.Delay(2000); // Let deferred framework initialization settle before steady idle.
                phases.Add(await Measure("tray_idle", () => Task.Delay(5000)));
                shell.ShowPopup(); shell.BeginSourceEdit(false); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                phases.Add(await Measure("empty_popup", () => Task.Delay(3000)));
                phases.Add(await Measure("streaming_translation", async () => {
                    await shell.TranslateAsync(string.Concat(Enumerable.Repeat("Reading a longer passage. ", 120)), "性能测试", true);
                    await Task.Delay(1000);
                }));
                shell.PopulateDemo(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                phases.Add(await Measure("word_card", () => Task.Delay(3000)));
                phases.Add(await MeasureHistory("history_long_200", Path.Combine(folder, "history-200"), 200, true));
                phases.Add(await MeasureHistory("history_long_1000_encode", Path.Combine(folder, "history-1000"), 1000, false));
                shell.OpenSettings(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                phases.Add(await Measure("settings_window", () => Task.Delay(3000)));
                phases.Add(await Measure("settings_open_close_15_times", async () => {
                    for (int i = 0; i < 15; i++) {
                        shell.OpenSettings(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        await shell.TryLeaveSettingsPageAsync();
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        await Task.Delay(100);
                    }
                    await Task.Delay(2000);
                }));
                shell.HidePopup(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                phases.Add(await Measure("tray_after_use", () => Task.Delay(5000)));
            }
            File.WriteAllText(Path.Combine(folder, "result.json"), Json.Write(new {
                version = typeof(Program).Assembly.GetName().Version.ToString(), utc = DateTime.UtcNow.ToString("o"),
                windows = Environment.OSVersion.Version.ToString(), logical_processors = Environment.ProcessorCount,
                processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                process_64bit = Environment.Is64BitProcess, app_ready_ms = ready, phases = phases,
                lifetime_peak_working_set_mib = MiB((long)Memory().PeakWorking.ToUInt64()), wpf_render_tier = System.Windows.Media.RenderCapability.Tier >> 16,
                note = "Isolated native tray/WPF process; local delayed SSE fixture, no real API. Samples every 100ms. CPU total is normalized by logical processors. Startup is Main-to-ready with existing OS caches; not a cold boot. Memory includes WPF and benchmark overhead; no forced GC or working-set trimming."
            }), new UTF8Encoding(false));
        }
        private static async Task<object> Measure(string name, Func<Task> action)
        {
            {
                IntPtr process = GetCurrentProcess(); long cpu = CpuTicks(); IoCounters before, after;
                GetProcessIoCounters(process, out before); var watch = Stopwatch.StartNew();
                double workingSum = 0, privateSum = 0, workingPeak = 0, privatePeak = 0; int samples = 0;
                var operation = action();
                do {
                    var counters = Memory(); double working = MiB((long)counters.Working.ToUInt64()), memory = MiB((long)counters.Private.ToUInt64());
                    workingSum += working; privateSum += memory; workingPeak = Math.Max(workingPeak, working); privatePeak = Math.Max(privatePeak, memory); samples++;
                    await Task.Delay(100);
                } while (!operation.IsCompleted);
                await operation; watch.Stop(); GetProcessIoCounters(process, out after);
                double oneCore = (CpuTicks() - cpu) / 10000.0 / watch.Elapsed.TotalMilliseconds * 100;
                uint handles; GetProcessHandleCount(process, out handles);
                return new {
                    phase = name, duration_ms = watch.ElapsedMilliseconds, samples = samples,
                    working_set_average_mib = Math.Round(workingSum / samples, 2), working_set_peak_mib = workingPeak,
                    private_bytes_average_mib = Math.Round(privateSum / samples, 2), private_bytes_peak_mib = privatePeak,
                    cpu_one_core_percent = Math.Round(oneCore, 3), cpu_system_percent = Math.Round(oneCore / Environment.ProcessorCount, 3),
                    threads = Threads(), handles = handles,
                    gdi_objects = GetGuiResources(process, 0), user_objects = GetGuiResources(process, 1),
                    io_read_bytes = after.ReadBytes - before.ReadBytes, io_write_bytes = after.WriteBytes - before.WriteBytes
                };
            }
        }
        private static async Task<object> MeasureHistory(string name, string directory, int sampleCount, bool sequential)
        {
            var store = new LocalStore(directory);
            var records = new List<TranslationRecord>();
            for (int i = 0; i < sampleCount; i++) records.Add(LongRecord(i, store.Settings));
            var watch = Stopwatch.StartNew();
            var gaps = new List<long>(); var gapWatch = Stopwatch.StartNew(); long lastBeat = 0;
            // Normal priority so the 20 ms heartbeat is observed even when the real work only
            // lasts a few tens of milliseconds; Background ticks would lose to the resumption.
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (s, e) => { long now = gapWatch.ElapsedMilliseconds; gaps.Add(now - lastBeat); lastBeat = now; };
            timer.Start();
            HistorySnapshots.HistoryPayload encoded = null;
            try {
                if (sequential) {
                    await Task.Yield();
                    foreach (var record in records) await store.SaveAsync(record, store.HistoryEpoch);
                    await store.FlushAsync();
                } else {
                    // One background pass: linear Encode of the 1000 candidates and a real atomic
                    // file commit, while the dispatcher keeps its 20 ms heartbeat.
                    encoded = await Task.Run(() => {
                        HistorySnapshots.ResetEncodeCalls();
                        var payload = HistorySnapshots.Encode(records, records.Count);
                        var batch = new AtomicFileBatch(directory, new PhysicalStoreFiles());
                        batch.Commit(new Dictionary<string, string> {
                            { AtomicFileBatch.HistoryName, payload.Json }
                        }, null, null, null);
                        return payload;
                    });
                }
            } finally { timer.Stop(); }
            watch.Stop();
            string historyPath = Path.Combine(directory, "history.json");
            long historyBytes = File.Exists(historyPath) ? new FileInfo(historyPath).Length : 0;
            return new {
                phase = name, duration_ms = watch.ElapsedMilliseconds, samples = sampleCount,
                sequential_saves = sequential, encode_calls = HistorySnapshots.EncodeCalls,
                records_kept = encoded == null ? -1 : encoded.Records.Count,
                utf8_bytes = encoded == null ? -1 : encoded.Utf8Bytes,
                heartbeat_beats = gaps.Count, heartbeat_max_gap_ms = gaps.Count == 0 ? -1 : gaps.Max(),
                history_file_bytes = historyBytes,
                note = "Real PhysicalStoreFiles; LongRecord = 2000-char source + 2000-char translation + 24x600-char chat. The 1000-record phase is one background linear Encode plus one real atomic commit, not 1000 sequential rewrites; heartbeat numbers are for this machine only."
            };
        }
        private static TranslationRecord LongRecord(int index, Settings context)
        {
            var record = TranslationRecord.Create(index.ToString() + new string('a', 2000), "夹具", context);
            record.Completed = true; record.Translation = new string('中', 2000);
            for (int i = 0; i < 24; i++) record.Chat.Add(new ChatTurn {
                Role = i % 2 == 0 ? "user" : "assistant", Content = new string('字', 600), Topic = "原句"
            });
            return record;
        }
        private static double MiB(long bytes) { return Math.Round(bytes / 1048576.0, 2); }
        private sealed class FixtureHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                string text = "这是一段用于测量界面更新成本的本地模拟译文。它不经过网络，也不读取任何用户内容。";
                string data = string.Concat(Enumerable.Repeat("data: " + Json.Write(new { choices = new[] { new { delta = new { content = text } } } }) + "\n\n", 20)) + "data: [DONE]\n\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(Encoding.UTF8.GetBytes(data))) });
            }
        }
        private sealed class SlowStream : MemoryStream
        {
            public SlowStream(byte[] bytes) : base(bytes) { }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellation)
            {
                await Task.Delay(75, cancellation).ConfigureAwait(false); return Read(buffer, offset, Math.Min(count, 384));
            }
        }
    }
}
