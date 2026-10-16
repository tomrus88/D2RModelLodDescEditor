using System;
using System.Runtime.InteropServices;
using Avalonia;

namespace D2RModelLodDescEditor
{
    internal sealed class Program
    {
        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            // Headless format audit: D2RModelLodDescEditor --selftest <bin> [json].
            // The app is WinExe (no console of its own), so attach to the parent
            // console to make `dotnet run -- --selftest ...` output visible; the
            // full report is always mirrored to selftest-report.txt as well.
            if (args.Length > 0 && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                AttachConsole(AttachParentProcess);
                Environment.ExitCode = SelfTest.Run(args);
                return;
            }

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
#if DEBUG
                .WithDeveloperTools()
#endif
                .WithInterFont()
                .LogToTrace();
    }
}
