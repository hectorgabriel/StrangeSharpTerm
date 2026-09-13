using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>One placeholder waiting for a value.</summary>
public sealed partial class PlaceholderValue(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial string Value { get; set; } = "";
}

/// <summary>
/// Filling in a snippet before it runs.
///
/// The point of the dialog is the preview. A snippet is about to be typed into a
/// live shell and run, so the last thing shown before that happens is the exact
/// line that will be sent — with the values already substituted, not the
/// template.
/// </summary>
public sealed partial class SnippetRunViewModel : ObservableObject
{
    private readonly Snippet _snippet;

    /// <param name="paneCount">
    /// How many panes this will reach. More than one means broadcast is on, and
    /// that has to be said here: a command meant for one server about to run on
    /// six is the surprise worth preventing.
    /// </param>
    public SnippetRunViewModel(Snippet snippet, string target, int paneCount = 1)
    {
        _snippet = snippet;
        Target = target;
        PaneCount = paneCount;
        Values = [.. snippet.Placeholders.Select(name => new PlaceholderValue(name))];

        foreach (var value in Values)
            value.PropertyChanged += (_, _) => Revalidate();
    }

    public string Title => $"Run {_snippet.Name}";

    /// <summary>The host it will run on, for the dialog to name.</summary>
    public string Target { get; }

    public int PaneCount { get; }

    public bool IsBroadcast => PaneCount > 1;

    /// <summary>Said plainly, because it is the thing most worth noticing.</summary>
    public string BroadcastWarning => $"Broadcast is on: this runs in all {PaneCount} panes of this tab.";

    public ObservableCollection<PlaceholderValue> Values { get; }

    /// <summary>
    /// Exactly what will be sent. This is the confirmation.
    ///
    /// A placeholder with nothing typed in it is left as written rather than
    /// substituted away: "tail -f /var/log/.log" looks like a command and is not
    /// one, and a preview whose job is to be read has to show the gap as a gap.
    /// </summary>
    public string Preview => _snippet.Rendered(
        Values.Where(value => value.Value.Trim().Length > 0)
            .ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal));

    /// <summary>
    /// Every placeholder needs a value. Running with one blank would send a
    /// literal <c>{{path}}</c> to the shell, which is what the preview shows and
    /// what Run refuses.
    /// </summary>
    public bool IsComplete => Values.All(value => value.Value.Trim().Length > 0);

    private void Revalidate()
    {
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(IsComplete));
    }
}
