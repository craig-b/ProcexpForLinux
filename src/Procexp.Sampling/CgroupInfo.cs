using Procexp.Model;

namespace Procexp.Sampling;

/// <summary>
/// What a process's cgroup path tells us about how it was launched.
/// </summary>
internal readonly record struct CgroupClassification(
    string? Path,
    string? Unit,
    bool IsService,
    ImageKind ContainerKind);

internal static class CgroupInfo
{
    /// <summary>
    /// Classify a process from <c>/proc/PID/cgroup</c>.
    /// </summary>
    /// <remarks>
    /// This replaces the macOS heuristic of "parent is launchd and uid is below
    /// 500". That test is a proxy; the cgroup path is the real answer, and it
    /// stays correct for services that fork or re-parent, which the PID test does
    /// not.
    ///
    /// Handles both hierarchies: cgroup v2 emits a single <c>0::/path</c> line,
    /// while v1 emits one line per controller. Systems running both ("hybrid")
    /// emit the v2 line alongside the v1 ones, so the v2 line wins where present.
    /// </remarks>
    internal static CgroupClassification Parse(ReadOnlySpan<byte> content)
    {
        string? best = null;

        while (!content.IsEmpty)
        {
            var newline = content.IndexOf((byte)'\n');
            var line = newline < 0 ? content : content[..newline];

            if (!line.IsEmpty)
            {
                // hierarchy-id:controllers:path
                var firstColon = line.IndexOf((byte)':');
                if (firstColon >= 0)
                {
                    var afterFirst = line[(firstColon + 1)..];
                    var secondColon = afterFirst.IndexOf((byte)':');
                    if (secondColon >= 0)
                    {
                        var controllers = afterFirst[..secondColon];
                        var path = afterFirst[(secondColon + 1)..];

                        // The unified (v2) line has an empty controller list and is
                        // authoritative when both hierarchies are mounted.
                        if (controllers.IsEmpty)
                        {
                            best = ProcFile.ToString(path);
                            break;
                        }

                        best ??= ProcFile.ToString(path);
                    }
                }
            }

            if (newline < 0)
            {
                break;
            }

            content = content[(newline + 1)..];
        }

        if (string.IsNullOrEmpty(best) || best == "/")
        {
            return new CgroupClassification(null, null, false, ImageKind.Unknown);
        }

        return new CgroupClassification(
            best,
            ExtractUnit(best),
            IsService(best),
            DetectContainer(best));
    }

    /// <summary>
    /// The owning systemd unit — the last path component naming a unit. Scopes
    /// and services both count; slices do not, since a slice is a grouping rather
    /// than something that launched the process.
    /// </summary>
    private static string? ExtractUnit(string path)
    {
        var span = path.AsSpan();

        while (!span.IsEmpty)
        {
            var slash = span.LastIndexOf('/');
            var component = slash < 0 ? span : span[(slash + 1)..];

            if (component.EndsWith(".service", StringComparison.Ordinal) ||
                component.EndsWith(".scope", StringComparison.Ordinal) ||
                component.EndsWith(".socket", StringComparison.Ordinal) ||
                component.EndsWith(".mount", StringComparison.Ordinal) ||
                component.EndsWith(".timer", StringComparison.Ordinal))
            {
                // user@1000.service is the per-user manager, not the thing that
                // launched this process — keep looking further up.
                if (!component.StartsWith("user@", StringComparison.Ordinal))
                {
                    return component.ToString();
                }
            }

            if (slash < 0)
            {
                break;
            }

            span = span[..slash];
        }

        return null;
    }

    /// <summary>
    /// A system service: something systemd's system manager launched, as opposed
    /// to a user session process or a login shell's child.
    /// </summary>
    private static bool IsService(string path) =>
        path.StartsWith("/system.slice/", StringComparison.Ordinal) ||
        path.StartsWith("/init.scope", StringComparison.Ordinal);

    private static ImageKind DetectContainer(string path)
    {
        if (path.Contains("/docker-", StringComparison.Ordinal) ||
            path.Contains("/docker/", StringComparison.Ordinal) ||
            path.Contains("/libpod-", StringComparison.Ordinal) ||
            path.Contains("/crio-", StringComparison.Ordinal) ||
            path.Contains("/kubepods", StringComparison.Ordinal))
        {
            return ImageKind.Container;
        }

        if (path.Contains("/lxc.payload", StringComparison.Ordinal) ||
            path.Contains("/lxc/", StringComparison.Ordinal))
        {
            return ImageKind.Container;
        }

        if (path.Contains("snap.", StringComparison.Ordinal))
        {
            return ImageKind.Snap;
        }

        if (path.Contains("app-flatpak-", StringComparison.Ordinal) ||
            path.Contains(".flatpak-", StringComparison.Ordinal))
        {
            return ImageKind.Flatpak;
        }

        return ImageKind.Unknown;
    }
}
