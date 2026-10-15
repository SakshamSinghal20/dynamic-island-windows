using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Island.Core;

public record ServerEvent(string Name, string Data);
public sealed class ServerEventParser
{
    private readonly List<byte> line = [];
    private readonly List<string> fields = [];
    private string name = "message";
    private bool carriageReturn;
    private int fieldBytes;
    public List<ServerEvent> Append(ReadOnlySpan<byte> bytes)
    {
        var events = new List<ServerEvent>();
        foreach (byte b in bytes)
        {
            if (b == 10 && carriageReturn) { carriageReturn = false; continue; }
            carriageReturn = b == 13;
            if (b is 10 or 13)
            {
                string text = Encoding.UTF8.GetString(line.ToArray()); line.Clear();
                if (text.Length == 0)
                {
                    if (fields.Count > 0) events.Add(new(name, string.Join("\n", fields)));
                    fields.Clear(); fieldBytes = 0; name = "message";
                }
                else if (text.StartsWith("data:", StringComparison.Ordinal))
                {
                    string value = text[5..]; if (value.StartsWith(' ')) value = value[1..];
                    fields.Add(value); fieldBytes += Encoding.UTF8.GetByteCount(value);
                }
                else if (text.StartsWith("event:", StringComparison.Ordinal)) name = text[6..].Trim();
            }
            else line.Add(b);
            if (line.Count > 2_000_000 || fieldBytes > 4_000_000) throw new InvalidDataException("Response event exceeded the size limit.");
        }
        return events;
    }
}
public sealed class ChatMessage(string role, string text, string state = "complete")
{
    public string Role { get; } = role;
    public string Text { get; set; } = text;
    public string State { get; set; } = state;
}
public sealed class Conversation
{
    public List<ChatMessage> Messages { get; } = [];
    public object[] Input => Messages.Where(m => m.State == "complete").Select(m => (object)new { role = m.Role, content = m.Text }).ToArray();
    public ChatMessage Begin(string prompt)
    {
        Messages.Add(new("user", prompt)); var answer = new ChatMessage("assistant", "", "streaming"); Messages.Add(answer); return answer;
    }
    public string? RemoveLastExchange()
    {
        if (Messages.Count < 2 || Messages[^1].Role != "assistant" || Messages[^2].Role != "user") return null;
        string prompt = Messages[^2].Text; Messages.RemoveRange(Messages.Count - 2, 2); return prompt;
    }
}
