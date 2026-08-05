using System.Collections.Concurrent;
using Procexp.Autostart;
using Procexp.Gpu;
using Procexp.Model;
using Procexp.Provenance;

namespace Procexp.App;

/// <summary>
/// Fills in the per-process detail that is too slow to gather during a sweep.
/// </summary>
/// <remarks>
/// The counterpart of the macOS AppModel's enrichment passes. The sweep itself
/// must stay at tens of milliseconds, but several columns need work measured in
/// whole seconds — querying the package database shells out per binary, and the
/// autostart index walks several hundred unit and desktop files.
///
/// So the sweep publishes immediately and this fills the columns in behind it.
/// <see cref="Enrich"/> is a pure cache lookup and runs on the UI thread; the
/// caches are populated by background workers that never block a frame. Columns
/// appear blank for a moment after launch and then fill, which is the honest
/// behaviour — the alternative is a list that stalls until the slowest package
/// query returns.
///
/// Keyed by executable path rather than by process. Description, company, version
/// and provenance are properties of an image, so ninety Chrome helpers share one
/// lookup instead of provoking ninety.
/// </remarks>
public sealed class ProcessEnricher : IDisposable
{
    private readonly ProvenanceProvider _provenance = new();
    private readonly AutostartProvider _autostart = new();
    private readonly GpuProvider _gpu = new();

    /// <summary>Image-keyed metadata: the expensive half.</summary>
    private readonly ConcurrentDictionary<string, ImageFacts> _byPath = new(StringComparer.Ordinal);

    /// <summary>Process-keyed autostart location.</summary>
    private readonly ConcurrentDictionary<ProcessId, string?> _autostartByProcess = new();

    /// <summary>Per-process GPU busy, refreshed on its own slow cadence.</summary>
    private IReadOnlyDictionary<ProcessId, double> _gpuUsage = new Dictionary<ProcessId, double>();

    private IReadOnlyDictionary<ProcessId, ulong> _gpuMemory = new Dictionary<ProcessId, ulong>();

    /// <summary>Paths queued for lookup, so a path is never queried twice.</summary>
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _lookupSlots = new(2);

    /// <summary>One image hashed at a time; see <see cref="QueueVirusTotalLookup"/>.</summary>
    private readonly SemaphoreSlim _hashSlot = new(1);

    /// <summary>What we know about an image, once someone has looked.</summary>
    private sealed record ImageFacts(
        string? Description,
        string? Company,
        string? Version,
        ProvenanceInfo? Provenance
    );

    public ProcessEnricher()
    {
        _ = RunGpuLoopAsync();
    }

    /// <summary>Raised when new detail has arrived and the list should repaint.</summary>
    public event EventHandler? Updated;

    /// <summary>
    /// Merge whatever is cached into a snapshot, and queue lookups for whatever
    /// is not. Cheap enough to call on the UI thread every refresh.
    /// </summary>
    public ProcessSnapshot Enrich(ProcessSnapshot snapshot)
    {
        var enriched = new Dictionary<ProcessId, ProcessRecord>(snapshot.Processes.Count);

        foreach (var (id, record) in snapshot.Processes)
        {
            var updated = record;

            if (LookupPathFor(record) is { Length: > 0 } path)
            {
                if (_byPath.TryGetValue(path, out var facts))
                {
                    updated = updated with
                    {
                        Description = facts.Description,
                        Company = facts.Company,
                        Version = facts.Version,
                        Provenance = facts.Provenance,
                    };
                }
                else
                {
                    QueueImageLookup(path);
                }
            }

            if (_autostartByProcess.TryGetValue(id, out var autostart))
            {
                updated = updated with { AutostartLocation = autostart };
            }
            else
            {
                QueueAutostartLookup(record);
            }

            if (_gpuUsage.TryGetValue(id, out var gpu))
            {
                updated = updated with { GpuPercent = gpu };
            }

            if (_gpuMemory.TryGetValue(id, out var gpuMemory))
            {
                updated = updated with { GpuMemoryBytes = gpuMemory };
            }

            enriched[id] = updated;
        }

        // Processes come and go constantly; without this the autostart cache
        // would grow for the life of the session.
        if (_autostartByProcess.Count > snapshot.Processes.Count * 2)
        {
            foreach (
                var id in _autostartByProcess.Keys.Where(k => !snapshot.Processes.ContainsKey(k))
            )
            {
                _autostartByProcess.TryRemove(id, out _);
            }
        }

        return new ProcessSnapshot
        {
            Timestamp = snapshot.Timestamp,
            Interval = snapshot.Interval,
            Processes = enriched,
            Roots = snapshot.Roots,
            Children = snapshot.Children,
            System = snapshot.System,
        };
    }

    /// <summary>
    /// Look an image up in the background.
    /// </summary>
    /// <remarks>
    /// Bounded to two concurrent lookups. Each one may spawn a package-manager
    /// process, and letting several hundred of those loose at once on a first
    /// sweep would be worse for the machine than the tool observing it.
    /// </remarks>
    private void QueueImageLookup(string path)
    {
        if (!_pending.TryAdd(path, 0))
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _lookupSlots.WaitAsync(_lifetime.Token).ConfigureAwait(false);

                    try
                    {
                        var info = await _provenance
                            .ProvenanceAsync(path, _lifetime.Token)
                            .ConfigureAwait(false);

                        _byPath[path] = new ImageFacts(
                            Description: DescriptionFor(path, info),
                            Company: info.Packager ?? info.Repository,
                            Version: info.PackageVersion,
                            Provenance: info
                        );

                        Updated?.Invoke(this, EventArgs.Empty);

                        if (_provenance.VirusTotalConfigured)
                        {
                            QueueVirusTotalLookup(path, info);
                        }
                    }
                    finally
                    {
                        _lookupSlots.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutting down.
                }
                finally
                {
                    _pending.TryRemove(path, out _);
                }
            },
            CancellationToken.None
        );
    }

    /// <summary>
    /// Check an image against VirusTotal in the background.
    /// </summary>
    /// <remarks>
    /// Opt-in: runs only when an API key is configured, and sends nothing but the
    /// SHA-256 — the file itself is never uploaded. Hashing is bounded to one
    /// image at a time because a first sweep would otherwise hash every binary on
    /// the system at once; the requests themselves are serialised behind the
    /// client's four-a-minute limiter, so the column fills gradually and the
    /// on-disk cache makes later sessions immediate.
    /// </remarks>
    private void QueueVirusTotalLookup(string path, ProvenanceInfo info)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _hashSlot.WaitAsync(_lifetime.Token).ConfigureAwait(false);

                    string? sha;
                    try
                    {
                        sha = await ProvenanceProvider
                            .ComputeSha256Async(path, _lifetime.Token)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        _hashSlot.Release();
                    }

                    if (sha is null)
                    {
                        return;
                    }

                    var vt = await _provenance
                        .VirusTotalAsync(sha, _lifetime.Token)
                        .ConfigureAwait(false);

                    if (vt is null || !_byPath.TryGetValue(path, out var facts))
                    {
                        return;
                    }

                    _byPath[path] = facts with
                    {
                        Provenance = info with { Sha256 = sha, VirusTotal = vt },
                    };

                    Updated?.Invoke(this, EventArgs.Empty);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down.
                }
                catch (ProviderException)
                {
                    // Network or quota trouble; the column simply stays blank.
                }
            },
            CancellationToken.None
        );
    }

    /// <summary>
    /// The path to look an image up by.
    /// </summary>
    /// <remarks>
    /// Prefers the resolved executable, but falls back to the first token of the
    /// command line. This matters far more than it sounds: readlink on
    /// <c>/proc/PID/exe</c> is gated by ptrace_may_access, so every process owned
    /// by another user — which on a desktop is most of the interesting system
    /// ones — has no executable path at all. Without the fallback, Description,
    /// Company and Verified Signer stay blank for exactly the processes a user
    /// most wants identified.
    ///
    /// <c>/proc/PID/cmdline</c> is world-readable, and argv[0] is the image path
    /// for essentially everything systemd starts. It is a weaker claim — argv[0]
    /// can be set to anything — so it is used only to look up metadata, never to
    /// populate <see cref="ProcessRecord.ExecutablePath"/>, which stays honest
    /// about what the kernel actually told us.
    /// </remarks>
    internal static string? LookupPathFor(ProcessRecord record)
    {
        if (record.ExecutablePath is { Length: > 0 } resolved)
        {
            return resolved;
        }

        if (record.CommandLine is not { Length: > 0 } commandLine)
        {
            return null;
        }

        var space = commandLine.IndexOf(' ');
        var candidate = space < 0 ? commandLine : commandLine[..space];

        // Only absolute paths, and only ones that exist. A relative argv[0] or a
        // process that rewrote it into a status line would otherwise send the
        // package manager on a pointless lookup for every refresh.
        return candidate.StartsWith('/') && File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// A human-readable description of an image.
    /// </summary>
    /// <remarks>
    /// The package summary where there is one. Failing that the file name, which
    /// is at least true — leaving it blank invites the reader to assume the column
    /// is broken rather than that the fact is unknown.
    /// </remarks>
    private static string? DescriptionFor(string path, ProvenanceInfo info)
    {
        // The package summary is the genuinely useful answer: "system and service
        // manager" tells the reader something the process name did not.
        if (info.PackageDescription is { Length: > 0 } summary)
        {
            return summary;
        }

        // Failing that the package name, but only when it differs from the file
        // name — repeating "systemd" next to systemd is column noise.
        return info.PackageName is { Length: > 0 } name && name != Path.GetFileName(path)
            ? name
            : null;
    }

    private void QueueAutostartLookup(ProcessRecord record)
    {
        // Mark it resolved straight away so a slow first index build does not
        // cause the same process to be queued on every refresh.
        if (!_autostartByProcess.TryAdd(record.Id, null))
        {
            return;
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var location = await _autostart
                        .AutostartLocationAsync(record, _lifetime.Token)
                        .ConfigureAwait(false);

                    if (location is not null)
                    {
                        _autostartByProcess[record.Id] = location;
                        Updated?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Shutting down.
                }
            },
            CancellationToken.None
        );
    }

    /// <summary>
    /// Refresh per-process GPU usage on its own cadence.
    /// </summary>
    /// <remarks>
    /// Five seconds rather than one. The walk has to inspect every open
    /// descriptor on the system to find the few that are DRM clients, and at the
    /// list's refresh rate that would cost a quarter of a core to discover that
    /// nothing changed.
    /// </remarks>
    private async Task RunGpuLoopAsync()
    {
        if (!_gpu.IsAvailable)
        {
            return;
        }

        using var timer = new PeriodicTimer(GpuProvider.RecommendedInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                var (percentages, memory) = await Task.Run(_gpu.Sample, _lifetime.Token)
                    .ConfigureAwait(false);

                _gpuUsage = percentages;
                _gpuMemory = memory;

                if (percentages.Count > 0 || memory.Count > 0)
                {
                    Updated?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Discard cached image facts, for when packages may have changed.</summary>
    public void Invalidate()
    {
        _byPath.Clear();
        _autostartByProcess.Clear();
        _autostart.Refresh();
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lookupSlots.Dispose();
        _hashSlot.Dispose();
    }
}
