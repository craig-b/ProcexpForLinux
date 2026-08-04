namespace Procexp.Model;

/// <summary>System-wide statistics sampled at one instant.</summary>
public sealed record SystemStats
{
    /// <summary>Aggregate CPU usage, 0 to 100, averaged across cores.</summary>
    public double CpuTotalPercent { get; init; }

    /// <summary>Per-core usage, each 0 to 100.</summary>
    public IReadOnlyList<double> PerCoreCpuPercent { get; init; } = [];

    public ulong MemoryUsed { get; init; }
    public ulong MemoryTotal { get; init; }

    /// <summary>
    /// Unreclaimable kernel memory — the closest analog of the macOS "wired"
    /// figure. Summed from Slab, KernelStack, PageTables and Unevictable.
    /// </summary>
    public ulong MemoryKernel { get; init; }

    public ulong MemoryCached { get; init; }

    /// <summary>Compressed memory held by zram or zswap; zero when neither is active.</summary>
    public ulong MemoryCompressed { get; init; }

    public ulong SwapUsed { get; init; }
    public ulong SwapTotal { get; init; }

    public ulong DiskBytesPerSec { get; init; }
    public ulong NetworkBytesPerSec { get; init; }
    public double? GpuPercent { get; init; }

    public int ProcessCount { get; init; }
    public int ThreadCount { get; init; }

    /// <summary>System-wide open file descriptors, from <c>/proc/sys/fs/file-nr</c>.</summary>
    public int HandleCount { get; init; }

    public static readonly SystemStats Zero = new();
}

/// <summary>
/// Fixed-capacity ring backing the history graphs. Appending past capacity drops
/// the oldest sample. <see cref="Values"/> returns oldest to newest.
/// </summary>
public sealed class HistoryRing<T>
{
    private readonly T[] _storage;
    private int _start;
    private int _count;

    public HistoryRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 0);
        Capacity = capacity;
        _storage = new T[capacity];
    }

    public int Capacity { get; }
    public int Count => _count;
    public bool IsEmpty => _count == 0;

    public void Append(T value)
    {
        if (_count < Capacity)
        {
            _storage[(_start + _count) % Capacity] = value;
            _count++;
        }
        else
        {
            _storage[_start] = value;
            _start = (_start + 1) % Capacity;
        }
    }

    /// <summary>Samples ordered oldest to newest.</summary>
    public T[] Values
    {
        get
        {
            var result = new T[_count];
            for (var i = 0; i < _count; i++)
            {
                result[i] = _storage[(_start + i) % Capacity];
            }

            return result;
        }
    }

    public T? Latest => _count == 0 ? default : _storage[(_start + _count - 1) % Capacity];

    public void Clear()
    {
        _start = 0;
        _count = 0;
        Array.Clear(_storage);
    }
}
