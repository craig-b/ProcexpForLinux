using System.Buffers.Binary;
using Xunit;

namespace Procexp.Provenance.Tests;

/// <summary>
/// Tests the ELF note walk against synthesised images, so the padding and
/// alignment rules are exercised deterministically rather than depending on
/// whichever binaries happen to be installed.
/// </summary>
public class ElfInspectorTests
{
    [Fact]
    public void RejectsNonElfFiles()
    {
        var path = WriteTemporary([0x4D, 0x5A, 0x90, 0x00, 0x03]);   // a PE header
        try
        {
            Assert.False(ElfInspector.Inspect(path).IsElf);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsTruncatedFiles()
    {
        var path = WriteTemporary([0x7F, (byte)'E', (byte)'L', (byte)'F']);
        try
        {
            Assert.False(ElfInspector.Inspect(path).IsElf);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadsBuildIdFromNoteSegment()
    {
        var buildId = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 };
        var path = WriteTemporary(BuildElf(buildId));

        try
        {
            var facts = ElfInspector.Inspect(path);

            Assert.True(facts.IsElf);
            Assert.True(facts.Is64Bit);
            Assert.True(facts.IsLittleEndian);
            Assert.Equal("deadbeef01020304", facts.BuildId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The real reason this parser needs testing: notes are padded to 4-byte
    /// boundaries after both the name and the descriptor. A walker that ignores
    /// the padding reads the first note correctly and then advances into
    /// misalignment — so the failure only shows up when the build-id note is not
    /// first.
    /// </summary>
    [Fact]
    public void FindsBuildIdWhenPrecededByOtherNotes()
    {
        var buildId = new byte[] { 0xAA, 0xBB, 0xCC };   // 3 bytes, so padded
        var path = WriteTemporary(BuildElf(buildId, precedingNotes: 2));

        try
        {
            Assert.Equal("aabbcc", ElfInspector.Inspect(path).BuildId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReturnsNullBuildIdWhenAbsent()
    {
        var path = WriteTemporary(BuildElf(buildId: null));
        try
        {
            var facts = ElfInspector.Inspect(path);
            Assert.True(facts.IsElf);
            Assert.Null(facts.BuildId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RecognisesSharedObjects()
    {
        var path = WriteTemporary(BuildElf(buildId: null, elfType: 3));   // ET_DYN
        try
        {
            Assert.True(ElfInspector.Inspect(path).IsSharedObject);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- fixture construction -----------------------------------------------

    /// <summary>
    /// Build a minimal but structurally valid ELF64 little-endian image with one
    /// PT_NOTE segment.
    /// </summary>
    private static byte[] BuildElf(byte[]? buildId, int precedingNotes = 0, ushort elfType = 2)
    {
        const int HeaderSize = 64;
        const int ProgramHeaderSize = 56;
        const int ProgramHeaderOffset = HeaderSize;
        var notesOffset = ProgramHeaderOffset + ProgramHeaderSize;

        var notes = new List<byte>();

        // Notes ahead of the build-id, to force the walker to advance correctly.
        for (var i = 0; i < precedingNotes; i++)
        {
            // A 5-byte name and a 3-byte descriptor: both need padding.
            AppendNote(notes, "GNU\0\0"u8.ToArray(), [1, 2, 3], type: 1);
        }

        if (buildId is not null)
        {
            AppendNote(notes, "GNU\0"u8.ToArray(), buildId, type: 3);   // NT_GNU_BUILD_ID
        }
        else
        {
            AppendNote(notes, "GNU\0"u8.ToArray(), [9, 9, 9, 9], type: 1);
        }

        var image = new byte[notesOffset + notes.Count];
        var span = image.AsSpan();

        // ELF header
        span[0] = 0x7F;
        span[1] = (byte)'E';
        span[2] = (byte)'L';
        span[3] = (byte)'F';
        span[4] = 2;    // ELFCLASS64
        span[5] = 1;    // ELFDATA2LSB
        span[6] = 1;    // EV_CURRENT
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], elfType);
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], 0x3E);              // x86-64
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], ProgramHeaderOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(span[54..], ProgramHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[56..], 1);                 // one program header

        // Program header: PT_NOTE
        var programHeader = span[ProgramHeaderOffset..];
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader, 4);              // PT_NOTE
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[8..], (ulong)notesOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[32..], (ulong)notes.Count);

        notes.CopyTo(image, notesOffset);
        return image;
    }

    private static void AppendNote(List<byte> notes, byte[] name, byte[] descriptor, uint type)
    {
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)name.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)descriptor.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], type);

        notes.AddRange(header.ToArray());

        notes.AddRange(name);
        Pad(notes, name.Length);

        notes.AddRange(descriptor);
        Pad(notes, descriptor.Length);

        static void Pad(List<byte> target, int length)
        {
            var padding = ((length + 3) & ~3) - length;
            for (var i = 0; i < padding; i++)
            {
                target.Add(0);
            }
        }
    }

    private static string WriteTemporary(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"procexp-elf-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, content);
        return path;
    }
}
