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
    public partial string Answer { get; set; } = "";
}
