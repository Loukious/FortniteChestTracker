using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;
using Unreal.Encryption;

namespace FortniteReplayReader.Editing;

/// <summary>
/// Lossless container for completed local replays (file versions 4 through 7).
/// Only replay-data and checkpoint payloads are editable. Metadata, events, unknown
/// chunks, and unchanged payloads retain their original bytes.
/// </summary>
public sealed class ReplayContainer : IDisposable
{
    private const int MaxDecodedChunkSize = 512 * 1024 * 1024;
    private readonly byte[] _fileHeader;
    private readonly string? _sourcePath;
    private readonly string? _oodleLibraryPath;
    private readonly bool _isCompressed;
    private readonly bool _isEncrypted;
    private readonly bool _analysisOnly;
    private readonly byte[] _encryptionKey;
    private NativeOodleCodec? _codec;
    private bool _disposed;

    /// <summary>Metadata for inspection. Changing these properties does not rewrite the file header.</summary>
    public ReplayInfo Info { get; }
    /// <summary>Network metadata for inspection. Changing these properties does not rewrite the network header.</summary>
    public ReplayHeader Header { get; }
    public IReadOnlyList<ReplayContainerChunk> Chunks { get; }

    private ReplayContainer(byte[] bytes, string? sourcePath, string? oodleLibraryPath, bool analysisOnly = false)
    {
        _sourcePath = sourcePath;
        _oodleLibraryPath = oodleLibraryPath;
        _analysisOnly = analysisOnly;
        var cursor = new Cursor(bytes);
        if (cursor.UInt32() != 0x1CA2E27F)
        {
            throw new InvalidDataException("Invalid local replay file magic.");
        }
        var version = cursor.UInt32();
        if (version is < 4 or > 7)
        {
            throw new NotSupportedException($"Replay container file version {version} is not supported for editing (supported: 4–7).");
        }
        if (version >= 7)
        {
            cursor.Skip(checked(cursor.Count(20) * 20));
        }

        var info = new ReplayInfo
        {
            FileVersion = (ReplayVersionHistory)version,
            LengthInMs = cursor.UInt32(),
            NetworkVersion = cursor.UInt32(),
            Changelist = cursor.UInt32(),
            FriendlyName = cursor.FString(),
            IsLive = cursor.Boolean(),
            Timestamp = DateTime.FromBinary(cursor.Int64()),
            IsCompressed = cursor.Boolean()
        };
        if (version >= 6)
        {
            info.IsEncrypted = cursor.Boolean();
            info.EncryptionKey = cursor.Take(cursor.Count(1));
        }
        if (info.IsLive)
        {
            throw new InvalidDataException("A replay must finish recording before it can be edited.");
        }
        if (info.IsEncrypted && info.EncryptionKey.Length is not (16 or 24 or 32))
        {
            throw new InvalidDataException("The encrypted replay does not contain a valid AES key.");
        }
        Info = info;
        _isCompressed = info.IsCompressed;
        _isEncrypted = info.IsEncrypted;
        _encryptionKey = info.EncryptionKey.ToArray();
        _fileHeader = _analysisOnly ? [] : bytes.AsSpan(0, cursor.Position).ToArray();

        var chunks = new List<ReplayContainerChunk>();
        ReplayHeader? header = null;
        try
        {
            var chunkIndex = 0;
            while (cursor.Remaining > 0)
            {
                var type = (ReplayChunkType)cursor.UInt32();
                var size = cursor.Count(1);
                var index = chunkIndex++;
                if (_analysisOnly && type == ReplayChunkType.Checkpoint)
                {
                    // Sequential chest analysis never seeks through checkpoints.
                    // Validate the outer size, then avoid copying or decoding their contents.
                    cursor.Skip(size);
                    continue;
                }
                var body = cursor.Take(size);
                var chunk = ReadChunk(index, type, body, version);
                chunks.Add(chunk);
                if (type == ReplayChunkType.Header)
                {
                    if (header != null)
                    {
                        throw new InvalidDataException("Multiple replay header chunks are not supported for editing.");
                    }
                    using var archive = new Unreal.Core.BinaryReader(body);
                    var metadata = new MetadataReader();
                    metadata.ReadReplayHeader(archive);
                    if (archive.IsError)
                    {
                        throw new InvalidDataException("The replay network header is malformed.");
                    }
                    header = metadata.Header;
                }
            }
            Header = header ?? throw new InvalidDataException("The replay has no network header chunk.");
            Chunks = chunks.AsReadOnly();
        }
        catch
        {
            _codec?.Dispose();
            throw;
        }
    }

    public static ReplayContainer Load(string path, string? oodleLibraryPath = null)
    {
        var sourcePath = Path.GetFullPath(path);
        return new ReplayContainer(File.ReadAllBytes(sourcePath), sourcePath, oodleLibraryPath);
    }

    /// <summary>Reads an in-memory file; payloads and immutable source bytes are copied.</summary>
    public static ReplayContainer Read(byte[] bytes, string? oodleLibraryPath = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new ReplayContainer(bytes, null, oodleLibraryPath);
    }

    /// <summary>Loads a read-only container for sequential analysis, omitting checkpoints and rewrite copies.</summary>
    public static ReplayContainer LoadForAnalysis(string path, string? oodleLibraryPath = null)
    {
        var sourcePath = Path.GetFullPath(path);
        return new ReplayContainer(File.ReadAllBytes(sourcePath), sourcePath, oodleLibraryPath, analysisOnly: true);
    }

    /// <summary>Reads a read-only in-memory container for sequential analysis, omitting checkpoints and rewrite copies.</summary>
    public static ReplayContainer ReadForAnalysis(byte[] bytes, string? oodleLibraryPath = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new ReplayContainer(bytes, null, oodleLibraryPath, analysisOnly: true);
    }

    private ReplayContainerChunk ReadChunk(int index, ReplayChunkType type, byte[] body, uint version)
    {
        if (type is not (ReplayChunkType.ReplayData or ReplayChunkType.Checkpoint))
        {
            return new ReplayContainerChunk(index, type, body, _analysisOnly ? body : body.ToArray(),
                [], -1, -1, 0, 0, preserveOriginal: !_analysisOnly);
        }

        var cursor = new Cursor(body);
        if (type == ReplayChunkType.Checkpoint)
        {
            cursor.FString(); // id
            cursor.FString(); // group
            cursor.FString(); // metadata
        }
        var start = cursor.UInt32();
        var end = cursor.UInt32();
        var dataLengthOffset = cursor.Position;
        var dataLength = cursor.Count(1);
        var memorySizeOffset = -1;
        if (type == ReplayChunkType.ReplayData && version >= 6)
        {
            memorySizeOffset = cursor.Position;
            cursor.Int32();
        }
        var prefix = _analysisOnly ? [] : body.AsSpan(0, cursor.Position).ToArray();
        var payload = cursor.Take(dataLength);
        if (cursor.Remaining != 0)
        {
            throw new InvalidDataException($"Replay chunk {index} contains bytes outside its declared data length.");
        }

        if (_isEncrypted)
        {
            if (OperatingSystem.IsBrowser())
                payload = ReplayAesEcb.Decrypt(payload, _encryptionKey);
            else
            {
                using var aes = Aes.Create();
                aes.Key = _encryptionKey;
                payload = aes.DecryptEcb(payload, PaddingMode.PKCS7);
            }
        }
        if (_isCompressed)
        {
            var compression = new Cursor(payload);
            var decompressedSize = compression.Int32();
            if (decompressedSize <= 0 || decompressedSize > MaxDecodedChunkSize)
            {
                throw new InvalidDataException($"Replay chunk {index} declares an invalid decoded size: {decompressedSize}.");
            }
            var compressedSize = compression.Count(1);
            var compressed = compression.Take(compressedSize);
            if (compression.Remaining != 0)
            {
                throw new InvalidDataException($"Replay chunk {index} has an inconsistent compressed size.");
            }
            if (_oodleLibraryPath == null)
            {
                var decoded = Oodle.DecompressReplayData(compressed, decompressedSize);
                // The managed decoder returns an owned array; read-only analysis can use it directly.
                payload = _analysisOnly && MemoryMarshal.TryGetArray(decoded, out var segment) &&
                    segment.Array is not null && segment.Offset == 0 && segment.Count == segment.Array.Length
                    ? segment.Array : decoded.ToArray();
            }
            else payload = Codec.Decompress(compressed, decompressedSize);
            if (payload.Length != decompressedSize)
            {
                throw new InvalidDataException($"Replay chunk {index} did not decompress to its declared size.");
            }
        }
        return new ReplayContainerChunk(index, type, body, payload, prefix, dataLengthOffset,
            memorySizeOffset, start, end, preserveOriginal: !_analysisOnly);
    }

    private NativeOodleCodec Codec => _codec ??= new NativeOodleCodec(_oodleLibraryPath
        ?? throw new InvalidOperationException("Writing modified compressed replays requires an absolute Oodle library path. Pass the installed game's oo2core DLL path when loading the replay."));

    /// <summary>Serializes a copy, preserving every unchanged chunk verbatim.</summary>
    public byte[] GetBytes()
    {
        using var output = new MemoryStream();
        Write(output);
        return output.ToArray();
    }

    /// <summary>Writes to a caller-owned output stream without closing it.</summary>
    public void Write(Stream output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_analysisOnly) throw new NotSupportedException("An analysis-only replay container cannot be written or saved.");
        ArgumentNullException.ThrowIfNull(output);
        output.Write(_fileHeader);
        using var writer = new System.IO.BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        foreach (var chunk in Chunks)
        {
            if (chunk.Data.AsSpan().SequenceEqual(chunk.OriginalData))
            {
                writer.Write((uint)chunk.Type);
                writer.Write(chunk.OriginalBody.Length);
                writer.Write(chunk.OriginalBody);
                continue;
            }
            if (chunk.Type is not (ReplayChunkType.ReplayData or ReplayChunkType.Checkpoint))
            {
                throw new InvalidOperationException("Only replay-data and checkpoint payloads can be edited.");
            }
            if (chunk.Data.Length == 0 || chunk.Data.Length > MaxDecodedChunkSize)
            {
                throw new InvalidDataException($"Modified replay chunk {chunk.Index} has an invalid decoded size.");
            }
            var payload = chunk.Data;
            if (_isCompressed)
            {
                var compressed = Codec.Compress(payload);
                payload = new byte[checked(8 + compressed.Length)];
                BinaryPrimitives.WriteInt32LittleEndian(payload, chunk.Data.Length);
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), compressed.Length);
                compressed.CopyTo(payload, 8);
            }
            if (_isEncrypted)
            {
                using var aes = Aes.Create();
                aes.Key = _encryptionKey;
                payload = aes.EncryptEcb(payload, PaddingMode.PKCS7);
            }
            var prefix = chunk.Prefix.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(chunk.DataLengthOffset), payload.Length);
            if (chunk.MemorySizeOffset >= 0)
            {
                BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(chunk.MemorySizeOffset), chunk.Data.Length);
            }
            writer.Write((uint)chunk.Type);
            writer.Write(checked(prefix.Length + payload.Length));
            writer.Write(prefix);
            writer.Write(payload);
        }
    }

    /// <summary>Creates a new replay file. Existing files, including the source, are never overwritten.</summary>
    public void Save(string outputPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var destination = Path.GetFullPath(outputPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (_sourcePath != null && string.Equals(destination, _sourcePath, comparison))
        {
            throw new IOException("Choose a different output path; the source replay cannot be overwritten.");
        }
        if (File.Exists(destination))
        {
            throw new IOException($"The output file already exists: {destination}");
        }
        // Finish compression and validation before creating the output file.
        var bytes = GetBytes();
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _codec?.Dispose();
            _disposed = true;
        }
    }

    private sealed class MetadataReader : Unreal.Core.ReplayReader<Replay>
    {
        public MetadataReader() : base(null!, ParseMode.EventsOnly) => Replay = new Replay();
        public ReplayHeader Header => Replay.Header;
        protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup) { }
        protected override void OnExternalDataRead(uint channelIndex, IExternalData? externalData) { }
        protected override void OnNetDeltaRead(uint channelIndex, NetDeltaUpdate update) { }
    }

    private sealed class Cursor(byte[] bytes)
    {
        public int Position { get; private set; }
        public int Remaining => bytes.Length - Position;

        public byte[] Take(int count)
        {
            Require(count);
            var result = bytes.AsSpan(Position, count).ToArray();
            Position += count;
            return result;
        }

        public void Skip(int count)
        {
            Require(count);
            Position += count;
        }

        public int Int32()
        {
            Require(4);
            var result = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(Position, 4));
            Position += 4;
            return result;
        }

        public uint UInt32() => unchecked((uint)Int32());

        public long Int64()
        {
            Require(8);
            var result = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(Position, 8));
            Position += 8;
            return result;
        }

        public bool Boolean() => Int32() switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException("Invalid replay boolean.")
        };

        public int Count(int elementSize)
        {
            var count = Int32();
            if (count < 0 || count > Remaining / elementSize)
            {
                throw new InvalidDataException("A replay length exceeds the remaining file data.");
            }
            return count;
        }

        public string FString()
        {
            var length = Int32();
            if (length == 0)
            {
                return string.Empty;
            }
            if (length == int.MinValue)
            {
                throw new InvalidDataException("Invalid replay FString length.");
            }
            var width = length < 0 ? 2 : 1;
            var characters = Math.Abs(length);
            if (characters > Remaining / width)
            {
                throw new InvalidDataException("A replay FString exceeds the remaining file data.");
            }
            var value = Take(characters * width);
            if (value[^1] != 0 || (width == 2 && value[^2] != 0))
            {
                throw new InvalidDataException("A replay FString has no terminator.");
            }
            return (width == 2 ? Encoding.Unicode : Encoding.UTF8).GetString(value.AsSpan(0, value.Length - width));
        }

        private void Require(int count)
        {
            if (count < 0 || count > Remaining)
            {
                throw new InvalidDataException("The replay file is truncated.");
            }
        }
    }
}

/// <summary>A replay chunk, retaining its source representation for lossless copying.</summary>
public sealed class ReplayContainerChunk
{
    private byte[] _data;
    public int Index { get; }
    public ReplayChunkType Type { get; }
    public uint StartTime { get; }
    public uint EndTime { get; }
    /// <summary>Decoded bytes for replay-data/checkpoints; opaque body bytes for other chunks.</summary>
    public byte[] Data
    {
        get => _data;
        set => _data = value ?? throw new ArgumentNullException(nameof(value));
    }
    internal byte[] OriginalBody { get; }
    internal byte[] OriginalData { get; }
    internal byte[] Prefix { get; }
    internal int DataLengthOffset { get; }
    internal int MemorySizeOffset { get; }

    internal ReplayContainerChunk(int index, ReplayChunkType type, byte[] originalBody, byte[] data,
        byte[] prefix, int dataLengthOffset, int memorySizeOffset, uint startTime, uint endTime,
        bool preserveOriginal = true)
    {
        Index = index;
        Type = type;
        OriginalBody = preserveOriginal ? originalBody : [];
        OriginalData = preserveOriginal ? data.ToArray() : [];
        _data = data;
        Prefix = prefix;
        DataLengthOffset = dataLengthOffset;
        MemorySizeOffset = memorySizeOffset;
        StartTime = startTime;
        EndTime = endTime;
    }
}
