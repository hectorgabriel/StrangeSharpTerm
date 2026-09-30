using CommunityToolkit.Mvvm.ComponentModel;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// One question put to the planner about its plan, and its answer as it
/// arrives.
/// </summary>
public sealed partial class DiscussionRow(string question) : ObservableObject
{
    public string Question { get; } = question;

    /// <summary>What the planner said. Empty until the first of it arrives.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    public partial string Answer { get; set; } = "";

    /// <summary>
    /// Nothing said yet. A reasoning model can think for a minute before its
    /// first word, and a heading with nothing under it reads as stalled.
    /// </summary>
    public bool IsWaiting => Answer.Length == 0;
}
