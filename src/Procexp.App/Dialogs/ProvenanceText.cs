using Procexp.Model;

namespace Procexp.App.Dialogs;

/// <summary>
/// Plain-English provenance statuses, shared by every view that shows one.
/// </summary>
/// <remarks>
/// One wording, in one place: the Properties tab and the mapped-file detail
/// window answer the same question about the same file, and two copies would
/// eventually answer it differently.
/// </remarks>
internal static class ProvenanceText
{
    public static string Describe(ProvenanceStatus status) =>
        status switch
        {
            ProvenanceStatus.PackageVerified =>
                "Shipped by the distribution, and unmodified on disk.",
            ProvenanceStatus.PackageModified =>
                "Owned by a package, but the file on disk no longer matches it.",
            ProvenanceStatus.Unpackaged =>
                "Not owned by any package — built locally, downloaded, or installed by hand.",
            ProvenanceStatus.SandboxedBundle =>
                "Shipped inside a Flatpak or Snap, which carries its own signing.",
            ProvenanceStatus.Unknown => "Could not be determined.",
            _ => status.ToString(),
        };
}
