namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// What has been typed into a chat field and sent, for the up arrow to walk
/// back through.
///
/// One implementation for both panes. It is the pane's and not the agent's:
/// the agent's transcript is what was asked and answered, and this is what
/// was typed.
///
/// It also holds what was in the field when the walk began. Pressing Up over
/// half a question used to replace it for good; now Down past the newest
/// entry puts it back, which is what makes Up safe to press without looking.
/// </summary>
public sealed class TypedHistory
{
    private readonly List<string> _sent = [];
    private int _walked;
    private string _draft = "";

    public int Count => _sent.Count;

    /// <summary>The newest thing sent, which is what a retry asks again.</summary>
    public string? Last => _sent.Count > 0 ? _sent[^1] : null;

    public void Remember(string text)
    {
        // Not twice in a row: sending the same thing again is one entry to
        // walk back to, not two.
        if (text.Length > 0 && (_sent.Count == 0 || _sent[^1] != text))
            _sent.Add(text);
        _walked = 0;
        _draft = "";
    }

    public void Clear()
    {
        _sent.Clear();
        _walked = 0;
        _draft = "";
    }

    /// <summary>
    /// The entry before this one (1) or after it (-1), or null at either end
    /// so the field and its caret can be left alone.
    /// </summary>
    /// <param name="current">What the field holds now, kept if this is the first step back.</param>
    public string? Walk(int direction, string current)
    {
        if (_sent.Count == 0)
            return null;

        var walked = Math.Clamp(_walked + direction, 0, _sent.Count);
        if (walked == _walked)
            return null;

        if (_walked == 0)
            _draft = current;
        _walked = walked;
        return walked == 0 ? _draft : _sent[^walked];
    }
}
