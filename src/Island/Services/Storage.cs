using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Island.Core;

namespace Island.Services;

public sealed class Settings
{
    public TimerSession? Timer { get; set; }
    public string MediaSource { get; set; } = "";
    public string Screen { get; set; } = "";
    public string SpotifyClientId { get; set; } = "";
    public string HostId { get; set; } = "urn:uuid:" + Guid.NewGuid();
    public string PendingClientId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public bool? ReduceMotion { get; set; }
    public bool ClipboardHistoryEnabled { get; set; }
    public DateTimeOffset ClipboardHistoryClearedAt { get; set; }
}

public static class Storage
{
    public static bool Diagnostic;
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Island");
    private static readonly SemaphoreSlim Gate = new(1);
    public static Settings LoadSettings()
    {
        if (Diagnostic) return new();
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(Root, "settings.json"))) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public static async Task SaveSettings(Settings settings)
    {
        if (Diagnostic) return;
        // Serialize a snapshot before yielding; later writes cannot regress state.
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings);
        await Gate.WaitAsync();
        try { await Task.Run(() => AtomicWrite("settings.json", bytes)); }
        finally { Gate.Release(); }
    }
    private static void AtomicWrite(string name, byte[] bytes)
    {
        Directory.CreateDirectory(Root);
        string target = Path.Combine(Root, name), temp = target + ".tmp";
        File.WriteAllBytes(temp, bytes); File.Move(temp, target, true);
    }
    public static T? LoadCredential<T>(string name)
    {
        if (Diagnostic) return default;
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(Root, name + ".bin")), null, DataProtectionScope.CurrentUser);
            try { return JsonSerializer.Deserialize<T>(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception e) when (e is IOException or JsonException or CryptographicException or UnauthorizedAccessException) { return default; }
    }
    public static void SaveCredential<T>(string name, T value)
    {
        if (Diagnostic) return;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try { AtomicWrite(name + ".bin", ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static void RemoveCredential(string name) { if (!Diagnostic) File.Delete(Path.Combine(Root, name + ".bin")); }
}
