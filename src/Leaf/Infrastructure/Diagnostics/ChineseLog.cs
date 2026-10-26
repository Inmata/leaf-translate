using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Leaf
{
    // The zh-CN companion of leaf.log. Every line is rendered deterministically from the same
    // structured row the JSON log writes, using only a whitelist of keys and fixed name
    // mappings. It never reaches a model or the network, and no selection text, translation,
    // context, conversation, window title, control Name/Value, key or raw exception message
    // can reach it: a value is rendered only when it is a string held by one of the row's own
    // string keys or an integer, and each such string key is itself written from an explicit
    // safe domain - a Token-filtered name or an enum name DiagnosticLog produced. An event or
    // enum this file does not know renders as a fixed "未分类" placeholder with no code
    // echoed, so an unrecognised case is never silently dropped while nothing it carried is
    // reproduced. Filtering is not what makes a value safe here; the value's source is.
    internal static class ChineseLog
    {
        public const string FileName = "leaf.zh-CN.log";
        // Every event name the product logs, mapped once. A name that is not here still
        // renders as "未分类" rather than being dropped, but its code is not echoed: the
        // placeholder is what makes an unmapped name safe to write, never a filter on its code.
        private static readonly Dictionary<string, string> Events = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "app_start", "程序启动" }, { "app_exit", "程序退出" }, { "api", "API 请求" },
            { "capture_stage", "取词阶段" }, { "capture_result", "取词结果" }, { "capture_failed", "取词失败" },
            { "internal_selection", "窗口内选区读取" },
            { "shortcut_registered", "快捷键已注册" }, { "shortcut_register_failed", "快捷键注册失败" },
            { "shortcut_restore_failed", "快捷键恢复失败" }, { "logs_open_failed", "打开日志目录失败" },
            { "settings_saved", "设置已保存" }, { "settings_save_failed", "设置保存失败" },
            { "settings_page_leave_flush_failed", "离开设置页刷新失败" },
            { "settings_close_flush_failed", "关闭设置刷新失败" },
            { "preferences_save_failed", "偏好保存失败" }, { "credential_migration_failed", "密钥迁移失败" },
            { "history_save_failed", "历史保存失败" }, { "placement_save_failed", "窗口位置保存失败" },
            { "model_apply_failed", "模型应用失败" }, { "model_discovery_failed", "模型获取失败" },
            { "connection_test_failed", "连接测试失败" }, { "copy_failed", "复制失败" },
            { "translation_failed", "翻译失败" }, { "word_card_failed", "词卡失败" },
            { "followup_failed", "追问失败" }, { "exit_failed", "退出失败" },
            { "exit_flush_failed", "退出刷新失败" }, { "unhandled_exception", "未处理异常" },
            { "unobserved_task", "未观察任务异常" }, { "ui_exception", "界面异常" },
            { "startup_failed", "启动失败" }
        };
        private static readonly Dictionary<string, string> Stages = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "snapshot", "取词前快照" }, { "uia", "自动化读取" }, { "copy_wait", "复制前等待" },
            { "copy_focus", "复制前焦点检查" }, { "copy_send", "模拟复制发送" },
            { "clipboard_read", "剪贴板读取" }, { "clipboard_restore", "剪贴板还原" }, { "result", "最终结果" }
        };
        private static readonly Dictionary<string, string> Reasons = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "None", "无" }, { "Desktop", "桌面或任务栏" }, { "EmptySelection", "确认空选区" },
            { "UnsupportedPattern", "不支持文本模式" }, { "AutomationTimeout", "自动化读取超时" },
            { "AutomationBusy", "上一次取词未结束" }, { "AutomationBudget", "自动化预算用尽" },
            { "AutomationUnreadable", "控件无法读取" }, { "ForegroundChanged", "前台窗口已变化" },
            { "ModifiersHeld", "按键仍未松开" }, { "CopyNoUpdate", "模拟复制无新内容" },
            { "ClipboardEmpty", "剪贴板无文字" }, { "ClipboardOriginUnknown", "剪贴板来源未知" },
            { "ClipboardBusy", "剪贴板被占用" }, { "CaptureBusy", "取词忙" }, { "InputDenied", "输入被拒绝" },
            { "SystemFailure", "系统失败" }, { "Cancelled", "已取消" },
            { "OwnershipUnproven", "无法证明窗口归属" }, { "FocusNotOwned", "键盘焦点不在取词窗口内" }
        };
        private static readonly Dictionary<string, string> Statuses = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "Text", "取得文字" }, { "Empty", "确认空选区" }, { "Unavailable", "未取得" },
            { "Failed", "系统失败" }, { "Cancelled", "已取消" }
        };
        private static readonly Dictionary<string, string> InternalReasons = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "None", "无" }, { "NoPage", "无可用页面" }, { "WindowInactive", "窗口未激活" },
            { "NoFocus", "无键盘焦点" }, { "FocusOutsidePage", "焦点不在页面内" },
            { "ContentFocus", "焦点在内容控件" }, { "HiddenPage", "页面已隐藏" },
            { "SensitiveControl", "敏感控件" }, { "NotSelectable", "不可选择" }, { "EmptySelection", "确认空选区" }
        };
        private static readonly Dictionary<string, string> Sources = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "SourceInput", "原文输入框" }, { "SourceText", "原文文本" }, { "TranslationText", "译文文本" },
            { "LeafSelectableText", "可选文本控件" }, { "other", "其他来源" }, { "", "无来源" }
        };
        private static readonly Dictionary<string, string> Operations = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "models", "模型列表" }, { "completion", "对话补全" }
        };
        private static readonly Dictionary<string, string> Outcomes = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "started", "已开始" }, { "success", "成功" }, { "cancelled", "已取消" }, { "failed", "失败" }
        };
        private static readonly Dictionary<string, string> ErrorCodes = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "key", "密钥未填写" }, { "model", "模型未填写" }, { "network", "网络连接失败" },
            { "timeout", "请求超时" }, { "format", "返回格式异常" }, { "models", "模型列表不可用" },
            { "parameter", "参数被拒绝" }, { "length", "输入过长" }, { "selection", "取词未完成" }
        };
        // A row this file cannot describe is dropped rather than guessed at, and a formatting
        // fault still yields a line carrying the time, version and pid: the event is never
        // silently lost, and Render itself never throws.
        public static string Render(Dictionary<string, object> row)
        {
            if (row == null) return "";
            try { return Prefix(row) + Body(row) + Environment.NewLine; }
            catch {
                try { return Prefix(row) + "事件=未分类(格式化失败)" + Environment.NewLine; }
                catch { return "事件=未分类(格式化失败)" + Environment.NewLine; }
            }
        }
        private static string Prefix(Dictionary<string, object> row)
        {
            return "[" + Local(Value(row, "utc")) + "] 版本=" + Str(row, "version") +
                " pid=" + Int(row, "pid") + " ";
        }
        private static string Body(Dictionary<string, object> row)
        {
            var text = new StringBuilder();
            string name = Text(Value(row, "event"));
            switch (name) {
                case "app_start": AppStart(text, row); break;
                case "api": Api(text, row); break;
                case "capture_stage": CaptureStage(text, row); break;
                case "capture_result": CaptureResult(text, row, false); break;
                case "capture_failed": CaptureResult(text, row, true); break;
                case "internal_selection": InternalSelection(text, row); break;
                default: text.Append("事件=").Append(Mapped(Events, name)); break;
            }
            ErrorFields(text, row);
            return text.ToString();
        }
        private static void AppStart(StringBuilder text, Dictionary<string, object> row)
        {
            text.Append("事件=程序启动");
            Add(text, "系统版本", Str(row, "windows"));
            Add(text, "运行时", Str(row, "framework"));
            AddFlag(text, "64位进程", row, "process_64bit");
        }
        private static void Api(StringBuilder text, Dictionary<string, object> row)
        {
            text.Append("事件=API 请求");
            Add(text, "请求ID", Str(row, "request_id"));
            Add(text, "操作", Mapped(Operations, Str(row, "operation")));
            Add(text, "服务", Str(row, "provider"));
            Add(text, "模型", Str(row, "model"));
            Add(text, "接口主机", Str(row, "endpoint_host"));
            AddFlag(text, "流式", row, "stream");
            AddNum(text, "输出上限", row, "output_token_limit");
            Add(text, "结果", Mapped(Outcomes, Str(row, "outcome")));
            Add(text, "token参数", Str(row, "token_parameter"));
            Add(text, "思考设置", Str(row, "configured_thinking"));
            Add(text, "思考类型", Str(row, "thinking_type"));
            Add(text, "推理强度", Str(row, "reasoning_effort"));
            AddNum(text, "HTTP状态", row, "http_status");
            AddNum(text, "耗时", row, "elapsed_ms", "ms");
            Add(text, "接口错误码", Str(row, "api_code"));
            Add(text, "参数", Str(row, "parameter"));
        }
        // The stage row's facts, each printed only when the event really carried it. The
        // document counts tell a real log where a document was read, what it answered and
        // which budget stopped the walk; none of them claims to be the single root cause.
        private static void CaptureStage(StringBuilder text, Dictionary<string, object> row)
        {
            text.Append("事件=取词阶段");
            Add(text, "capture", Str(row, "capture_id"));
            Add(text, "阶段", Mapped(Stages, Str(row, "stage")));
            Add(text, "原因", Mapped(Reasons, Str(row, "reason")));
            AddFlag(text, "前台一致", row, "foreground_same");
            AddFlag(text, "剪贴板有变化", row, "clipboard_changed");
            AddFlag(text, "按键已释放", row, "modifiers_released");
            AddFlag(text, "焦点已检查", row, "focus_checked");
            AddFlag(text, "焦点属于取词窗口", row, "focus_owned");
            AddNum(text, "剪贴板属主进程", row, "clipboard_owner_process");
            AddFlag(text, "属主窗口已验证", row, "owner_window_verified");
            AddNum(text, "候选数", row, "candidate_count");
            AddNum(text, "控件类型", row, "control_type_id");
            AddFlag(text, "支持文本模式", row, "pattern_supported");
            AddNum(text, "文档数", row, "document_count");
            AddNum(text, "焦点链文档", row, "document_chain_count");
            AddNum(text, "文档空选区", row, "document_empty_count");
            AddNum(text, "文档无文本模式", row, "document_nopattern_count");
            AddNum(text, "文档不可读取", row, "document_unreadable_count");
            AddFlag(text, "候选截断", row, "candidate_bound");
            AddFlag(text, "节点截断", row, "node_bound");
            AddFlag(text, "时间截断", row, "time_bound");
            AddFlag(text, "键清理失败", row, "key_cleanup_failed");
            AddNum(text, "键清理错误", row, "key_cleanup_error");
            AddNum(text, "系统错误", row, "native_error");
            AddNum(text, "HResult", row, "hresult");
            AddNum(text, "耗时", row, "elapsed_ms", "ms");
        }
        // The context-free hint stands only where a capture really did not succeed: a definite
        // system failure or an inconclusive "未取得". A confirmed empty, a successful capture
        // and a cancellation are answers of their own, so they get no root-cause caveat.
        private static void CaptureResult(StringBuilder text, Dictionary<string, object> row, bool failed)
        {
            text.Append(failed ? "事件=取词失败" : "事件=取词结果");
            Add(text, "capture", Str(row, "capture_id"));
            Add(text, "状态", Mapped(Statuses, Str(row, "status")));
            Add(text, "原因", Mapped(Reasons, Str(row, "reason")));
            AddFlag(text, "剪贴板模式", row, "clipboard_mode");
            Add(text, "快捷键", Str(row, "shortcut"));
            AddNum(text, "前台进程", row, "foreground_process_id");
            AddNum(text, "文本长度", row, "text_length");
            AddNum(text, "阶段数", row, "step_count");
            AddNum(text, "总耗时", row, "elapsed_ms", "ms");
            if (failed || Str(row, "status") == "Unavailable") text.Append(" 说明=日志显示失败阶段，尚不能确认唯一根因");
        }
        private static void InternalSelection(StringBuilder text, Dictionary<string, object> row)
        {
            text.Append("事件=窗口内选区读取");
            Add(text, "状态", Mapped(Statuses, Str(row, "status")));
            Add(text, "原因", Mapped(InternalReasons, Str(row, "reason")));
            Add(text, "来源", Mapped(Sources, Str(row, "source")));
            AddNum(text, "文本长度", row, "text_length");
        }
        // The exception block the JSON log writes through Error(): only the keys that block
        // builds, and only when it really ran. It never carries a message, only a type name,
        // a code, an HResult, a native error and Leaf method locations.
        private static void ErrorFields(StringBuilder text, Dictionary<string, object> row)
        {
            if (!row.ContainsKey("error_type")) return;
            Add(text, "异常类型", Str(row, "error_type"));
            Add(text, "错误码", Mapped(ErrorCodes, Str(row, "error_code")));
            AddNum(text, "异常HResult", row, "hresult");
            AddNum(text, "系统错误", row, "native_error");
            object locations;
            if (row.TryGetValue("locations", out locations)) {
                var names = new List<string>();
                var list = locations as IEnumerable<string>;
                if (list != null) foreach (string name in list) { string safe = Safe(name); if (safe.Length > 0) names.Add(safe); }
                if (names.Count > 0) Add(text, "位置", string.Join("/", names.ToArray()));
            }
        }
        private static void Add(StringBuilder text, string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            text.Append(' ').Append(name).Append('=').Append(value);
        }
        private static void AddFlag(StringBuilder text, string name, Dictionary<string, object> row, string key)
        {
            object value;
            if (!row.TryGetValue(key, out value) || !(value is bool)) return;
            Add(text, name, (bool)value ? "是" : "否");
        }
        private static void AddNum(StringBuilder text, string name, Dictionary<string, object> row, string key, string unit = "")
        {
            string value = Int(row, key);
            if (value.Length == 0) return;
            Add(text, name, value + unit);
        }
        // An unknown code renders as a fixed placeholder with nothing of the code echoed: an
        // unmapped name is still present as 未分类 - never silently dropped - and the code that
        // was not understood is never reproduced from a value whose only filter was a charset.
        private static string Mapped(Dictionary<string, string> names, string code)
        {
            string name;
            if (names.TryGetValue(code ?? "", out name)) return name;
            return "未分类";
        }
        private static object Value(Dictionary<string, object> row, string key)
        {
            object value;
            return row.TryGetValue(key, out value) ? value : null;
        }
        private static string Str(Dictionary<string, object> row, string key)
        {
            return Text(Value(row, key));
        }
        // A string is rendered only when the row really holds a string. Any other object is
        // dropped instead of being run through a general conversion, so nothing is printed
        // merely because it has a ToString of its own.
        private static string Text(object value)
        {
            return value is string ? Safe((string)value) : "";
        }
        // Only an integer is rendered as a number. A bool, a string or any other value in a
        // numeric field is not a number this log knows, so the field is left out rather than
        // printing whatever it happens to hold.
        private static string Int(Dictionary<string, object> row, string key)
        {
            object value;
            if (!row.TryGetValue(key, out value)) return "";
            if (value is int) return ((int)value).ToString(CultureInfo.InvariantCulture);
            if (value is long) return ((long)value).ToString(CultureInfo.InvariantCulture);
            return "";
        }
        // One line, one fact: control characters can never split a line, and a value past a
        // readable length is cut rather than pasted in whole. This is defence in depth for a
        // string already known to come from a safe domain - it is not what makes the value
        // safe, and it is never applied to an arbitrary object.
        private static string Safe(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var safe = new StringBuilder();
            foreach (char c in text) {
                if (safe.Length == 160) { safe.Append('…'); break; }
                if (char.IsControl(c)) continue;
                safe.Append(c);
            }
            return safe.ToString();
        }
        private static string Local(object utc)
        {
            DateTime parsed;
            if (utc is DateTime) parsed = (DateTime)utc;
            else if (!(utc is string) || !DateTime.TryParse((string)utc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out parsed))
                return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            return (parsed.Kind == DateTimeKind.Utc ? parsed.ToLocalTime() : parsed).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }
    }
}
