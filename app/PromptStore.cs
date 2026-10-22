using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace Mastermind;

/// <summary>The always-visible prompt band's collection + the filesystem reconcile. Runs on the UI thread from
/// the poll tick (same assumption as SessionStore). Folder = state; the collection is rebuilt-in-place from
/// disk each tick — files, and bundle directories in a project the manifest lists — so a consumed/moved entry just drops off. Auto-move
/// (default on) transports a source entry once it is known complete: a FILE by the size-stable-for-two-ticks
/// test, a BUNDLE by contract, since its rename into the root is the completion signal. Dispatch + delete are
/// driven by the view via PromptDispatcher; the first tick hands the address book to its staging clean.</summary>
public sealed class PromptStore : INotifyPropertyChanged
{
    public ObservableCollection<PromptItem> Items { get; } = new();

    // Notifying so the per-row `move ▸` button (hidden while AutoMove is on) reacts the instant the checkbox
    // toggles — the manual move conflicts with auto-transport, so only one of the two is ever offered.
    private bool _autoMove = true;
    public bool AutoMove
    {
        get => _autoMove;
        set { if (_autoMove == value) return; _autoMove = value; PropertyChanged?.Invoke(this, new(nameof(AutoMove))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly Dictionary<string, (long size, int tick)> _stable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoMoved = new(StringComparer.OrdinalIgnoreCase);   // keys auto-transport already tried once
    private int _tick;
    private readonly Action<PromptItem> _autoTransport;   // set by App to PromptDispatcher.Transport
    private readonly Action<IReadOnlyList<ProjectEntry>, bool> _cleanStaging;   // set by App to PromptDispatcher.CleanStaging
    private readonly Action<string, string, string> _moveAutoAllow;   // set by App to PromptDispatcher.MoveAutoAllow
    private bool _stagingChecked;                          // once per run — the clean is start-only (user decision)

    // internal: ProjectEntry is internal, and a public constructor taking it is CS0051.
    internal PromptStore(Action<PromptItem> autoTransport, Action<IReadOnlyList<ProjectEntry>, bool> cleanStaging,
        Action<string, string, string> moveAutoAllow)
    { _autoTransport = autoTransport; _cleanStaging = cleanStaging; _moveAutoAllow = moveAutoAllow; }

    /// <summary>One tick: enumerate every listed project's outbox + inbox + processing folder, build the
    /// desired PromptItem set, diff into Items in place, and auto-transport any stable source file. openKeys =
    /// the reconciler's open-project-key set (marks Waiting rows ready vs parked); liveSids = every live agent's
    /// session id (marks a PROCESSING row owned-and-alive vs orphaned).</summary>
    public void Tick(HashSet<string> openKeys, HashSet<string> liveSids)
    {
        // The start-time staging clean: once per run, on the first tick, relay on or off — the writer fills staging
        // either way.
        if (!_stagingChecked) { _stagingChecked = true; var (book, _, failed) = PromptRoute.Projects(); _cleanStaging(book, failed); }
        // The relay is opt-in (config's handoffs.enabled): off, nothing is collected or moved, the owner map is left as
        // it is, and the empty list keeps the inbox card hidden.
        if (!Config.Current.HandoffsEnabled) return;
        _tick++;
        var (projects, manifest, manifestFailed) = PromptRoute.Projects();
        var want = new Dictionary<string, PromptItem>(StringComparer.OrdinalIgnoreCase);
        var transportNow = new List<PromptItem>();

        foreach (var proj in projects)
        {
            var root = proj?.Root;
            if (string.IsNullOrEmpty(root)) continue;
            // A folder is a handoff only in a project the manifest lists — bundles come from manifest projects alone.
            bool folders = HookEntries.IsListed(manifest, root);
            Collect(root, PromptRoute.OutboxDir(root), PromptState.Source, projects, openKeys, liveSids, want, transportNow, folders);
            Collect(root, PromptRoute.InboxDir(root), PromptState.Waiting, projects, openKeys, liveSids, want, null, folders);
            Collect(root, PromptRoute.ProcessingDir(root), PromptState.InProcess, projects, openKeys, liveSids, want, null, folders);
        }

        // Diff into Items in place (keep instances so bindings don't churn). Add new, update the in-place flags
        // (DestWindowOpen for Waiting↔Parked, OwnerAlive for Processing↔Orphan, OwnerRecorded for back to inbox),
        // drop gone.
        var byKey = new Dictionary<string, PromptItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in Items) byKey[it.Key] = it;
        foreach (var (k, w) in want)
            if (byKey.TryGetValue(k, out var cur)) { cur.DestWindowOpen = w.DestWindowOpen; cur.OwnerAlive = w.OwnerAlive; cur.OwnerRecorded = w.OwnerRecorded; cur.Typed = w.Typed; }
            else Items.Add(w);
        for (int i = Items.Count - 1; i >= 0; i--) if (!want.ContainsKey(Items[i].Key)) Items.RemoveAt(i);

        // Name order — A→Z by filename, the full path breaking a tie — so same-route handoffs sit together and
        // neither a folder move (a new key) nor an app restart reshuffles the list. Move, never rebuild.
        var sorted = Items.OrderBy(it => it.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(it => it.Key, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = Items.IndexOf(sorted[i]);
            if (cur != i) Items.Move(cur, i);
        }

        // GC the owner map to the processing files we just saw — a consumed/deleted prompt's owner entry goes with
        // it, so the map only ever tracks live processing files (and never re-orphans work across a relaunch).
        var inprocessKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, w) in want) if (w.State == PromptState.InProcess) inprocessKeys.Add(k);
        // A tick whose manifest read failed saw a short list — cleaning against it would drop every live owner.
        if (!manifestFailed) PromptOwnerStore.RetainOnly(inprocessKeys);

        // stability + attempt GC: forget files no longer present as source candidates (so a re-appearing
        // same-named file is treated fresh, and a resolved collision can auto-move on its next sighting).
        foreach (var k in _stable.Keys.ToList()) if (!want.ContainsKey(k) || want[k].State != PromptState.Source) _stable.Remove(k);
        foreach (var k in _autoMoved.ToList()) if (!want.ContainsKey(k) || want[k].State != PromptState.Source) _autoMoved.Remove(k);

        // Auto-transport each stable source ONCE (HashSet.Add==false skips a re-attempt): a refused collision or a
        // transient IO failure must not re-fire the move every tick. The manual `move ▸` button always retries.
        if (AutoMove) foreach (var it in transportNow) if (_autoMoved.Add(it.Key)) _autoTransport(it);
    }

    // (dismiss removed — the card is the master list of every on-disk prompt; you act on a row via
    // move/dispatch/delete, there is no hide-but-keep state. Finding L.)

    private void Collect(string root, string dir, PromptState state, IReadOnlyList<ProjectEntry> projects,
        HashSet<string> openKeys, HashSet<string> liveSids, Dictionary<string, PromptItem> want, List<PromptItem> transportNow,
        bool folders)
    {
        if (!Directory.Exists(dir)) return;

        // One row builder for both shapes. A file and a bundle differ ONLY in IsDirectory and in how
        // completeness is decided, so everything else — DestRoot resolution, orphan detection, the want key —
        // must not acquire a second copy that can drift out of step with this one.
        //
        // Orphan detection (InProcess only): protected (alive) iff the owner is PENDING (a new-session dispatch
        // whose id hasn't resolved yet) OR the owner sid is a currently-live agent. No entry, or a dead sid ⇒
        // orphan ⇒ deletable — only Mastermind here, or a git checkout from another machine, puts a file there, and
        // neither is a live owner here.
        PromptItem Row(string path, string fn, PromptRoute.Parsed p, string key, bool isDirectory)
        {
            var destRoot = PromptRoute.DestRoot(p.Dst, projects) ?? "";
            bool ownerAlive = true;
            bool ownerRecorded = true;
            bool typed = false;
            if (state == PromptState.InProcess)
            {
                var owner = PromptOwnerStore.Find(key);
                // A typed dispatch counts as sent once its file name shows in a message — never off a busy or waiting
                // status, which a startup dialog or the person's own turn raises without it — and the session that sent
                // it owns it: the one it was handed to, or the project's running session whose message names the file,
                // when the 20 s search never found the new one (a slow start) or took another fresh session in that window.
                // A `new` dispatch's auto-allow follows it there.
                if (owner?.Typed == true
                    && LiveSessions.RunningIn(root).Select(r => r.SessionId).Prepend(owner.Value.Sid).Distinct()
                           .Where(s => s != PromptOwnerStore.Pending).FirstOrDefault(s => Sessions.UserSaid(s, root, fn)) is { } sender)
                {
                    PromptOwnerStore.Set(key, sender);
                    if (owner.Value.Spawned && sender != owner.Value.Sid) _moveAutoAllow(owner.Value.Sid, sender, root);
                    owner = (sender, false, false);
                }
                var ownerSid = owner?.Sid;
                ownerAlive = ownerSid == PromptOwnerStore.Pending || (ownerSid != null && liveSids.Contains(ownerSid));
                ownerRecorded = owner != null;
                typed = owner?.Typed == true;
            }
            return new PromptItem {
                FullPath = path, FileName = fn, Src = p.Src, Dst = p.Dst,
                DestRoot = destRoot, SourceRoot = root, State = state, IsDirectory = isDirectory,
                DestWindowOpen = !string.IsNullOrEmpty(destRoot) && openKeys.Contains(Paths.Key(destRoot)),
                OwnerAlive = ownerAlive, OwnerRecorded = ownerRecorded, Typed = typed,
            };
        }

        foreach (var path in Directory.GetFiles(dir, "*.md"))
        {
            var fn = Path.GetFileName(path);
            var p = PromptRoute.Parse(fn);
            if (p is null) continue;
            var key = Paths.Key(path);
            var it = Row(path, fn, p.Value, key, false);
            want[key] = it;

            if (state == PromptState.Source && transportNow != null && !string.IsNullOrEmpty(it.DestRoot))
            {
                long size; try { size = new FileInfo(path).Length; } catch { continue; }
                if (_stable.TryGetValue(key, out var s) && s.size == size && s.tick < _tick) transportNow.Add(it);   // stable ≥2 ticks
                else _stable[key] = (size, _tick);
            }
        }

        // BUNDLES — a round carrying bytes that are not prose, travelling as a DIRECTORY named by the same
        // grammar minus the extension. Every non-matching name falls out at the parse, so staging/, inbox/,
        // processing/ and every scratch directory are excluded structurally rather than by a maintained list. Only in a
        // project the manifest lists: elsewhere a folder named like a handoff is someone's work in progress.
        if (folders) foreach (var path in Directory.GetDirectories(dir))
        {
            var fn = Path.GetFileName(path);
            var p = PromptRoute.ParseDirectory(fn);
            if (p is null) continue;
            var key = Paths.Key(path);
            var it = Row(path, fn, p.Value, key, true);
            want[key] = it;

            // The stability test is deliberately SKIPPED, and skipping it is not a shortcut. A directory has no
            // size, so "same length for two ticks" has no answer here — and FileInfo.Length on one THROWS, into
            // a catch that would drop the row every tick with no signal at all. What replaces it is the rename:
            // a bundle composes under .mastermind/staging/<name>/ and is renamed into the outbox only when finished, so a
            // grammar-named directory sitting here is complete BY CONTRACT and transports on sight.
            if (state == PromptState.Source && transportNow != null && !string.IsNullOrEmpty(it.DestRoot))
                transportNow.Add(it);
        }
    }
}
