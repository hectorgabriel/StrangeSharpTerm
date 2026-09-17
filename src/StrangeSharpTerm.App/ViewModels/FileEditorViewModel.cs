using CommunityToolkit.Mvvm.ComponentModel;
using StrangeSharpTerm.Transport;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// One file open in the workspace: what it said when it was read, what it says
/// now, and whether those differ.
///
/// It holds no connection and does no saving. The pane owns the workspace and
/// therefore owns every round trip; this is the document, and the difference
/// matters because a document can be tested by typing into it.
/// </summary>
public sealed partial class FileEditorViewModel : ObservableObject
{
    public FileEditorViewModel(FileText file)
    {
        Path = file.Path;
        Relative = file.Relative;
        Newline = file.Newline;
        HasByteOrderMark = file.HasByteOrderMark;
        IsTruncated = file.Truncated;
        Length = file.Length;
        Modified = file.Modified;
        _saved = file.Text;
        Text = file.Text;
    }

    /// <summary>Absolute, as the server gives it. The only thing anything else navigates by.</summary>
    public string Path { get; }

    /// <summary>How it reads in the workspace: <c>conf/nginx.conf</c>.</summary>
    public string Relative { get; }

    /// <summary>What the tab says: the name alone, with a dot while it is unsaved.</summary>
    public string Title => PosixPath.Name(Path);

    /// <summary>What the file's own lines end with, put back when it is saved.</summary>
    public string Newline { get; }

    public bool HasByteOrderMark { get; }

    /// <summary>
    /// Only the start of the file is here.
    ///
    /// Saying so is not decoration: saving would replace the whole file with the
    /// part that was read, which would throw the rest away. The pane refuses,
    /// and this is what it refuses on.
    /// </summary>
    public bool IsTruncated { get; }

    public long Length { get; }

    /// <summary>
    /// What the server said the file was changed at, when it was read.
    ///
    /// Kept so that saving can notice somebody else got there first. A file
    /// being edited in a pane is a file somebody may also be editing in the
    /// terminal beside it.
    /// </summary>
    public DateTime Modified { get; private set; }

    /// <summary>
    /// Whether this is the file on screen.
    ///
    /// On the document rather than worked out by the tab strip, because with
    /// four files open the only thing saying which one you are editing is which
    /// tab is lit — and a tab strip that has to ask its parent cannot be
    /// templated without one.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    private string _saved;

    [ObservableProperty]
    public partial string Text { get; set; }

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(LineCount));
        OnPropertyChanged(nameof(Lines));
    }

    /// <summary>Whether there is anything to save.</summary>
    public bool IsDirty => Text != _saved;

    /// <summary>The tab's label, with the mark every editor uses for unsaved work.</summary>
    public string TabTitle => IsDirty ? Title + " •" : Title;

    public int LineCount => Text.Count(character => character == '\n') + 1;

    /// <summary>
    /// The numbers down the left-hand side, as one block of text.
    ///
    /// One <c>TextBlock</c> rather than a row per line: a control per line is a
    /// thousand controls in a thousand-line file, and they all have to be laid
    /// out every time a character is typed.
    /// </summary>
    public string Lines => string.Join('\n', Enumerable.Range(1, LineCount));

    /// <summary>What it looks like now is what it said all along.</summary>
    public void Saved(DateTime modified)
    {
        _saved = Text;
        Modified = modified;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(TabTitle));
    }

    /// <summary>
    /// Takes what the file now says, throwing away what was typed.
    ///
    /// Used when the file changed underneath — including when the assistant
    /// changed it, which is the common case and the reason this exists.
    /// </summary>
    public void Reloaded(FileText file)
    {
        _saved = file.Text;
        Text = file.Text;
        Modified = file.Modified;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(TabTitle));
    }
}
