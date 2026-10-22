using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// WinForms is also referenced (the tray icon), and it has its OWN TextBoxBase — alias so this can never bind
// to the wrong one.
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace Mastermind;

/// <summary>
/// Wheel-to-horizontal scrolling for the fullscreen column-major card list. WPF maps the wheel to VERTICAL
/// scrolling only; that mode disables vertical, so without this the wheel is dead over the list and the bar is
/// the sole way to move it.
///
/// Pure tree/arithmetic logic, deliberately off the window: it is the part that can be wrong in a way you
/// can't see (a mis-walked visual tree silently steals the wheel from a card's text box), so it lives where a
/// layout probe can exercise it directly. The event wiring stays in <see cref="MainWindow"/>.
/// </summary>
public static class WheelScroll
{
    /// <summary>
    /// Where the list should scroll to for a wheel notch. Wheel DOWN reports a negative delta and must move the
    /// list RIGHT (further into the columns), so the delta is subtracted.
    /// </summary>
    public static double NextHorizontalOffset(double current, int delta) => current - delta;

    /// <summary>
    /// Is the pointer over a nested scroller that still has somewhere to go? Walks from the wheel event's
    /// source up to — but not including — <paramref name="boundary"/>.
    ///
    /// This exists because the handler must tunnel (the boundary ScrollViewer consumes the wheel before it
    /// bubbles, so a bubbling handler would never run), which means we see the event BEFORE the card's own
    /// detail / todo boxes do. Without this check, spinning the wheel over a card's text would slide the whole
    /// list sideways instead of scrolling that text. A scroller that is fully scrolled (or whose content fits)
    /// reports <c>ScrollableHeight == 0</c> and correctly yields the wheel to the list.
    ///
    /// The walk prefers the VISUAL tree — a control's own scroller lives inside its template and is invisible
    /// to the logical tree — and falls back to the logical parent for non-visual sources (e.g. a text
    /// <c>Run</c>), which simply terminates without a hit.
    ///
    /// A text box is checked via its OWN <see cref="TextBoxBase.ExtentHeight"/>, not by looking for a
    /// ScrollViewer, because that scroller sits INSIDE the control's template — it is a descendant of the text
    /// box, never an ancestor of it. An ancestor-only walk therefore misses it whenever the event source is the
    /// text box itself rather than its inner view, and the list silently steals the wheel from card text.
    /// Measured, not assumed: a layout probe over a real scrollable TextBox failed the ancestor-only version.
    /// </summary>
    public static bool HasScrollableInnerScroller(DependencyObject node, ScrollViewer boundary)
    {
        while (node != null && node != boundary)
        {
            if (node is ScrollViewer sv && sv != boundary && sv.ScrollableHeight > 0) return true;
            if (node is TextBoxBase tb && tb.ExtentHeight > tb.ViewportHeight + 0.5) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
