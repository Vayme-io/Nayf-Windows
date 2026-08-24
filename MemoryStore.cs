using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NayfWindows;

/// <summary>
/// A local store of durable facts Vayme has learned about the user. Persisted to
/// %LOCALAPPDATA%\Vayme\memories.json, shown in the Memory tab, and injected into
/// the system prompt so Vayme remembers across sessions. This is the client-side
/// equivalent of the Mac app's server-side memory.
/// </summary>
public sealed class MemoryStore : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Memories { get; } = new();
    public bool HasMemories => Memories.Count > 0;

    private const int MaxMemories = 60;
    private readonly string _path;

    /// <summary>
    /// When each fact was last confirmed by the user restating it — see <see cref="Add"/>.
    /// Kept alongside <see cref="Memories"/> rather than inside it so the list stays a
    /// collection of strings: it is what the Memory tab binds to and what
    /// <see cref="ContextBlock"/> writes out, and neither has any use for the date.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _reinforced =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One fact as it is stored on disk.</summary>
    private sealed class StoredFact
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("reinforced")] public DateTimeOffset Reinforced { get; set; }
    }

    public MemoryStore()
    {
        _path = AppPaths.InDataDirectory("memories.json");
        Load();
    }

    /// <summary>
    /// Reads the file in either shape. It used to be a bare array of strings and became an
    /// array of objects when facts started carrying a date; a copy written by the older
    /// build — or restored from a backup — still has to open.
    /// </summary>
    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var text = File.ReadAllText(_path);

            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            bool legacy = doc.RootElement.GetArrayLength() > 0
                          && doc.RootElement[0].ValueKind == JsonValueKind.String;

            if (legacy)
            {
                // No dates to inherit, so they all start level and the list's own order is
                // the only ranking there is. Backdated oldest-first, which makes the first
                // eviction under the new rule behave exactly like the old one.
                var items = JsonSerializer.Deserialize<List<string>>(text) ?? new();
                var stamp = DateTimeOffset.UtcNow.AddSeconds(-items.Count);
                foreach (var m in items)
                {
                    Memories.Add(m);
                    _reinforced[m] = stamp;
                    stamp = stamp.AddSeconds(1);
                }

                // Kept once, before the first save rewrites the file in the new shape. If
                // anything about the migration is wrong, the facts are still on disk.
                var backup = _path + ".bak";
                if (items.Count > 0 && !File.Exists(backup)) File.Copy(_path, backup);
                return;
            }

            foreach (var fact in JsonSerializer.Deserialize<List<StoredFact>>(text) ?? new())
            {
                if (string.IsNullOrWhiteSpace(fact.Text)) continue;
                Memories.Add(fact.Text);
                _reinforced[fact.Text] = fact.Reinforced;
            }
        }
        catch { /* start empty */ }
    }

    private void Save()
    {
        try
        {
            var facts = Memories.Select(m => new StoredFact
            {
                Text = m,
                Reinforced = _reinforced.TryGetValue(m, out var at) ? at : DateTimeOffset.UtcNow
            }).ToList();
            File.WriteAllText(_path, JsonSerializer.Serialize(facts));
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Adds a fact if it isn't already known (case-insensitive).
    ///
    /// <para>A fact Vayme is told again is not a duplicate to be dropped on the floor — it is
    /// the user confirming it still holds, which is the only evidence there is that a fact
    /// is worth its place. Restating one moves it to the back of the eviction queue.</para>
    /// </summary>
    public void Add(string memory)
    {
        memory = memory?.Trim() ?? "";
        if (memory.Length == 0) return;

        var existing = Memories.FirstOrDefault(
            m => string.Equals(m, memory, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            _reinforced[existing] = DateTimeOffset.UtcNow;
            Save();
            return;
        }

        Memories.Add(memory);
        _reinforced[memory] = DateTimeOffset.UtcNow;
        Evict();
        Save();
        OnChanged();
    }

    /// <summary>
    /// Drops the fact confirmed least recently when the store is over its cap — not the
    /// oldest one, which is the opposite test. A name, a role, a working language are the
    /// facts that have been around longest precisely because they keep being true, and
    /// evicting by age threw those out first to make room for last Tuesday's trivia.
    /// </summary>
    private void Evict()
    {
        while (Memories.Count > MaxMemories)
        {
            var stalest = Memories.OrderBy(
                m => _reinforced.TryGetValue(m, out var at) ? at : DateTimeOffset.MinValue).First();

            Logger.Log("Memory", $"at capacity ({MaxMemories}) — forgetting \"{stalest}\", " +
                                 $"last confirmed {Since(stalest)}");
            Memories.Remove(stalest);
            _reinforced.Remove(stalest);
        }
    }

    private string Since(string memory)
        => _reinforced.TryGetValue(memory, out var at)
            ? $"{(DateTimeOffset.UtcNow - at).TotalDays:0.#} days ago"
            : "never";

    /// <summary>
    /// Forgets one fact. Matched on the text, which is what identifies a memory here —
    /// <see cref="Add"/> refuses a duplicate, so no two rows can carry the same words.
    /// A miss is a no-op: the extractor quotes a fact back to supersede it and may quote
    /// it imperfectly, and that is not worth throwing over.
    /// </summary>
    public void Remove(string memory)
    {
        var match = Memories.FirstOrDefault(
            m => string.Equals(m, memory, StringComparison.OrdinalIgnoreCase));
        if (match == null) return;

        Memories.Remove(match);
        _reinforced.Remove(match);
        Save();
        OnChanged();
    }

    public void ClearAll()
    {
        Memories.Clear();
        _reinforced.Clear();
        Save();
        OnChanged();
    }

    /// <summary>The block appended to the system prompt so Vayme recalls the user.</summary>
    public string ContextBlock()
    {
        if (Memories.Count == 0) return "";
        return "What you already know about the user (remember this across the conversation):\n"
            + string.Join("\n", Memories.Select(m => "- " + m));
    }

    private void OnChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMemories)));
}
