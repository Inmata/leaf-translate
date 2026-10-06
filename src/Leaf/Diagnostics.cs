using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace Leaf
{
    // Structured metadata only: never accept request/response bodies or passwords.
    public sealed class DiagnosticLog
    {
        // Fixed stage names are the only strings a capture row may carry.
        private static readonly string[] CaptureStages = {
            "snapshot", "uia", "copy_wait", "copy_focus", "copy_send", "clipboard_read", "clipboard_restore", "result"
        };
        // The only source names the internal selection row may carry.
        private static readonly string[] InternalSelectionSources = {
            "SourceInput", "SourceText", "TranslationText", "LeafSelectableText"
        };
        private readonly object sync = new object();
        private readonly int limit;
        public string Folder { get; private set; }
        public bool WriteFailed { get; private set; }
        public DiagnosticLog(string folder) : this(folder, 1048576) { }
        public DiagnosticLog(string folder, int maxBytes) { Folder = folder; limit = Math.Max(1024, maxBytes); }
        public void Event(string name, Exception error)
        {
            var row = Base(name);
            if (name == "app_start") {
                row["windows"] = Environment.OSVersion.Version.ToString();
                row["framework"] = Environment.Version.ToString(); row["process_64bit"] = Environment.Is64BitProcess;
            }
            Error(row, error); Write(row);
        }
        public void Request(string id, string operation, ProviderProfile provider, bool stream, int budget,
            string outcome, int status, long elapsed, Exception error, string apiCode, string parameter)
        {
            var row = Base("api");
            row["request_id"] = Token(id); row["operation"] = Token(operation);
            row["provider"] = Token(provider.Id); row["model"] = Token(provider.Model);
            Uri endpoint;
            row["endpoint_host"] = Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out endpoint) ? Token(endpoint.Host) : "invalid";
            row["stream"] = stream; row["output_token_limit"] = budget; row["outcome"] = Token(outcome);
            if (operation == "completion") {
                row["token_parameter"] = provider.Id == "openai" || LlmClient.IsOpenAiReasoning(provider) ? "max_completion_tokens" : "max_tokens";
                row["configured_thinking"] = Token(provider.ThinkingMode ?? "auto");
                if (LlmClient.IsGlm53(provider)) {
                    row["thinking_type"] = "enabled";
                    row["reasoning_effort"] = Token(provider.ReasoningEffort ?? "low");
                }
            }
            row["http_status"] = status; row["elapsed_ms"] = elapsed;
            row["api_code"] = Token(apiCode); row["parameter"] = Token(parameter);
            Error(row, error); Write(row);
        }
        // One row per whitelisted stage plus one final row, all sharing the capture id.
        // Each stage carries the duration of that stage alone, so the sum below is the real
        // elapsed time of the capture instead of a multiple of it.
        // This never accepts selection text, window titles, control Name/Value, context,
        // conversation content or a content hash.
        public void Capture(CaptureTelemetry telemetry)
        {
            if (telemetry == null) return;
            string id = Token(telemetry.Id);
            long elapsed = 0;
            var steps = telemetry.Steps ?? new List<CaptureStep>();
            foreach (var step in steps) {
                if (step == null) continue;
                string stage = Token(step.Stage);
                if (Array.IndexOf(CaptureStages, stage) < 0) continue;
                elapsed += step.ElapsedMs;
                // Only the fields the stage really checked are written: an unchecked field is
                // left out instead of being logged as a false that was never looked at.
                var fields = step.Checked;
                var row = Base("capture_stage");
                row["capture_id"] = id; row["stage"] = stage;
                row["reason"] = step.Reason.ToString();
                if ((fields & CaptureFields.Read) != 0) {
                    row["candidate_count"] = step.CandidateCount; row["control_type_id"] = step.ControlTypeId;
                    row["pattern_supported"] = step.PatternSupported;
                }
                if ((fields & CaptureFields.Foreground) != 0) row["foreground_same"] = step.ForegroundSame;
                if ((fields & CaptureFields.Clipboard) != 0) row["clipboard_changed"] = step.ClipboardChanged;
                if ((fields & CaptureFields.Modifiers) != 0) row["modifiers_released"] = step.ModifiersReleased;
                if ((fields & CaptureFields.Focus) != 0) {
                    row["focus_checked"] = step.FocusChecked; row["focus_owned"] = step.FocusOwned;
                }
                if ((fields & CaptureFields.Owner) != 0) {
                    row["clipboard_owner_process"] = (long)step.ClipboardOwnerProcess;
                    row["owner_window_verified"] = step.OwnerWindowVerified;
                }
                if (step.NativeError != 0) row["native_error"] = step.NativeError;
                if (step.HResult != 0) row["hresult"] = step.HResult;
                row["elapsed_ms"] = step.ElapsedMs;
                Write(row);
            }
            // Only a definite system failure is a capture_failed; Empty/Unavailable are normal results.
            var result = Base(telemetry.Status == CaptureStatus.Failed ? "capture_failed" : "capture_result");
            result["capture_id"] = id; result["stage"] = "result";
            result["clipboard_mode"] = telemetry.ClipboardMode; result["shortcut"] = Token(telemetry.Shortcut);
            result["foreground_process_id"] = (long)telemetry.ForegroundProcessId;
            result["status"] = telemetry.Status.ToString(); result["reason"] = telemetry.Reason.ToString();
            result["text_length"] = telemetry.TextLength; result["step_count"] = steps.Count;
            result["elapsed_ms"] = elapsed;
            Write(result);
        }
        // The in-window selection path records typed metadata only: its status, its typed
        // reason, a whitelisted source name and the length of what was read. The text itself,
        // the control's own name and anything written in a window never reach the log.
        public void InternalSelection(InternalSelectionStatus status, InternalSelectionReason reason, string source, int textLength)
        {
            var row = Base("internal_selection");
            row["status"] = status.ToString();
            row["reason"] = reason.ToString();
            row["source"] = InternalSource(source);
            row["text_length"] = textLength < 0 ? 0 : textLength;
            Write(row);
        }
        private static string InternalSource(string source)
        {
            if (string.IsNullOrEmpty(source)) return "";
            foreach (string known in InternalSelectionSources)
                if (string.Equals(known, source, StringComparison.Ordinal)) return known;
            return "other";
        }
        private static Dictionary<string, object> Base(string name)
        {
            return new Dictionary<string, object> {
                { "utc", DateTime.UtcNow.ToString("o") }, { "event", Token(name) },
                { "version", typeof(Program).Assembly.GetName().Version.ToString() }, { "pid", Process.GetCurrentProcess().Id }
            };
        }
        private static void Error(Dictionary<string, object> row, Exception error)
        {
            if (error == null) return;
            row["error_type"] = Token(error.GetType().Name);
            row["hresult"] = error.HResult;
            var user = error as UserError;
            if (user != null) row["error_code"] = Token(user.Code);
            var native = error as System.ComponentModel.Win32Exception;
            if (native != null) row["native_error"] = native.NativeErrorCode;
            var frames = new StackTrace(error, false).GetFrames();
            var locations = new List<string>();
            if (frames != null) foreach (var frame in frames) {
                MethodBase method = frame.GetMethod();
                if (method != null && method.DeclaringType != null && method.DeclaringType.Namespace == "Leaf")
                    locations.Add(Token(method.DeclaringType.Name + "." + method.Name));
                if (locations.Count == 8) break;
            }
            if (locations.Count > 0) row["locations"] = locations;
        }
        private static string Token(string text)
        {
            if (text == null) return "";
            var safe = new StringBuilder();
            foreach (char c in text) {
                if (safe.Length == 100) break;
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == ':' || c == '/') safe.Append(c);
            }
            return safe.ToString();
        }
        private void Write(Dictionary<string, object> row)
        {
            lock (sync) {
                try {
                    Directory.CreateDirectory(Folder);
                    string current = Path.Combine(Folder, "leaf.log");
                    string line = Json.Write(row) + Environment.NewLine;
                    if (File.Exists(current) && new FileInfo(current).Length + Encoding.UTF8.GetByteCount(line) > limit) {
                        for (int i = 4; i >= 1; i--) {
                            string destination = Path.Combine(Folder, "leaf." + i + ".log");
                            string source = i == 1 ? current : Path.Combine(Folder, "leaf." + (i - 1) + ".log");
                            if (File.Exists(destination)) File.Delete(destination);
                            if (File.Exists(source)) File.Move(source, destination);
                        }
                    }
                    File.AppendAllText(current, line, new UTF8Encoding(false)); WriteFailed = false;
                } catch { WriteFailed = true; } // A logging failure must not break translation.
            }
        }
    }
}
