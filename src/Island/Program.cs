using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Island.Services;

namespace Island;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool lifetimeProbe = args.Contains("--lifetime-hold");
        Storage.Diagnostic = lifetimeProbe || args.Contains("--motion-check") || args.Contains("--interaction-check") || args.Contains("--startup-check") || args.Contains("--render-only");
        if ((!Storage.Diagnostic || lifetimeProbe) && ProcessLifetime.Detach(args)) return 0;
        if (lifetimeProbe)
        {
            int index = Array.IndexOf(args, "--lifetime-hold");
            if (index + 2 >= args.Length) return 2;
            File.WriteAllText(args[index + 1], Environment.ProcessId.ToString());
            File.WriteAllText(args[index + 1] + ".profile", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            for (int i = 0; i < 300; i++) { if (File.Exists(args[index + 2])) return 0; Thread.Sleep(100); }
            return 2;
        }
        using var reveal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Island.Windows.Reveal");
        using var settingsRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Island.Windows.Settings");
        using var mutex = new Mutex(true, Storage.Diagnostic ? "Local\\Island.Windows.Diagnostics" : "Local\\Island.Windows", out bool first);
        if (!first) { if (!Storage.Diagnostic) { if (args.Contains("--settings")) settingsRequest.Set(); else reveal.Set(); } return 0; }
        ProcessLifetime.Write("start", "in-job=" + ProcessLifetime.InJob + " job-flags=" + ProcessLifetime.JobFlags + " startup=" + args.Contains("--startup"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ProcessLifetime.Write("process-exit");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ProcessLifetime.Write("unhandled-exception", e.ExceptionObject.ToString() ?? "");
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Exit += (_, e) => ProcessLifetime.Write("application-exit", "code=" + e.ApplicationExitCode);
            app.SessionEnding += (_, e) => ProcessLifetime.Write("session-ending", e.ReasonSessionEnding.ToString());
            app.DispatcherUnhandledException += (_, e) =>
            {
                ProcessLifetime.Write("dispatcher-exception", e.Exception.ToString());
                Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "last-error.txt"), e.Exception.ToString());
                // Fail visibly; swallowing render-loop exceptions can leave an invisible overlay.
                MessageBox.Show("Island encountered an error. Details were saved to " + Path.Combine(Storage.Root, "last-error.txt"), "Island");
                e.Handled = true; app.Shutdown(1);
            };
            var window = new IslandWindow();
            RegisteredWaitHandle? activation = Storage.Diagnostic ? null : ThreadPool.RegisterWaitForSingleObject(reveal,
                (_, _) => app.Dispatcher.BeginInvoke(window.Reveal), null, Timeout.Infinite, false);
            RegisteredWaitHandle? settingsActivation = Storage.Diagnostic ? null : ThreadPool.RegisterWaitForSingleObject(settingsRequest,
                (_, _) => app.Dispatcher.BeginInvoke(window.ShowSettings), null, Timeout.Infinite, false);
            try { return app.Run(window); }
            finally { activation?.Unregister(null); settingsActivation?.Unregister(null); }
        }
        catch (Exception e)
        {
            ProcessLifetime.Write("startup-exception", e.ToString());
            Directory.CreateDirectory(Storage.Root); File.WriteAllText(Path.Combine(Storage.Root, "last-error.txt"), e.ToString());
            return 1;
        }
    }
}
