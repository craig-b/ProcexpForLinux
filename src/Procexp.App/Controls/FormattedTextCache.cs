using System.Globalization;
using Avalonia.Media;

namespace Procexp.App.Controls;

/// <summary>
/// Caches laid-out text across frames.
/// </summary>
/// <remarks>
/// Constructing a <see cref="FormattedText"/> runs full text shaping, which
/// measured at roughly 44 microseconds per cell — about 10 ms for a screenful,
/// most of a 60 fps frame budget spent re-shaping strings that had not changed.
///
/// Most cell text is stable between refreshes: names, users, states, paths and
/// units do not move, and only the handful of numeric columns actually differ.
/// Caching on the text plus the layout constraints that affect shaping turns
/// those into lookups.
///
/// Entries are dropped when they go unused for a few frames, so the cache
/// follows the visible window rather than growing with the process table.
/// </remarks>
internal sealed class FormattedTextCache(Typeface typeface, double fontSize)
{
    private readonly record struct Key(string Text, double MaxWidth, bool Emphasised, bool Selected);

    private sealed record Entry(FormattedText Text)
    {
        public long LastUsedFrame { get; set; }
    }

    private const int UnusedFrameLimit = 120;
    private const int MaximumEntries = 4096;

    private readonly Dictionary<Key, Entry> _entries = [];
    private long _frame;

    /// <summary>Advance the frame counter; call once per paint.</summary>
    internal void BeginFrame()
    {
        _frame++;

        // Sweeping every frame would cost more than it saves, so do it rarely and
        // only once the cache is big enough to be worth trimming.
        if (_frame % 240 == 0 && _entries.Count > 256)
        {
            Evict();
        }
    }

    internal FormattedText Get(string text, double maxWidth, IBrush brush, bool emphasised, bool selected)
    {
        // Quantise the width so a one-pixel resize does not invalidate every
        // entry. Trimming only changes at character boundaries anyway.
        var quantised = Math.Round(maxWidth / 4) * 4;
        var key = new Key(text, quantised, emphasised, selected);

        if (_entries.TryGetValue(key, out var existing))
        {
            existing.LastUsedFrame = _frame;
            return existing.Text;
        }

        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            brush)
        {
            MaxTextWidth = quantised,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

        if (_entries.Count >= MaximumEntries)
        {
            Evict();
        }

        _entries[key] = new Entry(formatted) { LastUsedFrame = _frame };
        return formatted;
    }

    /// <summary>Drop entries that have not been drawn recently.</summary>
    private void Evict()
    {
        var cutoff = _frame - UnusedFrameLimit;

        foreach (var key in _entries.Where(e => e.Value.LastUsedFrame < cutoff).Select(e => e.Key).ToList())
        {
            _entries.Remove(key);
        }

        // If everything is still live the window is genuinely huge; drop the lot
        // rather than let the cache grow without bound.
        if (_entries.Count >= MaximumEntries)
        {
            _entries.Clear();
        }
    }

    /// <summary>Discard everything, for a theme change that alters every brush.</summary>
    internal void Clear() => _entries.Clear();
}
