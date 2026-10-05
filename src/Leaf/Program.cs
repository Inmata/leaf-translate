using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Security.Principal;

[assembly: AssemblyTitle("Leaf · 叶译")]
[assembly: AssemblyDescription("A small Windows translator for reading and learning")]
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]

namespace Leaf
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 1 && args[0] == "--ui-smoke") return Smoke.Run(args[1]);
            string identity = WindowsIdentity.GetCurrent().User.Value.Replace("-", "");
            bool created;
            using (var instance = new Mutex(true, @"Local\LeafTranslate." + identity, out created)) {
                if (!created) {
                    IntPtr window = Native.FindWindow(null, "Leaf · 叶译");
                    if (window != IntPtr.Zero) Native.PostMessage(window, 0x8001, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }
                try {
                    var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    Ui.InitializeTheme();
                    string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeafTranslate");
                    using (var shell = new AppShell(new LocalStore(folder), true)) {
                        application.DispatcherUnhandledException += (s, e) => {
                            shell.ShowError(e.Exception is UserError ? e.Exception.Message : "操作未完成，请重试。", null); e.Handled = true;
                        };
                        shell.Start(args.Contains("--background"), args.Contains("--demo"));
                        application.Run();
                    }
                } catch (Exception error) {
                    MessageBox.Show(error is UserError ? error.Message : "叶译无法启动。请确认 Windows 已安装 .NET Framework 4.8。", "叶译");
                    return 1;
                } finally { instance.ReleaseMutex(); }
            }
            return 0;
        }
    }
}
