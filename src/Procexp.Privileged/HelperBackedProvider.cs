using System.Text.Json;
using Procexp.Model;

namespace Procexp.Privileged;

/// <summary>
/// Wraps an unprivileged provider and falls back to the helper for the reads the
/// kernel refuses.
/// </summary>
/// <remarks>
/// A decorator rather than a branch inside the sampler, so nothing above knows
/// the helper exists. The UI asks for a process's modules; either the ordinary
/// path returns them, or this quietly asks the daemon. If the helper is absent
/// the original refusal propagates unchanged and the UI explains it.
///
/// Only the <c>ptrace_may_access</c>-gated reads are routed here — maps, fd,
/// fdinfo and environ. Everything else is world-readable and never fails in a way
/// the helper could fix, so sending it over a socket would add latency for
/// nothing.
/// </remarks>
public sealed class HelperBackedProvider(IProcessDataProvider inner, PrivilegedClient client)
    : IProcessDataProvider
{
    public ProviderCapabilities Capabilities =>
        inner.Capabilities |
        ProviderCapabilities.CrossUser |
        ProviderCapabilities.Environment |
        ProviderCapabilities.ProcessIo |
        ProviderCapabilities.ProportionalMemory;

    public IAsyncEnumerable<ProcessSnapshot> Snapshots(TimeSpan interval, CancellationToken cancellationToken = default) =>
        inner.Snapshots(interval, cancellationToken);

    public ValueTask<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
        inner.SnapshotAsync(cancellationToken);

    // Threads, command lines and working directories are readable cross-user, so
    // they never need the helper.
    public ValueTask<IReadOnlyList<ThreadInfo>> ThreadsAsync(ProcessId id, CancellationToken cancellationToken = default) =>
        inner.ThreadsAsync(id, cancellationToken);

    public ValueTask<string?> CommandLineAsync(ProcessId id, CancellationToken cancellationToken = default) =>
        inner.CommandLineAsync(id, cancellationToken);

    public ValueTask<string?> CurrentDirectoryAsync(ProcessId id, CancellationToken cancellationToken = default) =>
        inner.CurrentDirectoryAsync(id, cancellationToken);

    public ValueTask<IReadOnlyList<string>> StringsAsync(ProcessId id, CancellationToken cancellationToken = default) =>
        inner.StringsAsync(id, cancellationToken);

    public async ValueTask<IReadOnlyList<ModuleInfo>> ModulesAsync(
        ProcessId id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.ModulesAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException e) when (e.Kind == ProviderErrorKind.NotPermitted)
        {
            var viaHelper = await client.ReadModulesAsync(id, cancellationToken).ConfigureAwait(false);

            // No helper, or it refused too: the original refusal is the honest
            // answer, and the UI already knows how to explain it.
            if (viaHelper is null)
            {
                throw;
            }

            return viaHelper;
        }
    }

    public async ValueTask<IReadOnlyList<FileDescriptorInfo>> FileDescriptorsAsync(
        ProcessId id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.FileDescriptorsAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException e) when (e.Kind == ProviderErrorKind.NotPermitted)
        {
            var viaHelper = await client.ReadFileDescriptorsAsync(id, cancellationToken).ConfigureAwait(false);

            // No helper, or it refused too: the original refusal is the honest
            // answer, and the UI already knows how to explain it.
            if (viaHelper is null)
            {
                throw;
            }

            return viaHelper;
        }
    }

    public async ValueTask<IReadOnlyDictionary<string, string>> EnvironmentAsync(
        ProcessId id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.EnvironmentAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException e) when (e.Kind == ProviderErrorKind.NotPermitted)
        {
            var viaHelper = await client.ReadEnvironmentAsync(id, cancellationToken).ConfigureAwait(false);

            // No helper, or it refused too: the original refusal is the honest
            // answer, and the UI already knows how to explain it.
            if (viaHelper is null)
            {
                throw;
            }

            return viaHelper;
        }
    }
}

/// <summary>Deserialisation of the structured helper payloads.</summary>
internal static class HelperPayload
{
    internal static IReadOnlyList<ModuleInfo>? Modules(string json) =>
        Deserialize(json, HelperJsonContext.Default.IReadOnlyListModuleInfo);

    internal static IReadOnlyList<FileDescriptorInfo>? FileDescriptors(string json) =>
        Deserialize(json, HelperJsonContext.Default.IReadOnlyListFileDescriptorInfo);

    private static T? Deserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        try
        {
            return JsonSerializer.Deserialize(json, type);
        }
        catch (JsonException)
        {
            // A helper speaking a protocol we do not understand is indistinguishable
            // from one that is not there.
            return default;
        }
    }
}
