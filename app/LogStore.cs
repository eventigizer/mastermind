using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using BrushConverter = System.Windows.Media.BrushConverter;

namespace Mastermind;

/// <summary>Severity of an activity-log line — drives its colour via the table in <see cref="LogStore"/>.</summary>
internal enum LogKind { Event, Drop, Info, Warn }

/// <summary>One line in the activity log: a timestamp, a message, and a kind→colour. Public so the XAML
/// DataTemplate can bind it (same as <see cref="AgentEvent"/>).</summary>
public sealed class LogLine
{
    public string Time { get; init; } = "";
    public string Text { get; init; } = "";
    public Brush Tint { get; init; } = System.Windows.Media.Brushes.Gray;
}

/// <summary>
/// The activity log shown in the panel's bottom strip: a bounded, newest-at-bottom list of what's coming in
/// (each hook event + what Mastermind did with it) plus listener lifecycle. Plain + UI-thread-only like
/// <see cref="SessionStore"/> — every <see cref="Add"/> must be on the dispatcher (App.OnEvent already is).
/// </summary>
internal sealed class LogStore
{
    public ObservableCollection<LogLine> Lines { get; } = new();

    /// <summary>Fired after a line is appended — the view auto-scrolls to the bottom.</summary>
    public event Action? Added;

    /// <summary>Fired for a line the user must not miss (Warn only) — the view pops a transient toast. The Event
    /// firehose, Info lifecycle, and muted Drops stay in the log strip and never toast.</summary>
    public event Action<string>? Toast;

    private const int Max = 200;   // ring cap: drop the oldest lines past this

    /// <summary>Every line also lands in this file — <c>&lt;logDir&gt;/mastermind_activity.log</c>, started fresh at each
    /// launch — so a run reads without the panel: a <c>--background</c> start shows none, and a test restart checks
    /// its outcome here. Null: no file.</summary>
    private readonly string _file;

    public LogStore(string file = null)
    {
        _file = file;
        if (_file == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, "");
        }
        catch { /* the file copies the strip — never a reason to fail startup */ }
    }

    public void Add(LogKind kind, string text)
    {
        var time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);   // `:` in every culture — check_restore.js reads it
        Lines.Add(new LogLine { Time = time, Text = text, Tint = Tints[kind] });
        while (Lines.Count > Max) Lines.RemoveAt(0);
        if (_file != null)
            try { File.AppendAllText(_file, $"{time} {kind,-5} {text}\n"); }
            catch { /* best effort, like the strip itself */ }
        Added?.Invoke();
        if (kind == LogKind.Warn) Toast?.Invoke(text);   // surface it as a banner, not just a log line
    }

    public void Clear() => Lines.Clear();

    private static SolidColorBrush Solid(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    // kind → line colour. Event = calm neutral (the firehose), Drop = muted (ignored/hidden),
    // Info = green (lifecycle), Warn = amber (something off).
    private static readonly Dictionary<LogKind, Brush> Tints = new()
    {
        [LogKind.Event] = Solid("#9FB0C4"),
        [LogKind.Drop]  = Solid("#6E7787"),
        [LogKind.Info]  = Solid("#5FB87A"),
        [LogKind.Warn]  = Solid("#E0A458"),
    };
}
