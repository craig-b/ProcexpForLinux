using System.Diagnostics;

namespace Procexp.Provenance;

/// <summary>Which package manager owns this system.</summary>
public enum PackageManagerKind
{
    None,
    Pacman,
    Dpkg,
    Rpm,
    Apk,
}

/// <summary>What the package database knows about one file.</summary>
public sealed record PackageOwnership
{
    public required string PackageName { get; init; }
    public string? Version { get; init; }
    public string? Repository { get; init; }
    public string? Packager { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// The distribution package database — the Linux substitute for a code signature.
/// </summary>
/// <remarks>
/// No comparison to Security.framework is exact, but this is the closest honest
/// equivalent. A packaged file was built by the distribution, shipped through a
/// signed repository, and its expected hash is recorded locally, so "is this the
/// binary the distribution shipped?" is a question that can actually be answered.
/// An unpackaged binary is the analog of an unsigned one.
///
/// Ownership queries shell out, which is slow enough that results are cached by
/// path for the life of the process. Executable paths do not change owner while
/// the app is running, so the cache never needs invalidating.
/// </remarks>
public sealed class PackageDatabase
{
    private readonly Dictionary<string, PackageOwnership?> _ownershipCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PackageOwnership> _detailCache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public PackageManagerKind Kind { get; } = Detect();

    private static PackageManagerKind Detect()
    {
        // Test for the database rather than the binary: a container may have the
        // tool installed without a populated database, and querying that is slow
        // and always empty.
        if (Directory.Exists("/var/lib/pacman/local"))
        {
            return PackageManagerKind.Pacman;
        }

        if (File.Exists("/var/lib/dpkg/status"))
        {
            return PackageManagerKind.Dpkg;
        }

        if (Directory.Exists("/var/lib/rpm") || Directory.Exists("/usr/lib/sysimage/rpm"))
        {
            return PackageManagerKind.Rpm;
        }

        if (File.Exists("/lib/apk/db/installed"))
        {
            return PackageManagerKind.Apk;
        }

        return PackageManagerKind.None;
    }

    /// <summary>Which package owns a path, or null when nothing does.</summary>
    public PackageOwnership? Owner(string path)
    {
        lock (_gate)
        {
            if (_ownershipCache.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        var result = QueryOwner(path);

        lock (_gate)
        {
            _ownershipCache[path] = result;
        }

        return result;
    }

    private PackageOwnership? QueryOwner(string path) => Kind switch
    {
        PackageManagerKind.Pacman => QueryPacman(path),
        PackageManagerKind.Dpkg => QueryDpkg(path),
        PackageManagerKind.Rpm => QueryRpm(path),
        PackageManagerKind.Apk => QueryApk(path),
        _ => null,
    };

    // "/usr/bin/ls is owned by coreutils 9.7-2"
    private PackageOwnership? QueryPacman(string path)
    {
        var output = Run("pacman", ["-Qoq", path]);
        var name = output?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return Detail(name, () => ParsePacmanDetail(name));
    }

    private static PackageOwnership ParsePacmanDetail(string name)
    {
        var info = Run("pacman", ["-Qi", name]);
        string? version = null, packager = null, description = null;

        if (info is not null)
        {
            foreach (var line in info.Split('\n'))
            {
                var colon = line.IndexOf(':');
                if (colon < 0)
                {
                    continue;
                }

                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();

                if (key == "Version") version = value;
                else if (key == "Packager") packager = value;
                else if (key == "Description") description = value;
            }
        }

        return new PackageOwnership
        {
            PackageName = name,
            Version = version,
            Packager = packager,
            Description = description,
        };
    }

    // "coreutils: /usr/bin/ls"
    private PackageOwnership? QueryDpkg(string path)
    {
        var output = Run("dpkg-query", ["-S", path]);
        if (output is null)
        {
            return null;
        }

        var colon = output.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        // A diverted or multi-arch path can list several packages; take the first.
        var name = output[..colon].Split(',')[0].Trim();
        if (name.Length == 0)
        {
            return null;
        }

        return Detail(name, () =>
        {
            var fields = Run("dpkg-query", ["-W", "-f=${Version}\\n${Maintainer}\\n${Description}", name]);
            var parts = fields?.Split('\n') ?? [];
            return new PackageOwnership
            {
                PackageName = name,
                Version = parts.Length > 0 ? parts[0].Trim() : null,
                Packager = parts.Length > 1 ? parts[1].Trim() : null,
                Description = parts.Length > 2 ? parts[2].Trim() : null,
            };
        });
    }

    private PackageOwnership? QueryRpm(string path)
    {
        var output = Run("rpm", ["-qf", "--queryformat", "%{NAME}\\n%{VERSION}-%{RELEASE}\\n%{PACKAGER}\\n%{SUMMARY}", path]);
        if (output is null)
        {
            return null;
        }

        var parts = output.Split('\n');
        if (parts.Length == 0 || parts[0].Contains("not owned", StringComparison.Ordinal))
        {
            return null;
        }

        return new PackageOwnership
        {
            PackageName = parts[0].Trim(),
            Version = parts.Length > 1 ? parts[1].Trim() : null,
            Packager = parts.Length > 2 ? parts[2].Trim() : null,
            Description = parts.Length > 3 ? parts[3].Trim() : null,
        };
    }

    private PackageOwnership? QueryApk(string path)
    {
        var output = Run("apk", ["info", "--who-owns", path]);
        if (output is null)
        {
            return null;
        }

        // "/usr/bin/ls is owned by busybox-1.36.1-r15"
        var marker = output.LastIndexOf("owned by ", StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var name = output[(marker + "owned by ".Length)..].Trim();
        return name.Length == 0 ? null : new PackageOwnership { PackageName = name };
    }

    private PackageOwnership Detail(string name, Func<PackageOwnership> factory)
    {
        lock (_gate)
        {
            if (_detailCache.TryGetValue(name, out var cached))
            {
                return cached;
            }
        }

        var detail = factory();

        lock (_gate)
        {
            _detailCache[name] = detail;
        }

        return detail;
    }

    /// <summary>
    /// Whether the on-disk file still matches what the package shipped.
    /// </summary>
    /// <remarks>
    /// Deliberately not called during a sweep. Verification hashes every file the
    /// package owns, which takes seconds per package — fine when the Properties
    /// window asks about one binary, ruinous across a process list.
    ///
    /// Null means "could not determine", which is different from "modified" and
    /// must not be rendered as a warning.
    /// </remarks>
    public bool? IsUnmodified(string path, string packageName) => Kind switch
    {
        // pacman -Qkk reports mismatching properties per file; no output for the
        // path means it matches.
        PackageManagerKind.Pacman =>
            Run("pacman", ["-Qkk", packageName]) is { } output ? !output.Contains(path, StringComparison.Ordinal) : null,

        // rpm -V lists only files that differ.
        PackageManagerKind.Rpm =>
            Run("rpm", ["-V", packageName]) is { } output ? !output.Contains(path, StringComparison.Ordinal) : null,

        // debsums exits non-zero and prints "FAILED" for mismatches. It is not
        // installed by default on Debian, so absence is unknown, not a pass.
        PackageManagerKind.Dpkg =>
            Run("debsums", ["-s", packageName]) is { } output ? !output.Contains("FAILED", StringComparison.Ordinal) : null,

        _ => null,
    };

    private static string? Run(string file, string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();

            // Verification commands legitimately take a while; ownership queries
            // do not. A generous bound still beats hanging the caller.
            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
                {
                    // Already gone.
                }

                return null;
            }

            // A non-zero exit is the normal "not owned by any package" answer, but
            // some tools also print useful output alongside it, so only treat an
            // empty result as failure.
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // The tool is not installed.
            return null;
        }
    }
}
