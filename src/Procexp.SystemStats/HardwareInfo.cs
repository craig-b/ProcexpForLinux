using System.Globalization;
using System.Net.NetworkInformation;

namespace Procexp.Metrics;

/// <summary>One mounted filesystem, for the System Information window.</summary>
public sealed record VolumeInfo
{
    public required string MountPoint { get; init; }
    public required string Device { get; init; }
    public required string FileSystem { get; init; }
    public ulong TotalBytes { get; init; }
    public ulong AvailableBytes { get; init; }
    public bool IsBootVolume { get; init; }
}

/// <summary>One network interface.</summary>
public sealed record NetworkInterfaceInfo
{
    public required string Name { get; init; }
    public string? MacAddress { get; init; }
    public IReadOnlyList<string> Addresses { get; init; } = [];
    public bool IsUp { get; init; }
    public bool IsLoopback { get; init; }
    public long? SpeedMbps { get; init; }
    public int? Mtu { get; init; }
}

/// <summary>One graphics device.</summary>
public sealed record GpuInfo
{
    public required string Name { get; init; }
    public string? Driver { get; init; }
    public string? PciId { get; init; }
    public ulong? MemoryBytes { get; init; }
}

/// <summary>
/// Static machine facts for the System Information window: CPU topology and
/// caches, memory, storage, network interfaces, graphics, and OS identity.
/// </summary>
/// <remarks>
/// The Linux counterpart of the macOS <c>HardwareInfo</c>, which reads sysctl,
/// IOKit and Metal. Everything here comes from <c>/proc</c>, <c>/sys</c> and
/// <c>/etc/os-release</c>.
/// </remarks>
public sealed record HardwareInfo
{
    public string CpuModel { get; init; } = "";
    public string? CpuVendor { get; init; }
    public int PhysicalCores { get; init; }
    public int LogicalCores { get; init; }
    public int Sockets { get; init; }
    public double? CpuMhz { get; init; }
    public double? CpuMaxMhz { get; init; }
    public IReadOnlyList<string> CpuFlags { get; init; } = [];

    public ulong? L1DataCache { get; init; }
    public ulong? L1InstructionCache { get; init; }
    public ulong? L2Cache { get; init; }
    public ulong? L3Cache { get; init; }

    public ulong MemoryTotal { get; init; }
    public int PageSize { get; init; }

    public string? KernelVersion { get; init; }
    public string? DistributionName { get; init; }
    public string? Architecture { get; init; }
    public string? Hostname { get; init; }
    public DateTimeOffset? BootTime { get; init; }

    public IReadOnlyList<VolumeInfo> Volumes { get; init; } = [];
    public IReadOnlyList<NetworkInterfaceInfo> NetworkInterfaces { get; init; } = [];
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];

    public static HardwareInfo Gather()
    {
        var cpu = ReadCpuInfo();

        return new HardwareInfo
        {
            CpuModel = cpu.Model,
            CpuVendor = cpu.Vendor,
            PhysicalCores = cpu.PhysicalCores,
            LogicalCores = Environment.ProcessorCount,
            Sockets = cpu.Sockets,
            CpuMhz = cpu.Mhz,
            CpuMaxMhz = ReadMaxFrequencyMhz(),
            CpuFlags = cpu.Flags,
            L1DataCache = ReadCacheSize("index0"),
            L1InstructionCache = ReadCacheSize("index1"),
            L2Cache = ReadCacheSize("index2"),
            L3Cache = ReadCacheSize("index3"),
            MemoryTotal = ReadMemTotal(),
            PageSize = Environment.SystemPageSize,
            KernelVersion = ProcReader.ReadText("/proc/sys/kernel/osrelease"),
            DistributionName = ReadDistributionName(),
            Architecture =
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            Hostname = ProcReader.ReadText("/proc/sys/kernel/hostname"),
            BootTime = ReadBootTime(),
            Volumes = ReadVolumes(),
            NetworkInterfaces = ReadNetworkInterfaces(),
            Gpus = ReadGpus(),
        };
    }

    private readonly record struct CpuFacts(
        string Model,
        string? Vendor,
        int PhysicalCores,
        int Sockets,
        double? Mhz,
        IReadOnlyList<string> Flags
    );

    /// <summary>
    /// Parse <c>/proc/cpuinfo</c>.
    /// </summary>
    /// <remarks>
    /// Physical core count needs care: cpuinfo lists one block per logical CPU, so
    /// counting blocks gives the SMT-inflated number. The honest count is the
    /// number of distinct (physical id, core id) pairs, which collapses
    /// hyperthread siblings onto the core they share.
    /// </remarks>
    private static CpuFacts ReadCpuInfo()
    {
        var model = "";
        string? vendor = null;
        double? mhz = null;
        IReadOnlyList<string> flags = [];

        var physicalCores = new HashSet<(string Socket, string Core)>();
        var sockets = new HashSet<string>();
        var currentSocket = "";
        string? armImplementer = null;
        string? armPart = null;

        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
            {
                var colon = line.IndexOf(':');
                if (colon < 0)
                {
                    continue;
                }

                var key = line.AsSpan(..colon).Trim();
                var value = line.AsSpan((colon + 1)..).Trim().ToString();

                if (key.SequenceEqual("model name") && model.Length == 0)
                {
                    model = value;
                }
                else if (key.SequenceEqual("vendor_id") && vendor is null)
                {
                    vendor = value;
                }
                else if (
                    key.SequenceEqual("cpu MHz")
                    && mhz is null
                    && double.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
                )
                {
                    mhz = parsed;
                }
                else if (key.SequenceEqual("flags") && flags.Count == 0)
                {
                    flags = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                }
                else if (key.SequenceEqual("physical id"))
                {
                    currentSocket = value;
                    sockets.Add(value);
                }
                else if (key.SequenceEqual("core id"))
                {
                    physicalCores.Add((currentSocket, value));
                }
                // ARM parts do not publish "model name"; assemble something useful.
                else if (key.SequenceEqual("Model") && model.Length == 0)
                {
                    model = value;
                }
                // Server-class ARM has neither: only implementer and part codes,
                // and "Features" where x86 says "flags".
                else if (key.SequenceEqual("CPU implementer") && armImplementer is null)
                {
                    armImplementer = value;
                }
                else if (key.SequenceEqual("CPU part") && armPart is null)
                {
                    armPart = value;
                }
                else if (key.SequenceEqual("Features") && flags.Count == 0)
                {
                    flags = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fall through with whatever was gathered.
        }

        if (model.Length == 0 && armPart is not null)
        {
            model = DecodeArmCore(armImplementer, armPart);
        }

        return new CpuFacts(
            model.Length > 0 ? model : "Unknown CPU",
            vendor,
            physicalCores.Count > 0 ? physicalCores.Count : Environment.ProcessorCount,
            sockets.Count > 0 ? sockets.Count : 1,
            mhz,
            flags
        );
    }

    /// <summary>
    /// Turn ARM's implementer and part codes into a name, the way lscpu does
    /// from the same (much longer) table. Unknown codes degrade to the raw
    /// hex rather than to "Unknown CPU", since the codes still identify the
    /// machine to anyone who can look them up.
    /// </summary>
    private static string DecodeArmCore(string? implementer, string part)
    {
        var maker = implementer switch
        {
            "0x41" => "ARM",
            "0x42" => "Broadcom",
            "0x43" => "Cavium",
            "0x46" => "Fujitsu",
            "0x48" => "HiSilicon",
            "0x4e" => "NVIDIA",
            "0x51" => "Qualcomm",
            "0x53" => "Samsung",
            "0x61" => "Apple",
            "0xc0" => "Ampere",
            _ => null,
        };

        var core = (maker, part) switch
        {
            ("ARM", "0xd03") => "Cortex-A53",
            ("ARM", "0xd04") => "Cortex-A35",
            ("ARM", "0xd05") => "Cortex-A55",
            ("ARM", "0xd07") => "Cortex-A57",
            ("ARM", "0xd08") => "Cortex-A72",
            ("ARM", "0xd09") => "Cortex-A73",
            ("ARM", "0xd0a") => "Cortex-A75",
            ("ARM", "0xd0b") => "Cortex-A76",
            ("ARM", "0xd0c") => "Neoverse-N1",
            ("ARM", "0xd0d") => "Cortex-A77",
            ("ARM", "0xd40") => "Neoverse-V1",
            ("ARM", "0xd41") => "Cortex-A78",
            ("ARM", "0xd44") => "Cortex-X1",
            ("ARM", "0xd46") => "Cortex-A510",
            ("ARM", "0xd47") => "Cortex-A710",
            ("ARM", "0xd48") => "Cortex-X2",
            ("ARM", "0xd49") => "Neoverse-N2",
            ("ARM", "0xd4a") => "Neoverse-E1",
            ("ARM", "0xd4d") => "Cortex-A715",
            ("ARM", "0xd4e") => "Cortex-X3",
            ("ARM", "0xd4f") => "Neoverse-V2",
            ("ARM", "0xd84") => "Neoverse-V3",
            ("ARM", "0xd8e") => "Neoverse-N3",
            ("Ampere", "0xac3") => "AmpereOne",
            _ => null,
        };

        return (maker, core) switch
        {
            (not null, not null) => $"{maker} {core}",
            (not null, null) => $"{maker} part {part}",
            _ => $"ARM implementer {implementer ?? "?"} part {part}",
        };
    }

    private static double? ReadMaxFrequencyMhz()
    {
        var text = ProcReader.ReadText("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq");
        return text is not null && double.TryParse(text, CultureInfo.InvariantCulture, out var khz)
            ? khz / 1000.0
            : null;
    }

    /// <summary>Read a cache size from sysfs, where it is given as e.g. "32K".</summary>
    private static ulong? ReadCacheSize(string index)
    {
        var text = ProcReader.ReadText($"/sys/devices/system/cpu/cpu0/cache/{index}/size");
        if (text is null || text.Length == 0)
        {
            return null;
        }

        var multiplier = text[^1] switch
        {
            'K' or 'k' => 1024UL,
            'M' or 'm' => 1024UL * 1024,
            'G' or 'g' => 1024UL * 1024 * 1024,
            _ => 1UL,
        };

        var digits = multiplier == 1 ? text : text[..^1];
        return ulong.TryParse(digits, out var value) ? value * multiplier : null;
    }

    private static ulong ReadMemTotal()
    {
        var buffer = ProcReader.Rent();
        try
        {
            if (!ProcReader.TryRead("/proc/meminfo", ref buffer, out var length))
            {
                return 0;
            }

            foreach (var line in ProcReader.Lines(buffer.AsSpan(0, length)))
            {
                if (line.StartsWith("MemTotal:"u8))
                {
                    var rest = line["MemTotal:".Length..];
                    return ProcReader.ParseUInt64(ProcReader.NextField(ref rest)) * 1024;
                }
            }

            return 0;
        }
        finally
        {
            ProcReader.Return(buffer);
        }
    }

    private static string? ReadDistributionName()
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/os-release"))
            {
                if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                {
                    return line["PRETTY_NAME=".Length..].Trim('"');
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not every system has os-release.
        }

        return null;
    }

    private static DateTimeOffset? ReadBootTime()
    {
        var buffer = ProcReader.Rent(32768);
        try
        {
            if (!ProcReader.TryRead("/proc/stat", ref buffer, out var length))
            {
                return null;
            }

            foreach (var line in ProcReader.Lines(buffer.AsSpan(0, length)))
            {
                if (line.StartsWith("btime "u8))
                {
                    var rest = line["btime ".Length..];
                    return DateTimeOffset.FromUnixTimeSeconds((long)ProcReader.ParseUInt64(rest));
                }
            }

            return null;
        }
        finally
        {
            ProcReader.Return(buffer);
        }
    }

    /// <summary>
    /// Mounted filesystems worth showing. Pseudo and virtual filesystems are
    /// filtered out — a list padded with cgroup, tmpfs and overlay mounts buries
    /// the handful of real disks the user came to see.
    /// </summary>
    private static IReadOnlyList<VolumeInfo> ReadVolumes()
    {
        var result = new List<VolumeInfo>();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var format = drive.DriveFormat;
                if (IsPseudoFileSystem(format))
                {
                    continue;
                }

                result.Add(
                    new VolumeInfo
                    {
                        MountPoint = drive.RootDirectory.FullName,
                        Device = drive.Name,
                        FileSystem = format,
                        TotalBytes = (ulong)Math.Max(0, drive.TotalSize),
                        AvailableBytes = (ulong)Math.Max(0, drive.AvailableFreeSpace),
                        IsBootVolume = drive.RootDirectory.FullName == "/",
                    }
                );
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Return whatever was collected.
        }

        return result;
    }

    private static bool IsPseudoFileSystem(string format) =>
        format switch
        {
            "proc"
            or "sysfs"
            or "devtmpfs"
            or "devpts"
            or "tmpfs"
            or "cgroup"
            or "cgroup2"
            or "securityfs"
            or "pstore"
            or "bpf"
            or "configfs"
            or "debugfs"
            or "tracefs"
            or "hugetlbfs"
            or "mqueue"
            or "fusectl"
            or "binfmt_misc"
            or "autofs"
            or "ramfs"
            or "efivarfs"
            or "squashfs"
            or "overlay"
            or "nsfs" => true,
            _ => false,
        };

    private static IReadOnlyList<NetworkInterfaceInfo> ReadNetworkInterfaces()
    {
        var result = new List<NetworkInterfaceInfo>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var addresses = new List<string>();
                try
                {
                    foreach (var address in nic.GetIPProperties().UnicastAddresses)
                    {
                        addresses.Add(address.Address.ToString());
                    }
                }
                catch (Exception e)
                    when (e is PlatformNotSupportedException or NetworkInformationException)
                {
                    // Some virtual interfaces refuse property queries.
                }

                var mac = nic.GetPhysicalAddress().ToString();

                result.Add(
                    new NetworkInterfaceInfo
                    {
                        Name = nic.Name,
                        MacAddress =
                            mac.Length == 12
                                ? string.Join(
                                    ':',
                                    Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2))
                                )
                                : null,
                        Addresses = addresses,
                        IsUp = nic.OperationalStatus == OperationalStatus.Up,
                        IsLoopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                        SpeedMbps = nic.Speed > 0 ? nic.Speed / 1_000_000 : null,
                        Mtu = ReadInterfaceMtu(nic.Name),
                    }
                );
            }
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
        {
            // Return whatever was collected.
        }

        return result;
    }

    private static int? ReadInterfaceMtu(string name)
    {
        var text = ProcReader.ReadText($"/sys/class/net/{name}/mtu");
        return text is not null && int.TryParse(text, out var mtu) ? mtu : null;
    }

    /// <summary>
    /// Graphics devices, from the DRM subsystem in sysfs. Replaces the macOS
    /// Metal enumeration.
    /// </summary>
    private static IReadOnlyList<GpuInfo> ReadGpus()
    {
        var result = new List<GpuInfo>();

        try
        {
            foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card*"))
            {
                // Skip connector entries like card0-DP-1, which are outputs rather
                // than devices.
                if (Path.GetFileName(card).Contains('-'))
                {
                    continue;
                }

                var device = Path.Combine(card, "device");
                var vendorId = ProcReader.ReadText(Path.Combine(device, "vendor"));
                var deviceId = ProcReader.ReadText(Path.Combine(device, "device"));
                var driver = ProcFile.ReadLinkName(Path.Combine(device, "driver"));

                result.Add(
                    new GpuInfo
                    {
                        Name = DescribeGpu(vendorId, deviceId, driver),
                        Driver = driver,
                        PciId =
                            vendorId is not null && deviceId is not null
                                ? $"{vendorId}:{deviceId}"
                                : null,
                        MemoryBytes = ReadGpuMemory(device),
                    }
                );
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No DRM devices, or a headless system.
        }

        return result;
    }

    private static ulong? ReadGpuMemory(string devicePath)
    {
        // amdgpu publishes this; most other drivers do not.
        var text = ProcReader.ReadText(Path.Combine(devicePath, "mem_info_vram_total"));
        return text is not null && ulong.TryParse(text, out var bytes) ? bytes : null;
    }

    private static string DescribeGpu(string? vendorId, string? deviceId, string? driver)
    {
        var vendor = vendorId?.ToLowerInvariant() switch
        {
            "0x1002" => "AMD",
            "0x10de" => "NVIDIA",
            "0x8086" => "Intel",
            "0x1af4" => "Virtio",
            "0x15ad" => "VMware",
            _ => null,
        };

        if (vendor is not null)
        {
            return driver is not null ? $"{vendor} ({driver})" : vendor;
        }

        return driver ?? deviceId ?? "Unknown GPU";
    }
}

/// <summary>Symlink helpers shared by the hardware probes.</summary>
internal static class ProcFile
{
    /// <summary>The final component of a symlink target, e.g. the driver name.</summary>
    internal static string? ReadLinkName(string path)
    {
        try
        {
            var target = File.ResolveLinkTarget(path, returnFinalTarget: false);
            return target is null ? null : Path.GetFileName(target.FullName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
