using System.Runtime.CompilerServices;
using System.Text;

namespace StrangeSharpTerm.Assist.Providers;

/// <summary>
/// Server-sent events, as much of them as a chat completion stream uses.
///
/// Only the <c>data:</c> field, because that is the only one either provider
/// sends. Written rather than taken from a library so that a recorded stream
/// can be fed through exactly the code a socket feeds, which is what makes the
/// decoder tests worth anything.
/// </summary>
internal static class Sse
{
    /// <summary>The line a chat-completions stream ends with.</summary>
    internal const string Done = "[DONE]";

    /// <summary>
    /// The payload of each <c>data:</c> line, in order.
    ///
    /// Blank lines separate events and comment lines begin with a colon; both
    /// are skipped. A payload split across several <c>data:</c> lines of one
    /// event is rejoined with newlines, as the specification says.
    /// </summary>
    internal static async IAsyncEnumerable<string> Read(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var payload = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (payload.Length > 0)
                    yield return Take();
                continue;
            }

            if (line.StartsWith(':'))
                continue;

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (payload.Length > 0)
                    payload.Append('\n');
                payload.Append(line[5..].TrimStart());
            }
        }

        // A stream that ended without its blank line still delivered an event.
        if (payload.Length > 0)
            yield return Take();

        string Take()
        {
            var complete = payload.ToString();
            payload.Clear();
            return complete;
        }
    }
}
