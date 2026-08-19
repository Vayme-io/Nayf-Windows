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
/// One saved agent task the user can reopen and continue. Mirrors <c>SavedAgentTask</c>
/// in the Mac's <c>AgentTaskStore.swift</c>.
/// </summary>
public sealed class SavedAgentTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The task's mission label, e.g. "Rewriting CV from resume".</summary>
    public string Title { get; set; } = "";

    /// <summary>The latest outcome — the text Nayf spoke on the most recent turn.</summary>
    public string Summary { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Bumped every time the task is continued, so the list sorts newest-activity first.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The conversation thread for THIS task, replayed as context when it is resumed.
    /// Kept separate from the global rolling history so a follow-up picks up the task
    /// rather than whatever was said in between.
    /// </summary>
    public List<ConversationTurn> History { get; set; } = new();

    /// <summary>
    /// The tile/card accent, derived from the id so a task keeps its colour across
    /// launches and reads identically in the grid and on the floating card.
    /// </summary>
    [JsonIgnore]
    public System.Drawing.Color Accent => NayfAgentPalette.ColorFor(Id);

    // MARK: - Bindings for the Agents grid
    //
    // The tiles are painted straight from the model rather than through converters,
    // which is how the rest of the panel's lists are bound. Only ever read by XAML on
    // the UI thread, which is where a WinUI brush has to be built.

    [JsonIgnore]
    public Microsoft.UI.Xaml.Media.Brush AccentBrush => Brush(255);

    /// <summary>The tile's wash — the accent at the strength the Mac's grid uses at rest.</summary>
    [JsonIgnore]
    public Microsoft.UI.Xaml.Media.Brush TileFillBrush => Brush(28);

    [JsonIgnore]
    public Microsoft.UI.Xaml.Media.Brush TileBorderBrush => Brush(56);

    /// <summary>The little rounded icon square at the top of the tile.</summary>
    [JsonIgnore]
    public Microsoft.UI.Xaml.Media.Brush ChipFillBrush => Brush(48);

    private Microsoft.UI.Xaml.Media.SolidColorBrush Brush(byte alpha)
    {
        var c = Accent;
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(alpha, c.R, c.G, c.B));
    }
}

/// <summary>
/// The single source of truth for a task's accent colour. Both the Agents grid and the
/// floating result card ask this, so one task is one colour everywhere. Deterministic on
/// the id, so the colour survives a restart.
/// </summary>
public static class NayfAgentPalette
{
    private static readonly System.Drawing.Color[] Colors =
    [
        System.Drawing.Color.FromArgb(92, 140, 255),   // blue
        System.Drawing.Color.FromArgb(153, 107, 250),  // purple
        System.Drawing.Color.FromArgb(250, 112, 168),  // pink
        System.Drawing.Color.FromArgb(255, 148, 71),   // orange
        System.Drawing.Color.FromArgb(41, 204, 179),   // teal
        System.Drawing.Color.FromArgb(92, 199, 117),   // green
        System.Drawing.Color.FromArgb(250, 184, 66),   // amber
        System.Drawing.Color.FromArgb(107, 173, 250),  // sky
        System.Drawing.Color.FromArgb(245, 107, 107),  // red
        System.Drawing.Color.FromArgb(133, 138, 250),  // indigo
        System.Drawing.Color.FromArgb(71, 209, 143),   // mint
        System.Drawing.Color.FromArgb(230, 117, 230),  // magenta
    ];

    /// <summary>
    /// Mixes four spread-out bytes of the id with primes, the same shape as the Mac's
    /// version — consecutive ids would otherwise land on neighbouring colours, and a
    /// user's tasks are all created moments apart.
    /// </summary>
    public static System.Drawing.Color ColorFor(Guid taskId)
    {
        var bytes = taskId.ToByteArray();
        int seed = bytes[0] * 7 + bytes[5] * 13 + bytes[10] * 17 + bytes[15] * 23;
        return Colors[seed % Colors.Length];
    }
}

/// <summary>
/// Local-only store of the agent tasks Nayf has run, so the user can reopen one later and
/// CONTINUE it. Each saved task carries its own conversation thread, so a follow-up resumes
/// exactly where that task left off instead of using the global rolling history.
///
/// <para>Persisted to %LOCALAPPDATA%\Nayf\agent-tasks.json and never uploaded. Mirrors the
/// <see cref="MemoryStore"/> pattern: an instance owned by <see cref="CompanionManager"/>
/// that the response pipeline mutates and the Agents page reads.</para>
/// </summary>
public sealed class AgentTaskStore : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Backing collection, in save order. The UI reads <see cref="TasksNewestFirst"/>.</summary>
    public ObservableCollection<SavedAgentTask> Tasks { get; } = new();

    public bool HasTasks => Tasks.Count > 0;

    /// <summary>Caps so the file stays small — it is rewritten whole on every change.</summary>
    private const int MaxTasks = 40;
    private const int MaxHistoryPerTask = 6;

    private readonly string _path;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    public AgentTaskStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nayf");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "agent-tasks.json");
        Load();
    }

    // MARK: - Read

    /// <summary>Saved tasks ordered by most recent activity first — what the Agents grid shows.</summary>
    public IReadOnlyList<SavedAgentTask> TasksNewestFirst =>
        Tasks.OrderByDescending(t => t.UpdatedAt).ToList();

    public SavedAgentTask? Task(Guid id) => Tasks.FirstOrDefault(t => t.Id == id);

    // MARK: - Mutation

    /// <summary>Saves a newly-finished agent task, snapshotting its conversation thread.</summary>
    public SavedAgentTask CreateTask(string title, string summary, IEnumerable<ConversationTurn> history)
    {
        var now = DateTimeOffset.Now;
        var task = new SavedAgentTask
        {
            Id = Guid.NewGuid(),
            Title = title,
            Summary = summary,
            CreatedAt = now,
            UpdatedAt = now,
            History = TrimHistory(history.Select(Storable).ToList())
        };

        Tasks.Add(task);
        while (Tasks.Count > MaxTasks) Tasks.RemoveAt(0);
        Save();
        Logger.Log("AgentTasks", $"+ \"{title}\"");
        OnChanged();
        return task;
    }

    /// <summary>
    /// Appends a continued turn to an existing task's thread and refreshes its summary.
    /// No-op if the task is gone — the user can delete it from the grid mid-follow-up.
    /// </summary>
    public void AppendTurn(Guid taskId, ConversationTurn turn, string summary)
    {
        var task = Task(taskId);
        if (task == null) return;

        task.History.Add(Storable(turn));
        task.History = TrimHistory(task.History);
        task.Summary = summary;
        task.UpdatedAt = DateTimeOffset.Now;
        Save();
        Logger.Log("AgentTasks", $"~ \"{task.Title}\"");
        OnChanged();
    }

    /// <summary>Removes one task — the grid tile's delete button.</summary>
    public void Remove(Guid id)
    {
        var task = Task(id);
        if (task == null) return;

        Tasks.Remove(task);
        Save();
        Logger.Log("AgentTasks", "- removed 1");
        OnChanged();
    }

    public void ClearAll()
    {
        if (Tasks.Count == 0) return;

        Tasks.Clear();
        Save();
        Logger.Log("AgentTasks", "- cleared all");
        OnChanged();
    }

    // MARK: - Persistence

    /// <summary>
    /// A turn as it goes to disk. The image is dropped rather than carried: turns are
    /// serialised as JSON, so a screenshot would land in the file as base64 and a handful
    /// of tasks would run to megabytes. Resuming needs what was said, not what was seen.
    /// </summary>
    private static ConversationTurn Storable(ConversationTurn turn) =>
        new(turn.UserTranscript, turn.AssistantResponse);

    private static List<ConversationTurn> TrimHistory(List<ConversationTurn> history)
    {
        if (history.Count <= MaxHistoryPerTask) return history;
        return history.Skip(history.Count - MaxHistoryPerTask).ToList();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var items = JsonSerializer.Deserialize<List<SavedAgentTask>>(File.ReadAllText(_path));
            if (items == null) return;
            foreach (var task in items) Tasks.Add(task);
        }
        catch { /* start empty */ }
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(Tasks.ToList(), SerializerOptions)); }
        catch { /* non-fatal */ }
    }

    private void OnChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTasks)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TasksNewestFirst)));
    }
}
