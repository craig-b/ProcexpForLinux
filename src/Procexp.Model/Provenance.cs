namespace Procexp.Model;

/// <summary>
/// How much confidence we have in the origin of an on-disk image.
/// </summary>
/// <remarks>
/// This is the Linux replacement for the macOS <c>SigningStatus</c>. There is no
/// equivalent of Security.framework: ELF user-space binaries are essentially
/// never individually signed, so the honest source of provenance is the
/// distribution package database, which does carry cryptographically-verified
/// hashes for every file it owns.
/// </remarks>
public enum ProvenanceStatus
{
    /// <summary>Not yet examined.</summary>
    Unverified,

    /// <summary>Examination in flight.</summary>
    Verifying,

    /// <summary>Owned by a package and the on-disk file matches the package manifest.</summary>
    PackageVerified,

    /// <summary>Owned by a package but the on-disk file no longer matches it — tampered or patched.</summary>
    PackageModified,

    /// <summary>No owning package. Locally built, downloaded, or installed outside the package manager.</summary>
    Unpackaged,

    /// <summary>Shipped inside a Flatpak or Snap, which carries its own signing chain.</summary>
    SandboxedBundle,

    /// <summary>Could not be determined — unreadable, or no package manager present.</summary>
    Unknown,
}

/// <summary>Reputation lookup result. Ports directly from the macOS implementation.</summary>
public sealed record VirusTotalResult
{
    public required int Positives { get; init; }
    public required int Total { get; init; }
    public string? Permalink { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }
}

/// <summary>
/// Everything known about where an image came from. Fills the Verified Signer
/// column and the Security tab of the Properties window.
/// </summary>
public sealed record ProvenanceInfo
{
    public required ProvenanceStatus Status { get; init; }

    /// <summary>Owning package, e.g. <c>coreutils</c>.</summary>
    public string? PackageName { get; init; }

    public string? PackageVersion { get; init; }

    /// <summary>Repository the package came from, e.g. <c>core</c> or <c>bookworm/main</c>.</summary>
    public string? Repository { get; init; }

    /// <summary>Packager or maintainer identity — the closest analog of a signer.</summary>
    public string? Packager { get; init; }

    /// <summary>
    /// The owning package's one-line summary, which is what makes the Description
    /// column worth having — "system and service manager" rather than "systemd".
    /// </summary>
    public string? PackageDescription { get; init; }

    /// <summary>GNU build-id from the ELF note section, a stable image identity.</summary>
    public string? BuildId { get; init; }

    /// <summary>SHA-256 of the on-disk image, for reputation lookups.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Whether an IMA signature xattr is present, where the system enables IMA.</summary>
    public bool HasImaSignature { get; init; }

    /// <summary>Flatpak or Snap identity when the image ships inside a bundle.</summary>
    public string? BundleId { get; init; }

    public VirusTotalResult? VirusTotal { get; init; }

    /// <summary>Why verification failed, when it did.</summary>
    public string? VerificationError { get; init; }

    /// <summary>
    /// One-line summary for the Verified Signer column, chosen to be directly
    /// comparable to what the macOS build shows for a signed binary.
    /// </summary>
    public string DisplayName => Status switch
    {
        ProvenanceStatus.PackageVerified when Repository is not null && PackageName is not null =>
            $"{Repository}/{PackageName} {PackageVersion}".TrimEnd(),
        ProvenanceStatus.PackageVerified when PackageName is not null =>
            $"{PackageName} {PackageVersion}".TrimEnd(),
        ProvenanceStatus.PackageModified when PackageName is not null =>
            $"{PackageName} (modified)",
        ProvenanceStatus.SandboxedBundle when BundleId is not null => BundleId,
        ProvenanceStatus.Unpackaged => "(unpackaged)",
        ProvenanceStatus.Verifying => "(verifying...)",
        ProvenanceStatus.Unverified => "",
        _ => "(unknown)",
    };

    public static readonly ProvenanceInfo Unverified = new() { Status = ProvenanceStatus.Unverified };
}
