using System.Text;
using StrangeSharpTerm.Model;

namespace StrangeSharpTerm.Terminal;

/// <summary>
/// The live terminals, and which of them are typing together.
///
/// The Swift original held weak references because SwiftUI handed back no
/// reference to the view it hosted. Here the app owns its sessions, so these are
/// ordinary references and <see cref="Forget"/> is how one leaves.
/// </summary>
public sealed class TerminalRegistry
{
    private readonly Dictionary<NodeId, TerminalSession> _sessions = [];
    private readonly Lock _gate = new();
    private HashSet<NodeId> _broadcastGroup = [];

    /// <summary>
    /// Panes currently receiving broadcast input. Empty means broadcasting is off,
    /// which is the only state that should ever be the default.
    /// </summary>
    public IReadOnlyCollection<NodeId> BroadcastGroup
    {
        get
        {
            lock (_gate)
                return [.. _broadcastGroup];
        }
    }

    public bool IsBroadcasting
    {
        get
        {
            lock (_gate)
                return _broadcastGroup.Count > 1;
        }
    }

    /// <summary>
    /// A group of one is not a broadcast: keeping it empty means the indicator
    /// and the fan-out agree about what is happening.
    /// </summary>
    public void SetBroadcastGroup(IEnumerable<NodeId> panes)
    {
        var group = new HashSet<NodeId>(panes);
        lock (_gate)
            _broadcastGroup = group.Count > 1 ? group : [];
    }

    public void Register(TerminalSession session)
    {
        lock (_gate)
            _sessions[session.Id] = session;
        session.InputSent += Mirror;
    }

    public void Forget(NodeId session)
    {
        TerminalSession? removed;
        lock (_gate)
        {
            _sessions.Remove(session, out removed);
            _broadcastGroup.Remove(session);
        }
        if (removed is not null)
            removed.InputSent -= Mirror;
    }

    public TerminalSession? Session(NodeId id)
    {
        lock (_gate)
            return _sessions.GetValueOrDefault(id);
    }

    /// <summary>Sends the same keystrokes to several terminals, which is how a snippet reaches a group.</summary>
    public void Send(string text, IEnumerable<NodeId> sessions)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        foreach (var id in sessions)
            Session(id)?.Write(bytes);
    }

    public string? VisibleText(NodeId session) => Session(session)?.VisibleText;

    public string? RecentText(NodeId session, int maxLines = 200) => Session(session)?.RecentText(maxLines);

    /// <summary>
    /// Mirrors keystrokes from one pane to the others in the group.
    ///
    /// Nothing is echoed back to the origin: it has already written the bytes
    /// itself. The copies are written rather than sent, so a mirrored keystroke
    /// does not set off another round of mirroring.
    /// </summary>
    private void Mirror(object? sender, ReadOnlyMemory<byte> bytes)
    {
        if (sender is not TerminalSession origin)
            return;

        TerminalSession[] others;
        lock (_gate)
        {
            if (!_broadcastGroup.Contains(origin.Id))
                return;
            others = [.. _broadcastGroup.Where(id => id != origin.Id)
                .Select(_sessions.GetValueOrDefault)
                .OfType<TerminalSession>()];
        }

        foreach (var session in others)
            session.Write(bytes.Span);
    }
}
