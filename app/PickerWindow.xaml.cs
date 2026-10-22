using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace Mastermind;

/// <summary>
/// A confirm-before-act picker: a filterable list on the left, the selected item's full text on the right, and one
/// action button. The card's go / rec pattern applied to a list: a click SELECTS a row and arms it, and only a
/// second click on that same row — or the button, or Enter — acts, so a slipped click never sends anything. Used
/// for the card's canned prompts and for the inbox's hand-to-an-agent dispatch.
///
/// MODELESS on purpose (<see cref="Window.Show"/>, never ShowDialog): any click outside closes it, and a modal
/// dialog disables its owner, so a click on the panel would only flash the dialog instead of dismissing it. Losing
/// activation IS the outside click (<see cref="Window.Deactivated"/>); the pick comes back through the callback.
/// </summary>
public partial class PickerWindow : Window
{
    private readonly List<PromptPreset> _all;
    private readonly Action<PromptPreset> _onPick;
    private bool _closing;   // Close() itself deactivates the window — one close, never a re-entrant second
    private PromptPreset _armed;   // the row the last MOUSE click selected — a second click on it acts

    public PickerWindow(string title, IEnumerable<PromptPreset> items, string actionText, Action<PromptPreset> onPick)
    {
        InitializeComponent();
        TitleText.Text = title;
        ActionButton.Content = actionText;
        _all = items.ToList();
        _onPick = onPick;
        ApplyFilter();
        Loaded += (_, _) => Filter.Focus();
        Deactivated += (_, _) => CloseOnce();
        Closing += (_, _) => _closing = true;
    }

    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        int on = 1, round = 2;
        Native.DwmSetWindowAttribute(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
        Native.DwmSetWindowAttribute(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
    }

    /// <summary>Open at the FULL height of <paramref name="anchor"/>'s monitor (a small inset keeps the rounded corners
    /// visible), fixed width, horizontally centred on the anchor and clamped into the work area so a card near an
    /// edge never pushes it half off-screen. Full height shows the whole list without scrolling.</summary>
    public void PlaceNear(FrameworkElement anchor)
    {
        const double inset = 8;
        var src = PresentationSource.FromVisual(anchor);
        if (src?.CompositionTarget == null) { WindowStartupLocation = WindowStartupLocation.CenterOwner; return; }
        var fromDevice = src.CompositionTarget.TransformFromDevice;
        var dev = anchor.PointToScreen(new System.Windows.Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2));
        var wa = Screen.FromPoint(new System.Drawing.Point((int)dev.X, (int)dev.Y)).WorkingArea;   // device px
        var at = fromDevice.Transform(dev);
        var tl = fromDevice.Transform(new System.Windows.Point(wa.Left, wa.Top));
        var br = fromDevice.Transform(new System.Windows.Point(wa.Right, wa.Bottom));
        Left   = Math.Max(tl.X + inset, Math.Min(at.X - Width / 2, br.X - Width - inset));
        Top    = tl.Y + inset;
        Height = Math.Max(MinHeight, br.Y - tl.Y - 2 * inset);
    }

    /// <summary>Narrow the list to labels or texts containing the filter. With a filter typed, the first match is
    /// pre-selected so type-then-Enter works; with none, nothing is — the first choice is always explicit.</summary>
    private void ApplyFilter()
    {
        var q = Filter.Text.Trim();
        var shown = q.Length == 0 ? _all
            : _all.Where(p => p.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                           || p.Text.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        ItemList.ItemsSource = shown;
        ItemList.SelectedIndex = q.Length > 0 && shown.Count > 0 ? 0 : -1;
        _armed = null;   // a pre-selected match is NOT armed — one click on it must only arm, never act
        Placeholder.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Filter_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    /// <summary>↑ / ↓ in the filter box move the list selection, so the keyboard never has to leave the box.</summary>
    private void Filter_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        int n = ItemList.Items.Count;
        if (n == 0) return;
        if (e.Key == Key.Down) { ItemList.SelectedIndex = Math.Min(n - 1, ItemList.SelectedIndex + 1); e.Handled = true; }
        else if (e.Key == Key.Up) { ItemList.SelectedIndex = Math.Max(0, ItemList.SelectedIndex - 1); e.Handled = true; }
        if (e.Handled)
        {
            _armed = null;   // a row reached by the keyboard is acted on with Enter, never by a single click
            if (ItemList.SelectedItem != null) ItemList.ScrollIntoView(ItemList.SelectedItem);
        }
    }

    /// <summary>
    /// The click half of the go / rec pattern: the ListBox has already selected the row on mouse-down, so a click on
    /// a row that is NOT the armed one arms it, and a click on the armed, still-selected row acts — a double-click
    /// included, a slower click-read-click too. Only a mouse click arms (see <see cref="_armed"/>).
    /// </summary>
    private void Item_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBoxItem)?.DataContext is not PromptPreset p) return;
        if (ReferenceEquals(p, _armed) && ReferenceEquals(ItemList.SelectedItem, p)) { e.Handled = true; Act(p); return; }
        _armed = p;
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var sel = ItemList.SelectedItem as PromptPreset;
        Preview.Text = sel?.Text ?? "";
        ActionButton.IsEnabled = sel != null;
    }

    // Close FIRST, then hand the pick back: the caller's work (a CDP send, a file move) never runs under an open
    // picker, and a deactivation it causes can't re-enter the close.
    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is PromptPreset p) Act(p);
    }

    private void Act(PromptPreset p)
    {
        if (_closing) return;   // a late click on a closing picker acts on nothing
        CloseOnce();
        _onPick(p);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseOnce();
}
