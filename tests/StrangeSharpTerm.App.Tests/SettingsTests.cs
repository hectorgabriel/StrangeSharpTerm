using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using StrangeSharpTerm.App.Theming;
using StrangeSharpTerm.App.ViewModels;
using StrangeSharpTerm.App.Views;
using StrangeSharpTerm.Model;
using StrangeSharpTerm.Security;

namespace StrangeSharpTerm.App.Tests;

/// <summary>
/// The settings sheet: choosing a theme, and the way in to the two libraries.
///
/// The sheet holds no state of its own. It shows what the theme already is and
/// asks it to change, which is why choosing takes effect at once rather than on
/// Done — the window in front of the user is the preview.
/// </summary>
public class SettingsTests
{
    private static SettingsViewModel Sheet(AppTheme theme, Action? credentials = null, Action? snippets = null) =>
        new(
            theme,
            () => { credentials?.Invoke(); return Task.CompletedTask; },
            () => { snippets?.Invoke(); return Task.CompletedTask; });

    private static ShellViewModel Shell(IDialogService dialogs, AppTheme theme) =>
        new(
            new InventoryViewModel(null, new InventoryTree()),
            new FakeSessions(),
            (_, _) => new Border(),
            dialogs,
            theme,
            new InMemorySecretStore());

    [Fact]
    public void EveryThemeIsOfferedAndOneIsMarkedAsChosen()
    {
        var sheet = Sheet(new AppTheme());

        sheet.Themes.Select(choice => choice.Name).ShouldBe(AppPalette.BuiltIn.Select(palette => palette.Name));
        sheet.Themes.Count(choice => choice.IsChosen).ShouldBe(1);
        sheet.Themes.Single(choice => choice.IsChosen).Name.ShouldBe(AppPalette.StrangeTermDark.Name);
    }

    [Fact]
    public void ChoosingOneChangesTheThemeAndMovesTheMark()
    {
        var theme = new AppTheme();
        var sheet = Sheet(theme);

        sheet.ChooseCommand.Execute(sheet.Themes.Single(choice => choice.Name == AppPalette.Dracula.Name));

        theme.Palette.Id.ShouldBe(AppPalette.Dracula.Id);
        sheet.Themes.Single(choice => choice.IsChosen).Name.ShouldBe(AppPalette.Dracula.Name);
    }

    [Fact]
    public void ChoosingTheOneAlreadyInUseChangesNothing()
    {
        var theme = new AppTheme();
        var changes = 0;
        theme.Changed += (_, _) => changes++;
        var sheet = Sheet(theme);

        sheet.ChooseCommand.Execute(sheet.Themes.Single(choice => choice.IsChosen));

        changes.ShouldBe(0);
    }

    [Fact]
    public void ACardIsASampleOfItsOwnTheme()
    {
        // The swatch is the only honest way to pick a theme, so it is drawn from
        // the theme the card is about rather than the one in use.
        var sheet = Sheet(new AppTheme());
        var dracula = sheet.Themes.Single(choice => choice.Name == AppPalette.Dracula.Name);

        ((ISolidColorBrush)dracula.Accent).Color.ShouldBe(ThemeTokens.ToColor(AppPalette.Dracula.Accent));
        ((ISolidColorBrush)dracula.Rail).Color.ShouldBe(ThemeTokens.ToColor(AppPalette.Dracula.Rail));

        // Unchosen: its own hairline. Chosen: its own accent.
        ((ISolidColorBrush)dracula.Outline).Color.ShouldBe(ThemeTokens.ToColor(AppPalette.Dracula.Border));
        sheet.ChooseCommand.Execute(dracula);
        var chosen = sheet.Themes.Single(choice => choice.IsChosen);
        ((ISolidColorBrush)chosen.Outline).Color.ShouldBe(ThemeTokens.ToColor(AppPalette.Dracula.Accent));
    }

    [Fact]
    public async Task TheSheetOpensBothLibraries()
    {
        var opened = new List<string>();
        var sheet = Sheet(new AppTheme(), () => opened.Add("credentials"), () => opened.Add("snippets"));

        await sheet.OpenCredentialsCommand.ExecuteAsync(null);
        await sheet.OpenSnippetsCommand.ExecuteAsync(null);

        opened.ShouldBe(["credentials", "snippets"]);
    }

    [Fact]
    public async Task TheSheetOpensFromTheWindowAndItsLibrariesAreTheWindowsOwn()
    {
        // Opened through the shell rather than listed in the sheet, so there is
        // one implementation of each library and the sheet is a signpost.
        var dialogs = new ScriptedDialogService();
        var shell = Shell(dialogs, new AppTheme());

        await shell.OpenSettingsCommand.ExecuteAsync(null);

        var sheet = dialogs.Settings.ShouldHaveSingleItem();
        await sheet.OpenCredentialsCommand.ExecuteAsync(null);
        await sheet.OpenSnippetsCommand.ExecuteAsync(null);

        dialogs.Managed.ShouldHaveSingleItem();
        dialogs.ManagedSnippets.ShouldHaveSingleItem();
    }

    [Fact]
    public void SettingsHasThePlatformShortcutForIt()
    {
        // ⌘, on macOS, Ctrl+, on Windows: the one shortcut here that is not a
        // port, because every macOS app has it and SwiftUI gave the Swift app it
        // for free.
        var shell = Shell(new ScriptedDialogService(), new AppTheme());
        shell.Describe(KeyModifiers.Meta);

        var settings = shell.Commands.Single(command => command.Id == "settings");
        settings.Title.ShouldBe("Settings\u2026");
        settings.Gesture!.Key.ShouldBe(Key.OemComma);
        settings.Gesture.KeyModifiers.ShouldBe(KeyModifiers.Meta);

        shell.Describe(KeyModifiers.Control);
        shell.Commands.Single(command => command.Id == "settings").Gesture!.KeyModifiers
            .ShouldBe(KeyModifiers.Control);
    }
}
