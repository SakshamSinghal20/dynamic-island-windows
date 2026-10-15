using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Island.Services;

public static class ProcessLifetime
{
    private static readonly object Gate = new();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int kind, IntPtr buffer, uint size, out uint returned);

    public static bool InJob { get { using var current = Process.GetCurrentProcess(); return IsProcessInJob(current.Handle, IntPtr.Zero, out bool value) && value; } }
    public static uint? JobFlags
    {
        get
        {
            if (!InJob) return null;
            IntPtr buffer = Marshal.AllocHGlobal(64);
            try { return QueryInformationJobObject(IntPtr.Zero, 2, buffer, 64, out _) ? (uint)Marshal.ReadInt32(buffer, 16) : null; }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    // Tool runners can own a kill-on-close job. A desktop app must outlive that runner.
    // WMI starts the app from its service rather than inheriting nested tool jobs.
    // The marker prevents another relay when WMI applies its own provider quotas.
    public static bool Detach(string[] args)
    {
        string? executable = Environment.ProcessPath;
        if (executable == null || Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) || Array.IndexOf(args, "--independent-launch") >= 0 || !InJob) return false;
        uint? jobFlags = JobFlags;
        if (jobFlags is uint limits)
        {
            if ((limits & 0x2000) == 0) return false;
            var command = new StringBuilder(Quote(executable));
            foreach (string argument in args) command.Append(' ').Append(Quote(argument));
            command.Append(" --independent-launch");
            object? locator = null, services = null, process = null, startupClass = null, startup = null, method = null, inputTemplate = null, input = null, output = null;
            try
            {
                locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
                services = ((dynamic)locator!).ConnectServer(".", "root\\cimv2");
                process = ((dynamic)services).Get("Win32_Process");
                startupClass = ((dynamic)services).Get("Win32_ProcessStartup");
                startup = ((dynamic)startupClass).SpawnInstance_();
                ((dynamic)startup).CreateFlags = 0x01000000 | 0x400;
                ((dynamic)startup).WinstationDesktop = "WinSta0\\Default";
                var environment = new List<string>();
                foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) environment.Add(entry.Key + "=" + entry.Value);
                ((dynamic)startup).EnvironmentVariables = environment.ToArray();
                ((dynamic)startup).ShowWindow = Array.IndexOf(args, "--lifetime-hold") >= 0 ? 0 : 1;
                method = ((dynamic)process).Methods_.Item("Create");
                inputTemplate = ((dynamic)method).InParameters;
                input = ((dynamic)inputTemplate).SpawnInstance_();
                ((dynamic)input).CommandLine = command.ToString();
                ((dynamic)input).CurrentDirectory = AppContext.BaseDirectory;
                ((dynamic)input).ProcessStartupInformation = startup;
                output = ((dynamic)services).ExecMethod("Win32_Process", "Create", input);
                uint result = (uint)((dynamic)output).ReturnValue;
                if (result != 0) { Write("detach-failed", "Windows process service code=" + result); return false; }
                Write("detached-launch", "broker=WMI child=" + ((dynamic)output).ProcessId); return true;
            }
            catch (Exception e) { Write("detach-failed", e.Message); return false; }
            finally
            {
                foreach (object? value in new[] { output, input, inputTemplate, method, startup, startupClass, process, services, locator })
                    if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
            }
        }
        return false;
    }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public static void Write(string kind, string detail = "")
    {
        if (Storage.Diagnostic) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Storage.Root);
                string path = Path.Combine(Storage.Root, "lifetime.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {kind} {detail}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
