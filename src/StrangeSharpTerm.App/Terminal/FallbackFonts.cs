using System.Reflection;
using System.Text;
using Iciclecreek.Terminal;
using SkiaSharp;

namespace StrangeSharpTerm.App.Terminal;

/// <summary>
/// Finds the fonts a pane will need before the pane needs them, off the thread
/// that draws it.
///
/// A character the terminal font has no glyph for is drawn from a fallback font,
/// and the renderer finds that font by asking the system -- on macOS, CoreText,
/// over XPC to the font server, a few milliseconds a character and far more for
/// one no font has. It asks the first time it draws the character, which is on
/// the UI thread, which is also where keystrokes are handled. Output with a few
/// thousand characters the terminal font lacks -- emoji, CJK, an installer's
/// icons -- held the UI thread for seconds at a time, and typing sat in the
/// queue behind it until the pane looked hung. A sample of the stalled app had
/// 94% of the main thread inside <c>sk_fontmgr_match_family_style_character</c>.
///
/// The renderer caches every answer, hits and misses both, in a concurrent
/// dictionary keyed by the character alone. So this asks it the same question
/// first, on the thread that reads the output, before the bytes reach the
/// renderer's engine: by the time a frame draws those characters every one is
/// already answered. The cost is still paid, but by a thread nobody is waiting
/// on to type.
///
/// The cache is private to Iciclecreek, and so is the method that fills it,
/// which is why this reaches them by reflection. If an upgrade renames either,
/// this does nothing -- the pane is back to looking fonts up as it draws, which
/// is slower and still correct -- and a window test fails to say so.
/// </summary>
internal sealed class FallbackFonts
{
    private static readonly FieldInfo? CacheField =
        typeof(TerminalView).GetField("_skiaFonts", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? FamilyField =
        typeof(TerminalView).GetField("_fontFamilyChain", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo? FallbackFace =
        CacheField?.FieldType.GetMethod("FallbackFace", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(int)]);

    /// <summary>The cache's own font lookup: the font the pane really draws with, as it chose it.</summary>
    private static readonly MethodInfo? FontFor = CacheField?.FieldType
        .GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .FirstOrDefault(method => method.Name == "For" && method.GetParameters() is [{ ParameterType: var family }, { ParameterType: var size }, _]
            && family == typeof(string) && size == typeof(double));

    private readonly TerminalView _view;
    private readonly object _cache;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private char[] _chars = new char[1024];

    /// <summary>
    /// Every character this has already dealt with, found or not. Touched only
    /// by the one thread that reads the pane's output, so it needs no lock.
    /// </summary>
    private readonly HashSet<int> _seen = [];

    private FallbackFonts(TerminalView view, object cache)
    {
        _view = view;
        _cache = cache;
    }

    /// <summary>
    /// For a pane's view, or null when this version of the renderer is not the
    /// shape this expects.
    /// </summary>
    public static FallbackFonts? For(TerminalView view) =>
        FamilyField is not null && FallbackFace is not null && FontFor is not null
            && CacheField?.GetValue(view) is { } cache
            ? new FallbackFonts(view, cache)
            : null;

    /// <summary>
    /// Looks up a fallback for every new character in these bytes that the
    /// terminal font cannot draw. Called with each chunk of output, in order,
    /// before the renderer sees it.
    /// </summary>
    public void Observe(ReadOnlySpan<byte> bytes)
    {
        // Nearly everything a shell prints. A sequence left half-decoded by the
        // last chunk cannot be finished by ASCII, so it is dropped rather than
        // carried into the next one.
        if (Ascii.IsValid(bytes))
        {
            _decoder.Reset();
            return;
        }

        var needed = _decoder.GetCharCount(bytes, flush: false);
        if (needed > _chars.Length)
            _chars = new char[Math.Max(needed, _chars.Length * 2)];
        var count = _decoder.GetChars(bytes, _chars, flush: false);

        SKTypeface? face = null;
        foreach (var rune in _chars.AsSpan(0, count).EnumerateRunes())
        {
            if (rune.IsAscii || !_seen.Add(rune.Value))
                continue;

            try
            {
                // Asked for once, on the first new character: most output with
                // anything non-ASCII in it has only a few, and the family is
                // read fresh each time in case the pane's font changed.
                face ??= PrimaryFace();
                if (face is null || face.ContainsGlyph(rune.Value))
                    continue;
                FallbackFace!.Invoke(_cache, [rune.Value]);
            }
            catch (Exception)
            {
                // Never at the cost of the output. Whatever went wrong here, the
                // renderer will look the character up itself when it draws it.
            }
        }
    }

    private SKTypeface? PrimaryFace()
    {
        var family = FamilyField!.GetValue(_view) as string ?? "monospace";
        var flags = Activator.CreateInstance(FontFor!.GetParameters()[2].ParameterType);
        return (FontFor.Invoke(_cache, [family, 12.0, flags]) as SKFont)?.Typeface;
    }
}
