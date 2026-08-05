using System.Runtime.CompilerServices;
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
        inner.Capabilities
        | ProviderCapabilities.CrossUser
        | ProviderCapabilities.Environment
        | ProviderCapabilities.ProcessIo
        | ProviderCapabilities.ProportionalMemory;

    public async IAsyncEnumerable<ProcessSnapshot> Snapshots(
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await foreach (
            var snapshot in inner.Snapshots(interval, cancellationToken).ConfigureAwait(false)
        )
        {
            yield return EnrichIo(snapshot);
        }
    }

    public async ValueTask<ProcessSnapshot> SnapshotAsync(
        CancellationToken cancellationToken = default
    ) => EnrichIo(await inner.SnapshotAsync(cancellationToken).ConfigureAwait(false));

    // --- Sweep enrichment ---------------------------------------------------
    //
    // The sweep cannot read other users' /proc/PID/io, so those records arrive
    // with null I/O counters and the LimitedInfo flag. The helper can read
    // them, but the sweep visits hundreds of processes a second and a socket
    // round trip per restricted process would put the daemon on the hot path.
    // So sweeps only read this cache, and a single background wave refreshes
    // stale entries between sweeps. The columns are cumulative bytes, so a
    // value one refresh old is indistinguishable from a fresh one.

    private static readonly TimeSpan RefreshAge = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan QuietAfterFailure = TimeSpan.FromSeconds(30);

    private readonly Lock _ioGate = new();
    private readonly Dictionary<ProcessId, CachedIo> _io = [];
    private int _refreshing;
    private DateTimeOffset _quietUntil = DateTimeOffset.MinValue;

    private sealed record CachedIo(ulong? Read, ulong? Written, DateTimeOffset At);

    private ProcessSnapshot EnrichIo(ProcessSnapshot snapshot)
    {
        List<ProcessId>? stale = null;
        Dictionary<ProcessId, ProcessRecord>? patched = null;
        var now = DateTimeOffset.UtcNow;

        lock (_ioGate)
        {
            // Entries for exited processes are dropped here rather than aging
            // out, so the cache always tracks the live process set.
            List<ProcessId>? gone = null;
            foreach (var id in _io.Keys)
            {
                if (!snapshot.Processes.ContainsKey(id))
                {
                    (gone ??= []).Add(id);
                }
            }

            if (gone is not null)
            {
                foreach (var id in gone)
                {
                    _io.Remove(id);
                }
            }

            var refreshDue = now >= _quietUntil;
            foreach (var (id, record) in snapshot.Processes)
            {
                if (
                    record.DiskBytesRead is not null
                    || !record.Flags.HasFlag(ProcessFlags.LimitedInfo)
                    || record.Flags.HasFlag(ProcessFlags.KernelThread)
                )
                {
                    continue;
                }

                if (_io.TryGetValue(id, out var cached))
                {
                    if (cached.Read is not null || cached.Written is not null)
                    {
                        patched ??= new Dictionary<ProcessId, ProcessRecord>(snapshot.Processes);
                        patched[id] = record with
                        {
                            DiskBytesRead = cached.Read,
                            DiskBytesWritten = cached.Written,
                        };
                    }

                    if (refreshDue && now - cached.At >= RefreshAge)
                    {
                        (stale ??= []).Add(id);
                    }
                }
                else if (refreshDue)
                {
                    (stale ??= []).Add(id);
                }
            }
        }

        ScheduleRefresh(stale);

        return patched is null
            ? snapshot
            : new ProcessSnapshot
            {
                Timestamp = snapshot.Timestamp,
                Interval = snapshot.Interval,
                Processes = patched,
                Roots = snapshot.Roots,
                Children = snapshot.Children,
                System = snapshot.System,
            };
    }

    private void ScheduleRefresh(List<ProcessId>? stale)
    {
        // One wave at a time: if the previous one is still draining, this
        // sweep's stale list is simply picked up by a later sweep.
        if (stale is null || Interlocked.Exchange(ref _refreshing, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() => RefreshAsync(stale));
    }

    private async Task RefreshAsync(List<ProcessId> stale)
    {
        try
        {
            // A few connections at a time — enough to drain a few hundred
            // restricted processes well inside a sweep interval, without
            // hammering the daemon.
            const int Lanes = 4;
            for (var i = 0; i < stale.Count; i += Lanes)
            {
                await Task.WhenAll(stale.Skip(i).Take(Lanes).Select(FetchIoAsync))
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private async Task FetchIoAsync(ProcessId id)
    {
        try
        {
            var io = await client.ReadIoAsync(id).ConfigureAwait(false);
            lock (_ioGate)
            {
                _io[id] = new CachedIo(io?.Read, io?.Written, DateTimeOffset.UtcNow);
            }
        }
        catch (ProviderException e)
        {
            lock (_ioGate)
            {
                _io[id] = new CachedIo(null, null, DateTimeOffset.UtcNow);
                if (e.Kind == ProviderErrorKind.HelperUnavailable)
                {
                    // Socket gone or refused: stop asking for a while rather
                    // than failing once per restricted process per wave.
                    _quietUntil = DateTimeOffset.UtcNow + QuietAfterFailure;
                }
            }
        }
    }

    // Threads, command lines and working directories are readable cross-user, so
    // they never need the helper.
    public ValueTask<IReadOnlyList<ThreadInfo>> ThreadsAsync(
        ProcessId id,
        CancellationToken cancellationToken = default
    ) => inner.ThreadsAsync(id, cancellationToken);

    public ValueTask<string?> CommandLineAsync(
        ProcessId id,
        CancellationToken cancellationToken = default
    ) => inner.CommandLineAsync(id, cancellationToken);

    public ValueTask<string?> CurrentDirectoryAsync(
        ProcessId id,
        CancellationToken cancellationToken = default
    ) => inner.CurrentDirectoryAsync(id, cancellationToken);

    public ValueTask<IReadOnlyList<string>> StringsAsync(
        ProcessId id,
        CancellationToken cancellationToken = default
    ) => inner.StringsAsync(id, cancellationToken);

    public async ValueTask<IReadOnlyList<ModuleInfo>> ModulesAsync(
        ProcessId id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await inner.ModulesAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException e) when (e.Kind == ProviderErrorKind.NotPermitted)
        {
            var viaHelper = await client
                .ReadModulesAsync(id, cancellationToken)
                .ConfigureAwait(false);

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
        ProcessId id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await inner.FileDescriptorsAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException e) when (e.Kind == ProviderErrorKind.NotPermitted)
        {
            var viaHelper = await client
                .ReadFileDescriptorsAsync(id, cancellationToken)
                .ConfigureAwait(false);

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
        ProcessId id,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await inner.EnvironmentAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException e) when (e.Kind == ProviderErrorKind.NotPermitted)
        {
            var viaHelper = await client
                .ReadEnvironmentAsync(id, cancellationToken)
                .ConfigureAwait(false);

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

    private static T? Deserialize<T>(
        string json,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type
    )
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
