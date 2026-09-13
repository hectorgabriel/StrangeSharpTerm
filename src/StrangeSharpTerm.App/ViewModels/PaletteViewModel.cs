using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// The command palette: type a few letters, get the thing you meant.
///
/// The last of the seams the Swift god object held. It ranks with
/// <see cref="FuzzyMatch"/>, ported in M1 for this — people type "spl" for
/// "Split Side by Side" and "nh" for "New Host", and a palette that only did
/// substrings would find neither.
///
/// It offers what can be run <em>now</em>: a command whose CanExecute is false —
/// close a pane when none is open — is left out rather than shown greyed, because
/// a palette is a list of answers to "what can I do", not a menu.
/// </summary>
public sealed partial class PaletteViewModel(IReadOnlyList<AppCommand> commands) : ObservableObject
{
    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    /// <summary>Which row the keyboard is on. Always within the list, or -1 when it is empty.</summary>
    [ObservableProperty]
    public partial int SelectedIndex { get; set; } = -1;

    public ObservableCollection<AppCommand> Matches { get; } = [];

    public AppCommand? Selected =>
        SelectedIndex >= 0 && SelectedIndex < Matches.Count ? Matches[SelectedIndex] : null;

    /// <summary>Opens it fresh: an old query is not what the next question starts from.</summary>
    public void Open()
    {
        Query = "";
        Refresh();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    /// <summary>Runs what is selected and closes. Nothing selected closes it anyway.</summary>
    public void RunSelected()
    {
        var command = Selected;
        Close();
        command?.Run();
    }

    /// <summary>Moves the selection, stopping at each end rather than wrapping.</summary>
    public void Move(int by)
    {
        if (Matches.Count == 0)
            return;
        SelectedIndex = Math.Clamp(SelectedIndex + by, 0, Matches.Count - 1);
    }

    partial void OnQueryChanged(string value) => Refresh();

    private void Refresh()
    {
        var offered = CommandCatalogue.ForPalette(commands).Where(command => command.CanRun);

        Matches.Clear();
        foreach (var command in FuzzyMatch.Rank(offered, Query, command => command.Title))
            Matches.Add(command);

        // The first row is always the one Enter would run, so a palette that has
        // just been narrowed does not run whatever was under the old highlight.
        SelectedIndex = Matches.Count == 0 ? -1 : 0;
    }
}
