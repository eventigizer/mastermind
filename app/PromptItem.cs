using System.ComponentModel;
using System.IO;
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using BrushConverter = System.Windows.Media.BrushConverter;

namespace Mastermind;

/// <summary>Folder = state. Source (outbox/, needs transport) → Waiting (inbox/, dispatchable) →
/// InProcess (processing/, an agent has it). Parked is Waiting with the dest window closed (dimmed, disabled).</summary>
public enum PromptState { Source, Waiting, InProcess }

/// <summary>One in-flight cross-agent prompt file, keyed on its full current path. State is derived from which
/// folder it sits in; DestWindowOpen (set by the scan) splits Waiting into ready vs parked for the view.</summary>
public sealed class PromptItem : INotifyPropertyChanged
{
    public string FullPath { get; init; } = "";      // current location (outbox / inbox / processing)
    public string FileName { get; init; } = "";
    public string Src { get; init; } = "";
    public string Dst { get; init; } = "";
    public string DestRoot { get; init; } = "";      // full path; "" when the DST token didn't resolve
    public string SourceRoot { get; init; } = "";    // the project the file was found under
    public PromptState State { get; init; } = PromptState.Source;

    // A BUNDLE: the round travels as a DIRECTORY named by the same grammar minus the extension, because it
    // carries bytes that are not prose. Every mechanic above is keyed on the PATH rather than on file-ness, so
    // this changes exactly two things — which filesystem call moves or deletes the entry (PromptDispatcher),
    // and how completeness is decided (PromptStore: a directory has no size, so the rename into the root IS
    // the completion signal). Contents are never enumerated, filtered or inspected anywhere: a bundle carrying
    // only assets is legal, and the receiving agent asks the user what it is for rather than the relay parsing
    // an answer out of it.
    public bool IsDirectory { get; init; }

    // Set by the scan each tick: is the destination project's VS Code window open? (Waiting only.)
    private bool _destWindowOpen;
    public bool DestWindowOpen
    {
        get => _destWindowOpen;
        // DestWindowOpen is the ONLY thing that mutates in place on a retained row (State is init; folder moves
        // are remove+insert), so the parked↔ready transition MUST raise every derived prop — incl. StateText
        // ("WAITING"/"PARKED") and Accent (amber/grey), or the pill lies while the buttons are correct (finding D).
        set { if (_destWindowOpen == value) return; _destWindowOpen = value;
              Raise(nameof(DestWindowOpen)); Raise(nameof(IsParked)); Raise(nameof(RowOpacity));
              Raise(nameof(DispatchEnabled)); Raise(nameof(ParkHintVisibility));
              Raise(nameof(StateText)); Raise(nameof(Accent)); }
    }

    // Set by the scan each tick for InProcess rows: is the file's owner session still a live agent? Mutates in
    // place on a retained row like DestWindowOpen, so the orphan↔processing flip must raise every derived prop —
    // DeleteVisibility, StateText, Accent — or the pill/button lie. Non-InProcess rows keep the default (true).
    private bool _ownerAlive = true;
    public bool OwnerAlive
    {
        get => _ownerAlive;
        set { if (_ownerAlive == value) return; _ownerAlive = value;
              Raise(nameof(OwnerAlive)); Raise(nameof(IsOrphan)); Raise(nameof(DeleteVisibility));
              Raise(nameof(StateText)); Raise(nameof(Accent)); }
    }

    // Set by the scan each tick for InProcess rows: does the owner map hold any entry for the file? None means a file
    // this machine never dispatched — another machine's, brought here by git — and only such a file offers back to
    // inbox: an ORPHAN whose owner's session merely ended was usually read already, and reopening that session picks
    // it back up. Non-InProcess rows keep the default (true).
    private bool _ownerRecorded = true;
    public bool OwnerRecorded
    {
        get => _ownerRecorded;
        set { if (_ownerRecorded == value) return; _ownerRecorded = value;
              Raise(nameof(OwnerRecorded)); Raise(nameof(BackVisibility)); }
    }

    // Set by the scan for InProcess rows: the dispatch only typed its text (debug port off) and no message of its
    // session carries it yet. Such a row offers back to inbox and never delete, whatever its owner's state — the
    // handoff was never read (user decision).
    private bool _typed;
    public bool Typed
    {
        get => _typed;
        set { if (_typed == value) return; _typed = value;
              Raise(nameof(Typed)); Raise(nameof(BackVisibility)); Raise(nameof(DeleteVisibility));
              Raise(nameof(StateText)); Raise(nameof(Accent)); }
    }

    public string RouteText => $"{Src} → {Dst}";
    public bool Unresolved => string.IsNullOrEmpty(DestRoot);   // DST token didn't map — flagged, not dispatchable

    // ---- view state -------------------------------------------------------
    public bool IsParked => State == PromptState.Waiting && !DestWindowOpen;
    public bool IsOrphan => State == PromptState.InProcess && !OwnerAlive;   // in processing/, but its agent is gone/unknown
    public double RowOpacity => IsParked || Unresolved ? 0.45 : 1.0;   // dimmed = present-but-not-actionable (unresolved/parked)
    public bool DispatchEnabled => State == PromptState.Waiting && DestWindowOpen && !Unresolved;

    public System.Windows.Visibility MoveVisibility => State == PromptState.Source
        ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public bool MoveEnabled => State == PromptState.Source && !Unresolved;   // an unmapped DST can't transport (finding K)
    public System.Windows.Visibility DispatchVisibility => State == PromptState.Waiting
        ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    // Delete is the "cancel this prompt" escape hatch, shown in every state EXCEPT a LIVE-owned PROCESSING row:
    // while an agent actively owns the file yanking it is unsafe. An ORPHAN (InProcess + owner gone) shows it again.
    public System.Windows.Visibility DeleteVisibility => State == PromptState.InProcess && (OwnerAlive || Typed)
        ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public System.Windows.Visibility BackVisibility => State == PromptState.InProcess && (!OwnerRecorded || Typed)
        ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility ParkHintVisibility => IsParked
        ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public string ParkHint => $"open {Paths.Leaf(DestRoot)} to dispatch";
    public string StateText => Unresolved ? "UNKNOWN DEST"
        : State switch { PromptState.Source => "TO SEND",
                         PromptState.InProcess => Typed ? "TYPED" : OwnerAlive ? "PROCESSING" : "ORPHAN",
                         _ => DestWindowOpen ? "WAITING" : "PARKED" };

    public string Key => Paths.Key(FullPath);   // identity for the store diff

    private static SolidColorBrush F(string hex) { var b=(SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
    public Brush Accent => F(
        State == PromptState.InProcess ? (OwnerAlive || Typed ? "#6E7E8F" : "#E0524D")   // processing or typed = muted steel-grey (matches Working), orphan = danger red (NOT the terracotta brand)
        : IsParked || Unresolved ? "#8893A6" : "#E0A458");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}
