using System.Globalization;
using Procexp.Model;

namespace Procexp.Sampling;

/// <summary>
/// Loaded modules, from <c>/proc/PID/maps</c>.
/// </summary>
/// <remarks>
/// A cleaner source than the macOS equivalent, which walks VM regions with
/// <c>proc_regionwithpathinfo</c> and reconstructs images from them. Here the
/// kernel already names the backing file for every mapping; we just group the
/// segments of each file back together.
/// </remarks>
internal static class ProcMaps
{
    internal static IReadOnlyList<ModuleInfo> Read(ProcessId id)
    {
        var buffer = ProcFile.RentBuffer(65536);
        try
        {
            if (ProcFile.TryRead($"/proc/{id.Pid}/maps", ref buffer, out var length))
            {
                return Parse(buffer.AsSpan(0, length));
            }

            // Distinguish "exited" from "not allowed". Unlike most of /proc,
            // maps is gated by ptrace_may_access, so it is readable only for
            // your own processes — the same restriction macOS applies, and one
            // the caller has to be able to report rather than showing an empty
            // list that reads as "this process has no libraries loaded".
            throw Directory.Exists($"/proc/{id.Pid}")
                ? ProviderException.NotPermitted(
                    $"/proc/{id.Pid}/maps is readable only by the process owner"
                )
                : ProviderException.ProcessGone(id);
        }
        finally
        {
            ProcFile.ReturnBuffer(buffer);
        }
    }

    /// <summary>
    /// Parse maps content. Each line is
    /// <c>start-end perms offset dev inode pathname</c>, where the pathname is
    /// absent for anonymous mappings and bracketed for special regions such as
    /// <c>[heap]</c> and <c>[stack]</c>.
    /// </summary>
    internal static List<ModuleInfo> Parse(ReadOnlySpan<byte> content)
    {
        // Several segments of the same file appear as separate lines with
        // different permissions — .text, .rodata, .data. Process Explorer shows
        // one row per module, so fold them together.
        var byPath = new Dictionary<string, (ulong Low, ulong High, string Perms)>(
            StringComparer.Ordinal
        );
        var order = new List<string>();

        while (!content.IsEmpty)
        {
            var newline = content.IndexOf((byte)'\n');
            var line = newline < 0 ? content : content[..newline];

            if (!line.IsEmpty)
            {
                ParseLine(line, byPath, order);
            }

            if (newline < 0)
            {
                break;
            }

            content = content[(newline + 1)..];
        }

        var result = new List<ModuleInfo>(order.Count);
        foreach (var path in order)
        {
            var (low, high, perms) = byPath[path];
            result.Add(
                new ModuleInfo
                {
                    Path = path,
                    Name = Path.GetFileName(path) is { Length: > 0 } n ? n : path,
                    LoadAddress = low,
                    Size = high - low,
                    Permissions = perms,
                    IsSharedLibrary =
                        perms.Contains('x') && path.Contains(".so", StringComparison.Ordinal),
                }
            );
        }

        return result;
    }

    private static void ParseLine(
        ReadOnlySpan<byte> line,
        Dictionary<string, (ulong Low, ulong High, string Perms)> byPath,
        List<string> order
    )
    {
        var rest = line;

        var range = ProcFile.NextField(ref rest);
        var dash = range.IndexOf((byte)'-');
        if (dash < 0)
        {
            return;
        }

        if (
            !TryParseHex(range[..dash], out var start)
            || !TryParseHex(range[(dash + 1)..], out var end)
        )
        {
            return;
        }

        var perms = ProcFile.NextField(ref rest);
        ProcFile.NextField(ref rest); // offset
        ProcFile.NextField(ref rest); // dev
        ProcFile.NextField(ref rest); // inode

        // The pathname is space-padded to a column, so trim rather than split.
        var pathSpan = rest;
        var pathStart = 0;
        while (pathStart < pathSpan.Length && pathSpan[pathStart] == (byte)' ')
        {
            pathStart++;
        }

        pathSpan = pathSpan[pathStart..];
        while (!pathSpan.IsEmpty && (pathSpan[^1] == (byte)'\r' || pathSpan[^1] == (byte)' '))
        {
            pathSpan = pathSpan[..^1];
        }

        // Anonymous mappings have no backing file and are not modules.
        if (pathSpan.IsEmpty)
        {
            return;
        }

        var path = ProcFile.ToString(pathSpan);
        var permString = ProcFile.ToString(perms);

        if (byPath.TryGetValue(path, out var existing))
        {
            byPath[path] = (
                Math.Min(existing.Low, start),
                Math.Max(existing.High, end),
                MergePermissions(existing.Perms, permString)
            );
        }
        else
        {
            byPath[path] = (start, end, permString);
            order.Add(path);
        }
    }

    /// <summary>Union the rwx bits across segments so the row shows the whole picture.</summary>
    private static string MergePermissions(string a, string b)
    {
        if (a.Length != 4 || b.Length != 4)
        {
            return a;
        }

        Span<char> merged = stackalloc char[4];
        for (var i = 0; i < 3; i++)
        {
            merged[i] = a[i] != '-' ? a[i] : b[i];
        }

        // The fourth character is private/shared, not a permission — keep the first.
        merged[3] = a[3];
        return new string(merged);
    }

    private static bool TryParseHex(ReadOnlySpan<byte> span, out ulong value)
    {
        value = 0;
        if (span.IsEmpty || span.Length > 16)
        {
            return false;
        }

        Span<char> chars = stackalloc char[span.Length];
        for (var i = 0; i < span.Length; i++)
        {
            chars[i] = (char)span[i];
        }

        return ulong.TryParse(
            chars,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out value
        );
    }
}
