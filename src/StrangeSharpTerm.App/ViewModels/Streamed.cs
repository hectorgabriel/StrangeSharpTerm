using System.Diagnostics;
using Avalonia.Threading;

namespace StrangeSharpTerm.App.ViewModels;

/// <summary>
/// Puts streamed work on screen at the rate a person reads, rather than at the
/// rate a model writes.
///
/// A reasoning model sends its thinking a token at a time, and every token used
/// to be a property change of its own. Each of those re-wraps the whole
/// paragraph that has arrived so far, so the work per token grows with the text
/// behind it: a think that runs to a few thousand tokens spends a core laying
/// out prose that is thrown away a millisecond later, and the pane heats the
/// machine up for the length of the answer. DeepSeek is where this shows,
/// because it is the provider that streams raw thinking rather than a summary
/// that arrives in paragraphs.
///
/// The tokens are worth showing. Showing each of them separately is not, so
/// they are collected and applied together, at most once every <see cref="Gap"/>.
/// The newest value under a key wins, which is exactly what a stream of this
/// shape means: each delta carries everything so far, and the one in between
/// was only ever a shorter version of the one after it.
/// </summary>
internal sealed class Streamed
{
    /// <summary>
    /// How long the newest value waits for the ones behind it.
    ///
    /// Twelve frames a second: fast enough that the text still reads as
    /// arriving, slow enough that a burst of forty tokens costs one layout
    /// rather than forty. Prose is forgiving of this in a way scrolling would
    /// not be, which is why the number can be this large.
    /// </summary>
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(80);

    private readonly Lock _gate = new();

    /// <summary>The newest work under each key, and the order the keys arrived in.</summary>
    private readonly Dictionary<object, Action> _waiting = [];

    private readonly List<object> _keys = [];

    /// <summary>Whether a frame is already on its way to the UI thread.</summary>
    private bool _coming;

    /// <summary>When the last frame was applied, so the next one can be spaced from it.</summary>
    private long _applied;

    /// <summary>
    /// Shows this at the next frame, replacing anything else waiting under the
    /// same key.
    ///
    /// A value that arrives too soon after the last frame is kept rather than
    /// dropped: the delta after it schedules the frame that carries both, and
    /// <see cref="Now"/> or <see cref="Flush"/> carries the last one when the
    /// stream stops. So nothing waits on a timer that has to be cancelled, and
    /// nothing is lost when the tokens run out mid-gap.
    /// </summary>
    internal void Soon(object key, Action work)
    {
        lock (_gate)
        {
            if (!_waiting.ContainsKey(key))
                _keys.Add(key);
            _waiting[key] = work;

            if (_coming || Stopwatch.GetElapsedTime(_applied) < Gap)
                return;

            _coming = true;
        }

        Post(Apply);
    }

    /// <summary>
    /// Runs this now, after everything already waiting.
    ///
    /// For what is not a stream: a row arriving, a run finishing, a pane
    /// cleared. It applies the waiting work first because order is the whole
    /// point -- thinking that arrived before an answer must not land on screen
    /// after it.
    /// </summary>
    internal void Now(Action work) => Post(() =>
    {
        Apply();
        work();
    });

    /// <summary>Everything waiting, on screen, whether or not a frame is due.</summary>
    internal void Flush() => Post(Apply);

    private void Apply()
    {
        Action[] work;
        lock (_gate)
        {
            _coming = false;
            _applied = Stopwatch.GetTimestamp();
            if (_waiting.Count == 0)
                return;

            work = [.. _keys.Select(key => _waiting[key])];
            _waiting.Clear();
            _keys.Clear();
        }

        foreach (var each in work)
            each();
    }

    /// <summary>
    /// The UI thread, from wherever this was called.
    ///
    /// Inline when it is already that thread, because a test body and the pane
    /// itself both run there and posting would only defer the work past the
    /// assertion that is waiting for it.
    /// </summary>
    private static void Post(Action work)
    {
        if (Dispatcher.UIThread.CheckAccess())
            work();
        else
            Dispatcher.UIThread.Post(work);
    }
}
