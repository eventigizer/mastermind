using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;

namespace Mastermind;

/// <summary>
/// Mastermind's own dialog, in place of the Windows message box: a title, the text and one to three buttons in
/// the panel's colours, modal over the panel when it shows, else centred on the screen. <see cref="Ask"/> is a
/// confirmation — the action button accented, Enter on it (or on the button the keyboard is on), Esc the cancel; <see cref="Notice"/> one OK;
/// <see cref="Tell"/> a refused or failed click — what happened, why and what to do next — so a failure stays on
/// screen until read.
/// </summary>
public partial class MastermindDialog : Window
{
    private readonly int _enter, _escape;
    private int _picked = -1;

    private MastermindDialog(string title, string text, string[] buttons, int accent, int cancel)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = text;
        _enter = accent;
        _escape = cancel;
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var b = new Button { Content = buttons[i], Style = (Style)FindResource("GhostButton"), Margin = new Thickness(8, 0, 0, 0) };
            if (i == accent) { b.Foreground = (Brush)FindResource("Accent"); b.FontWeight = FontWeights.SemiBold; }
            b.Click += (_, _) => Pick(index);
            Buttons.Children.Add(b);
        }
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Pick(_escape); }
            else if (e.Key == Key.Enter)
            {
                // The button the keyboard is on, when it is on one — Tab onto Cancel and Enter cancels — else the action.
                int focused = Keyboard.FocusedElement is Button fb ? Buttons.Children.IndexOf(fb) : -1;
                e.Handled = true;
                Pick(focused >= 0 ? focused : _enter);
            }
        };
    }

    /// <summary>The dialog built, not shown — the one construction path; Mastermind.UiProbe measures it.</summary>
    public static MastermindDialog Build(string title, string text, string[] buttons, int accent, int cancel) =>
        new(title, text, buttons, accent, cancel);

    private void Pick(int index)
    {
        if (_picked < 0) { _picked = index; Close(); }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        int on = 1, round = 2;
        Native.DwmSetWindowAttribute(h, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
        Native.DwmSetWindowAttribute(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
    }

    private static int Show(Window owner, MastermindDialog d)
    {
        if (owner is { IsVisible: true }) { d.Owner = owner; d.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.ShowDialog();
        return d._picked;
    }

    /// <summary>A confirmation: true only when the action button was pressed, or Enter.</summary>
    public static bool Ask(Window owner, string title, string text, string action, string cancel = "Cancel") =>
        Show(owner, Build(title, text, new[] { cancel, action }, accent: 1, cancel: 0)) == 1;

    /// <summary>A notice with one OK.</summary>
    public static void Notice(Window owner, string title, string text) =>
        Show(owner, Build(title, text, new[] { "OK" }, accent: 0, cancel: 0));

    /// <summary>A click refused or failed: what happened, why and what to do next.</summary>
    public static void Tell(Window owner, string what, string why, string next) =>
        Notice(owner, "Mastermind", $"{what}\n\nWhy: {why}\n\nNext: {next}");
}
