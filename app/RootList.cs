using System.Text.Json.Serialization;

namespace Mastermind;

/// <summary>A JSON file listing project roots under <c>roots</c> — the roots Mastermind wrote to, so each start can
/// revisit them: <c>watched.json</c> (the watch command's hook entries) and <c>handoffs.json</c> (the handoff
/// skills).</summary>
internal static class RootList
{
    private sealed class Doc
    {
        [JsonPropertyName("roots")] public List<string> Roots { get; set; } = new();
    }

    public static List<string> Read(string path) => Json.Load<Doc>(path)?.Roots ?? new List<string>();

    /// <summary>The root added (on) or taken out, matched on its normalized key, and the file saved atomically.</summary>
    public static void Set(string path, string root, bool on)
    {
        var key = Paths.Key(root);
        var roots = Read(path).Where(r => Paths.Key(r) != key).ToList();
        if (on) roots.Add(root);
        Json.SaveAtomic(path, new Doc { Roots = roots });
    }
}
