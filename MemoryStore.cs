using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NayfWindows;

/// <summary>
/// A local store of durable facts Nayf has learned about the user. Persisted to
/// %LOCALAPPDATA%\Nayf\memories.json, shown in the Memory tab, and injected into
/// the system prompt so Nayf remembers across sessions. This is the client-side
/// equivalent of the Mac app's server-side memory.
/// </summary>
public sealed class MemoryStore : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Memories { get; } = new();
    public bool HasMemories => Memories.Count > 0;

    private const int MaxMemories = 60;
    private readonly string _path;

    public MemoryStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nayf");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "memories.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var items = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path));
            if (items != null)
                foreach (var m in items) Memories.Add(m);
        }
        catch { /* start empty */ }
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Memories.ToList())); }
        catch { /* non-fatal */ }
    }

    /// <summary>Adds a fact if it isn't already known (case-insensitive).</summary>
    public void Add(string memory)
    {
        memory = memory?.Trim() ?? "";
        if (memory.Length == 0) return;
        if (Memories.Any(m => string.Equals(m, memory, StringComparison.OrdinalIgnoreCase))) return;

        Memories.Add(memory);
        while (Memories.Count > MaxMemories) Memories.RemoveAt(0);
        Save();
        OnChanged();
    }

    public void ClearAll()
    {
        Memories.Clear();
        Save();
        OnChanged();
    }

    /// <summary>The block appended to the system prompt so Nayf recalls the user.</summary>
    public string ContextBlock()
    {
        if (Memories.Count == 0) return "";
        return "What you already know about the user (remember this across the conversation):\n"
            + string.Join("\n", Memories.Select(m => "- " + m));
    }

    private void OnChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMemories)));
}
