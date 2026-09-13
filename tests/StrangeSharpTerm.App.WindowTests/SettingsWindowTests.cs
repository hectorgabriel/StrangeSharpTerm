using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StrangeSharpTerm.App.Theming;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;

namespace StrangeSharpTerm.App.WindowTests;

/// <summary>
/// The settings sheet, drawn: the cards, the mark on the chosen one, and the
/// repaint that follows choosing another.
/// </summary>
[Collection("window")]
public class SettingsWindowTests
{
    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static IEnumerable<T> In<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    private static IEnumerable<string?> Text(Visual root) =>
        In<TextBlock>(root).Where(block => block.IsEffectivelyVisible).Select(block => block.Text);

    private static SettingsViewModel Sheet(AppTheme theme) =>
        new(theme, () => Task.CompletedTask, () => Task.CompletedTask);

    [Fact]
    public void EachThemeIsACardWithItsOwnColoursInIt()
    {
        Headless.Run(() =>
        {
            var window = new SettingsWindow(Sheet(new AppTheme()));
            window.Show();
            Settle(window);

            var shown = Text(window).ToArray();
            shown.ShouldContain("StrangeTerm Dark");
            shown.ShouldContain("Near-black surfaces, teal accent");
            shown.ShouldContain("Dracula");
            shown.ShouldContain(SettingsViewModel.ThemeNote);

            // Four swatches per card, painted from the theme each card is about.
            var swatches = In<Border>(window)
                .Where(border => border.Width == 14 && border.Background is ISolidColorBrush)
                .Select(border => ((ISolidColorBrush)border.Background!).Color)
                .ToArray();
            swatches.Length.ShouldBe(AppPalette.BuiltIn.Count * 4);
            swatches.ShouldContain(ThemeTokens.ToColor(AppPalette.Dracula.Accent));
            swatches.ShouldContain(ThemeTokens.ToColor(AppPalette.StrangeTermDark.Accent));

            window.Close();
        });
    }

    [Fact]
    public void ChoosingACardRepaintsTheSheetItIsIn()
    {
        // Nothing to confirm: the window in front of the user is the preview and
        // the answer at once.
        Headless.Run(() =>
        {
            var theme = new AppTheme();
            theme.PaintInto(Application.Current!);
            var window = new SettingsWindow(Sheet(theme));
            window.Show();
            Settle(window);

            var background = () => ((ISolidColorBrush)window.Background!).Color;
            background().ShouldBe(ThemeTokens.ToColor(AppPalette.StrangeTermDark.Background));

            // Clicked, not raised: a button runs its Command inside OnClick, which
            // only the input path reaches — raising the event would test nothing.
            var dracula = In<Button>(window)
                .Single(button => button.DataContext is ThemeChoice { Name: "Dracula" });
            var centre = dracula.TranslatePoint(
                new Point(dracula.Bounds.Width / 2, dracula.Bounds.Height / 2), window)!.Value;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Settle(window);

            theme.Palette.Id.ShouldBe(AppPalette.Dracula.Id);
            background().ShouldBe(ThemeTokens.ToColor(AppPalette.Dracula.Background));

            window.Close();

            // Put it back: the application is shared by every test in this run.
            theme.Use(AppPalette.StrangeTermDark);
        });
    }

    [Fact]
    public void BothLibrariesAreReachableFromIt()
    {
        Headless.Run(() =>
        {
            var window = new SettingsWindow(Sheet(new AppTheme()));
            window.Show();
            Settle(window);

            var shown = Text(window).ToArray();
            shown.ShouldContain("Credentials…");
            shown.ShouldContain("Snippets…");

            window.Close();
        });
    }
}
