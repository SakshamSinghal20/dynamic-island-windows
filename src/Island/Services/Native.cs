using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Island.Services;

public static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr SetActiveWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int w, int h, uint flags);
    public static void ToolWindow(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, (IntPtr)(style | 0x80));
    }
    public static void ClickThrough(IntPtr hwnd, bool enabled)
    {
        long style = GetWindowLongPtr(hwnd, -20).ToInt64(), updated = enabled ? style | 0x20 : style & ~0x20;
        if (updated != style) SetWindowLongPtr(hwnd, -20, (IntPtr)updated);
    }
    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    public static string StartupShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Island.lnk");
    public static string InstalledExecutable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Island", "Island.exe");
    public static bool StartsWithWindows
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return File.Exists(StartupShortcut) || key?.GetValue("Island") is string; }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value)
            {
                string executable = File.Exists(InstalledExecutable) ? InstalledExecutable : Environment.ProcessPath ?? throw new InvalidOperationException("Island executable could not be located.");
                Directory.CreateDirectory(Path.GetDirectoryName(StartupShortcut)!);
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                try
                {
                    dynamic shortcut = shell.CreateShortcut(StartupShortcut);
                    try
                    {
                        shortcut.TargetPath = executable; shortcut.Arguments = "--startup";
                        shortcut.WorkingDirectory = Path.GetDirectoryName(executable);
                        shortcut.Description = "Start Island at Windows sign-in"; shortcut.IconLocation = executable + ",0";
                        shortcut.Save();
                    }
                    finally { Marshal.FinalReleaseComObject(shortcut); }
                }
                finally { Marshal.FinalReleaseComObject(shell); }
                using var approval = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", true);
                approval?.DeleteValue("Island.lnk", false);
            }
            else File.Delete(StartupShortcut);
            // Replace the old OneDrive Run entry, keeping one startup mechanism.
            key.DeleteValue("Island", false);
        }
    }
}
