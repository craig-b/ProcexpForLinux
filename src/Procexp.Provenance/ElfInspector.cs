using System.Buffers.Binary;

namespace Procexp.Provenance;

/// <summary>What the ELF header and notes tell us about an image.</summary>
public sealed record ElfFacts
{
    public bool IsElf { get; init; }
    public bool Is64Bit { get; init; }
    public bool IsLittleEndian { get; init; }

    /// <summary>GNU build-id as lowercase hex — a stable identity for the image.</summary>
    public string? BuildId { get; init; }

    /// <summary>True for a position-independent executable or a shared object.</summary>
    public bool IsSharedObject { get; init; }

    /// <summary>True when no symbol or debug sections remain.</summary>
    public bool IsStripped { get; init; }

    /// <summary>
    /// True when the image looks packed or self-modifying: a UPX signature, or a
    /// loadable segment that is both writable and executable. A heuristic, and
    /// labelled as such wherever it is shown.
    /// </summary>
    public bool IsLikelyPacked { get; init; }

    public static readonly ElfFacts NotElf = new();
}

/// <summary>
/// Minimal ELF reader for the fields the Properties window shows.
/// </summary>
/// <remarks>
/// Reads only the header, the program headers, and any PT_NOTE segments, so the
/// cost is a few small reads rather than mapping the whole image. The build-id is
/// the useful part: it identifies an image independently of its path or
/// modification time, which is what makes it a usable substitute for the code
/// directory hash the macOS build reads out of a signature.
/// </remarks>
public static class ElfInspector
{
    private const int ElfHeaderSize64 = 64;
    private const uint PtLoad = 1;
    private const uint PtNote = 4;
    private const uint NtGnuBuildId = 3;
    private const ushort EtDyn = 3;
    private const uint PfExecute = 1;
    private const uint PfWrite = 2;

    public static ElfFacts Inspect(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                4096
            );
            return Read(stream);
        }
        catch (Exception e)
            when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ElfFacts.NotElf;
        }
    }

    private static ElfFacts Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[ElfHeaderSize64];
        if (
            stream.ReadAtLeast(header, ElfHeaderSize64, throwOnEndOfStream: false) < ElfHeaderSize64
        )
        {
            return ElfFacts.NotElf;
        }

        if (
            header[0] != 0x7F
            || header[1] != (byte)'E'
            || header[2] != (byte)'L'
            || header[3] != (byte)'F'
        )
        {
            return ElfFacts.NotElf;
        }

        var is64Bit = header[4] == 2;
        var isLittleEndian = header[5] == 1;

        // Only 64-bit little-endian is parsed beyond the header. Every mainstream
        // Linux target is one, and mis-parsing the others would be worse than
        // admitting we did not look.
        if (!is64Bit || !isLittleEndian)
        {
            return new ElfFacts
            {
                IsElf = true,
                Is64Bit = is64Bit,
                IsLittleEndian = isLittleEndian,
            };
        }

        var type = BinaryPrimitives.ReadUInt16LittleEndian(header[16..]);
        var programHeaderOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[32..]);
        var programHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(header[54..]);
        var programHeaderCount = BinaryPrimitives.ReadUInt16LittleEndian(header[56..]);

        var (buildId, writableExecutableLoad) = ScanProgramHeaders(
            stream,
            programHeaderOffset,
            programHeaderSize,
            programHeaderCount
        );

        return new ElfFacts
        {
            IsElf = true,
            Is64Bit = true,
            IsLittleEndian = true,
            IsSharedObject = type == EtDyn,
            BuildId = buildId,
            IsLikelyPacked = writableExecutableLoad || HasUpxSignature(stream),
        };
    }

    /// <summary>
    /// Whether the UPX loader stub's magic appears near the front of the image.
    /// </summary>
    /// <remarks>
    /// UPX writes its <c>l_info</c> block directly after the program headers, so
    /// the first kilobyte is enough — scanning further would start matching the
    /// string in binaries that merely mention UPX.
    /// </remarks>
    private static bool HasUpxSignature(Stream stream)
    {
        Span<byte> head = stackalloc byte[1024];
        stream.Seek(0, SeekOrigin.Begin);
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

        return head[..read].IndexOf("UPX!"u8) >= 0;
    }

    private static (string? BuildId, bool WritableExecutableLoad) ScanProgramHeaders(
        Stream stream,
        long offset,
        int entrySize,
        int count
    )
    {
        if (offset <= 0 || entrySize < 56 || count is <= 0 or > 512)
        {
            return (null, false);
        }

        var entry = new byte[entrySize];
        string? buildId = null;
        var writableExecutableLoad = false;

        for (var i = 0; i < count; i++)
        {
            stream.Seek(offset + ((long)i * entrySize), SeekOrigin.Begin);
            if (stream.ReadAtLeast(entry, entrySize, throwOnEndOfStream: false) < entrySize)
            {
                return (buildId, writableExecutableLoad);
            }

            var type = BinaryPrimitives.ReadUInt32LittleEndian(entry);

            // A loadable segment mapped writable and executable is the classic
            // packer trait: the stub unpacks into memory it then jumps into.
            // Ordinary toolchain output never asks for W|X in the file itself.
            if (type == PtLoad)
            {
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(4));
                writableExecutableLoad |= (flags & (PfWrite | PfExecute)) == (PfWrite | PfExecute);
                continue;
            }

            if (type != PtNote || buildId is not null)
            {
                continue;
            }

            var noteOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(8));
            var noteSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(32));

            if (noteSize is <= 0 or > 65536)
            {
                continue;
            }

            var notes = new byte[noteSize];
            stream.Seek(noteOffset, SeekOrigin.Begin);
            if (stream.ReadAtLeast(notes, (int)noteSize, throwOnEndOfStream: false) < noteSize)
            {
                continue;
            }

            buildId = ScanNotes(notes);
        }

        return (buildId, writableExecutableLoad);
    }

    /// <summary>
    /// Walk a note segment looking for NT_GNU_BUILD_ID.
    /// </summary>
    /// <remarks>
    /// Each note is a 12-byte header followed by the name and the descriptor, both
    /// padded to a 4-byte boundary. Forgetting the padding is the usual way this
    /// parse goes wrong — it reads correctly for the first note and then walks off
    /// into misaligned garbage.
    /// </remarks>
    private static string? ScanNotes(ReadOnlySpan<byte> notes)
    {
        var position = 0;

        while (position + 12 <= notes.Length)
        {
            var nameSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(notes[position..]);
            var descriptorSize = (int)
                BinaryPrimitives.ReadUInt32LittleEndian(notes[(position + 4)..]);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(notes[(position + 8)..]);

            var nameOffset = position + 12;
            var descriptorOffset = nameOffset + Align4(nameSize);
            var next = descriptorOffset + Align4(descriptorSize);

            if (nameSize < 0 || descriptorSize < 0 || next > notes.Length || next <= position)
            {
                return null;
            }

            if (
                type == NtGnuBuildId
                && nameSize >= 3
                && notes[nameOffset] == (byte)'G'
                && notes[nameOffset + 1] == (byte)'N'
                && notes[nameOffset + 2] == (byte)'U'
            )
            {
                return Convert.ToHexStringLower(notes.Slice(descriptorOffset, descriptorSize));
            }

            position = next;
        }

        return null;

        static int Align4(int value) => (value + 3) & ~3;
    }
}
