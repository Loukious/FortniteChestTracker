using System.Buffers.Binary;

namespace FortniteReplayReader.Editing;

/// <summary>
/// The local replay browser's compatibility fields. These are independent of
/// the network header's serialization versions and do not convert replay data.
/// </summary>
public sealed record ReplayCompatibilityHeader
{
    private const uint Magic = 0x1CA2E27F;
    public uint FileVersion { get; }
    public int CustomVersionCount { get; }
    public uint NetworkVersion { get; }
    public uint Changelist { get; }
    public int NetworkVersionOffset { get; }
    public int ChangelistOffset => NetworkVersionOffset + 4;

    private ReplayCompatibilityHeader(uint fileVersion, int customVersionCount,
        int networkVersionOffset, uint networkVersion, uint changelist)
    {
        FileVersion = fileVersion;
        CustomVersionCount = customVersionCount;
        NetworkVersionOffset = networkVersionOffset;
        NetworkVersion = networkVersion;
        Changelist = changelist;
    }

    /// <summary>Reads only the local header; compressed or encrypted payloads are not decoded.</summary>
    public static ReplayCompatibilityHeader Read(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadHeader(stream);
    }

    public static ReplayCompatibilityHeader Read(ReadOnlySpan<byte> replay)
    {
        var (version, count, offset) = Locate(replay, replay.Length);
        return new(version, count, offset,
            BinaryPrimitives.ReadUInt32LittleEndian(replay.Slice(offset, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(replay.Slice(offset + 4, 4)));
    }

    /// <summary>
    /// Creates an independent replay buffer with the reference's network version
    /// and changelist. All other source bytes, including inner version fields, survive unchanged.
    /// </summary>
    public static byte[] CopyFromReference(ReadOnlySpan<byte> source, ReadOnlySpan<byte> reference)
    {
        var sourceHeader = Read(source);
        var referenceHeader = Read(reference);
        var result = source.ToArray();
        WriteValues(result.AsSpan(sourceHeader.NetworkVersionOffset, 8), referenceHeader);
        return result;
    }

    /// <summary>
    /// Saves a new copy with the reference's two compatibility fields. Refuses an
    /// existing output path, including either input. Does not require an Oodle library.
    /// </summary>
    public static void SaveCopy(string sourcePath, string referencePath, string outputPath)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var referenceFullPath = Path.GetFullPath(referencePath);
        var outputFullPath = Path.GetFullPath(outputPath);
        if (string.Equals(sourceFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(referenceFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Save the compatibility override to a new replay file.");

        // Read and validate both inputs before creating any output. Keep the
        // source handle open so copying uses the same file whose offsets were read.
        using var source = File.OpenRead(sourceFullPath);
        var sourceHeader = ReadHeader(source);
        var referenceHeader = Read(referenceFullPath);
        Span<byte> values = stackalloc byte[8];
        WriteValues(values, referenceHeader);
        source.Position = 0;
        using var output = new FileStream(outputFullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(output);
        output.Position = sourceHeader.NetworkVersionOffset;
        output.Write(values);
    }

    private static ReplayCompatibilityHeader ReadHeader(Stream source)
    {
        Span<byte> prefix = stackalloc byte[12];
        var available = (int)Math.Min(prefix.Length, source.Length);
        source.ReadExactly(prefix[..available]);
        var (version, count, offset) = Locate(prefix[..available], source.Length);
        source.Position = offset;
        Span<byte> values = stackalloc byte[8];
        source.ReadExactly(values);
        return new(version, count, offset,
            BinaryPrimitives.ReadUInt32LittleEndian(values),
            BinaryPrimitives.ReadUInt32LittleEndian(values[4..]));
    }

    private static (uint Version, int Count, int Offset) Locate(ReadOnlySpan<byte> prefix, long totalLength)
    {
        if (prefix.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(prefix) != Magic)
            throw new InvalidDataException("The file is not a local Unreal replay.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(prefix[4..]);
        if (version > 7)
            throw new NotSupportedException($"Local replay header version {version} has not been verified for compatibility editing.");
        var count = 0;
        var offset = 12L; // Magic, file version, duration, then network version.
        if (version >= 7)
        {
            if (prefix.Length < 12)
                throw new InvalidDataException("The replay custom-version count is truncated.");
            count = BinaryPrimitives.ReadInt32LittleEndian(prefix[8..]);
            if (count < 0)
                throw new InvalidDataException("The replay custom-version count is negative.");
            offset = 16L + 20L * count;
        }
        if (offset > int.MaxValue - 8 || offset + 8 > totalLength)
            throw new InvalidDataException("The replay compatibility fields are truncated.");
        return (version, count, (int)offset);
    }

    private static void WriteValues(Span<byte> destination, ReplayCompatibilityHeader reference)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, reference.NetworkVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], reference.Changelist);
    }
}
