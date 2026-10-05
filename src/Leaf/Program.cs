using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Security.Principal;

[assembly: AssemblyTitle("Leaf")]
[assembly: AssemblyDescription("A small Windows translator for reading and learning")]
[assembly: AssemblyVersion("0.4.1.0")]
[assembly: AssemblyFileVersion("0.4.1.0")]

namespace Leaf
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 1 && args[0] == "--ui-smoke") return Smoke.Run(args[1]);
            if (args.Length > 1 && args[0] == "--performance") return Performance.Run(args[1]);
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeafTranslate");
            var log = new DiagnosticLog(Path.Combine(folder, "logs"));
            string identity = WindowsIdentity.GetCurrent().User.Value.Replace("-", "");
            bool created;
            using (var instance = new Mutex(true, @"Local\LeafTranslate." + identity, out created)) {
                if (!created) {
                    IntPtr window = Native.FindWindow(null, "Leaf");
                    if (window == IntPtr.Zero) window = Native.FindWindow(null, "Leaf · 叶译"); // Earlier instances share the same data and mutex.
                    if (window != IntPtr.Zero) Native.PostMessage(window, 0x8001, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }
                try {
                    log.Event("app_start", null);
                    var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    AppDomain.CurrentDomain.UnhandledException += (s, e) => log.Event("unhandled_exception", e.ExceptionObject as Exception);
                    System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => log.Event("unobserved_task", e.Exception);
                    Ui.InitializeTheme();
                    using (var shell = new AppShell(new LocalStore(folder), true, new LlmClient { Log = log })) {
                        application.DispatcherUnhandledException += (s, e) => {
                            log.Event("ui_exception", e.Exception);
                            shell.ShowError(e.Exception is UserError ? e.Exception.Message : "操作未完成，请重试。", null); e.Handled = true;
                        };
                        shell.Start(args.Contains("--background"), args.Contains("--demo"));
                        application.Run();
                    }
                } catch (Exception error) {
                    log.Event("startup_failed", error);
                    MessageBox.Show(error is UserError ? error.Message : "Leaf无法启动。请确认 Windows 已安装 .NET Framework 4.8。", "Leaf");
                    return 1;
                } finally { instance.ReleaseMutex(); }
            }
            return 0;
        }
    }
}
