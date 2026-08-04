using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Procexp.Model;

namespace Procexp.Provenance;

/// <summary>
/// Establishes where an on-disk image came from — the Linux replacement for the
/// macOS code-signing provider.
/// </summary>
/// <remarks>
/// Confidence is assembled from four independent sources, in descending order of
/// strength: the package database (built and shipped by the distribution, with a
/// recorded hash), an IMA signature xattr where the system enables it, Flatpak or
/// Snap bundle identity, and finally the ELF build-id, which identifies but does
/// not attest.
/// </remarks>
public sealed partial class ProvenanceProvider : IProvenanceProvider
{
    private readonly PackageDatabase _packages = new();
    private readonly VirusTotalClient _virusTotal;
    private readonly Dictionary<string, ProvenanceInfo> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public ProvenanceProvider(VirusTotalClient? virusTotal = null) =>
        _virusTotal = virusTotal ?? new VirusTotalClient();

    public PackageManagerKind PackageManager => _packages.Kind;

    public ValueTask<ProvenanceInfo> ProvenanceAsync(string path, CancellationToken cancellationToken = default)
    {
        // Keyed by path plus identity, so an upgraded binary is re-examined rather
        // than served stale from a previous version.
        var key = CacheKey(path);

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return ValueTask.FromResult(cached);
            }
        }

        var info = Examine(path);

        lock (_gate)
        {
            _cache[key] = info;
        }

        return ValueTask.FromResult(info);
    }

    private static string CacheKey(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    private ProvenanceInfo Examine(string path)
    {
        if (!File.Exists(path))
        {
            return new ProvenanceInfo
            {
                Status = ProvenanceStatus.Unknown,
                VerificationError = "image not found",
            };
        }

        var elf = ElfInspector.Inspect(path);
        var bundleId = DetectBundle(path);
        var hasIma = HasImaSignature(path);

        // A Flatpak or Snap ships inside its own signed bundle and is not tracked
        // by the host package database, so testing that first avoids reporting it
        // as unpackaged.
        if (bundleId is not null)
        {
            return new ProvenanceInfo
            {
                Status = ProvenanceStatus.SandboxedBundle,
                BundleId = bundleId,
                BuildId = elf.BuildId,
                HasImaSignature = hasIma,
            };
        }

        var owner = _packages.Owner(path);
        if (owner is null)
        {
            return new ProvenanceInfo
            {
                Status = _packages.Kind == PackageManagerKind.None
                    ? ProvenanceStatus.Unknown
                    : ProvenanceStatus.Unpackaged,
                BuildId = elf.BuildId,
                HasImaSignature = hasIma,
                VerificationError = _packages.Kind == PackageManagerKind.None
                    ? "no package database on this system"
                    : null,
            };
        }

        return new ProvenanceInfo
        {
            // Ownership alone does not prove the file is unmodified — that needs a
            // verification pass expensive enough to be on-demand only, so the
            // sweep reports the weaker claim it can actually stand behind.
            Status = ProvenanceStatus.PackageVerified,
            PackageName = owner.PackageName,
            PackageVersion = owner.Version,
            Repository = owner.Repository,
            Packager = owner.Packager,
            PackageDescription = owner.Description,
            BuildId = elf.BuildId,
            HasImaSignature = hasIma,
        };
    }

    /// <summary>
    /// Re-examine one image with the expensive checks enabled: hash the file and
    /// verify it against the package manifest. For the Properties window, never
    /// for the process list.
    /// </summary>
    public async ValueTask<ProvenanceInfo> DeepProvenanceAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var basic = await ProvenanceAsync(path, cancellationToken).ConfigureAwait(false);
        var sha = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);

        if (basic.PackageName is not { } package)
        {
            return basic with { Sha256 = sha };
        }

        var unmodified = _packages.IsUnmodified(path, package);

        return basic with
        {
            Sha256 = sha,
            Status = unmodified switch
            {
                true => ProvenanceStatus.PackageVerified,
                false => ProvenanceStatus.PackageModified,
                // Null means the verification tool is missing — debsums is not
                // installed by default on Debian. Keep the weaker claim rather
                // than implying tampering.
                null => basic.Status,
            },
            VerificationError = unmodified is null ? "package verification unavailable" : null,
        };
    }

    public ValueTask<VirusTotalResult?> VirusTotalAsync(string sha256, CancellationToken cancellationToken = default) =>
        _virusTotal.ResultAsync(sha256, cancellationToken);

    public static async ValueTask<string?> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(hash);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Flatpak and Snap identity, inferred from the install location.
    /// </summary>
    private static string? DetectBundle(string path)
    {
        // /var/lib/flatpak/app/<id>/... or ~/.local/share/flatpak/app/<id>/...
        var flatpak = path.IndexOf("/flatpak/app/", StringComparison.Ordinal);
        if (flatpak >= 0)
        {
            var rest = path[(flatpak + "/flatpak/app/".Length)..];
            var slash = rest.IndexOf('/');
            return slash > 0 ? rest[..slash] : rest;
        }

        // /snap/<name>/<revision>/...
        if (path.StartsWith("/snap/", StringComparison.Ordinal))
        {
            var rest = path["/snap/".Length..];
            var slash = rest.IndexOf('/');
            return slash > 0 ? rest[..slash] : rest;
        }

        return null;
    }

    /// <summary>
    /// Whether an IMA signature xattr is present.
    /// </summary>
    /// <remarks>
    /// The Integrity Measurement Architecture is the one mechanism that signs
    /// individual Linux binaries the way macOS does, but it is off by default
    /// almost everywhere, so its absence says nothing.
    /// </remarks>
    private static bool HasImaSignature(string path)
    {
        try
        {
            return GetXattr(path, "security.ima", nint.Zero, 0) > 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "getxattr", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint GetXattr(string path, string name, nint value, nuint size);
}
