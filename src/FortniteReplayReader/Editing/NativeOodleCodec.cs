using System.Runtime.InteropServices;

namespace FortniteReplayReader.Editing;

/// <summary>
/// Uses a caller-supplied Oodle library. The library is not redistributed with this project.
/// </summary>
internal sealed class NativeOodleCodec : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint DecompressDelegate(nint source, nint sourceSize, nint destination, nint destinationSize,
        int fuzzSafe, int checkCrc, int verbosity, nint decodeBuffer, nint decodeBufferSize,
        nint callback, nint callbackUserData, nint decoderMemory, nint decoderMemorySize, int threadPhase);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CompressDelegate(int compressor, nint source, nint sourceSize, nint destination,
        int level, nint options, nint dictionaryBase, nint longRangeMatcher, nint scratchMemory, nint scratchMemorySize);

    private nint _library;
    private readonly DecompressDelegate _decompress;
    private readonly CompressDelegate _compress;

    public NativeOodleCodec(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The Oodle library path must be absolute.", nameof(path));
        }

        _library = NativeLibrary.Load(path);
        try
        {
            _decompress = Marshal.GetDelegateForFunctionPointer<DecompressDelegate>(
                NativeLibrary.GetExport(_library, "OodleLZ_Decompress"));
            _compress = Marshal.GetDelegateForFunctionPointer<CompressDelegate>(
                NativeLibrary.GetExport(_library, "OodleLZ_Compress"));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public byte[] Decompress(byte[] source, int decompressedSize)
    {
        ObjectDisposedException.ThrowIf(_library == 0, this);
        var output = new byte[decompressedSize];
        var sourceHandle = GCHandle.Alloc(source, GCHandleType.Pinned);
        var outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            var actualSize = _decompress(sourceHandle.AddrOfPinnedObject(), source.Length,
                outputHandle.AddrOfPinnedObject(), output.Length, 1, 0, 0, 0, 0, 0, 0, 0, 0, 3);
            if (actualSize != decompressedSize)
            {
                throw new InvalidDataException($"Oodle decompression returned {actualSize}; expected {decompressedSize} bytes.");
            }
            return output;
        }
        finally
        {
            outputHandle.Free();
            sourceHandle.Free();
        }
    }

    public byte[] Compress(byte[] source)
    {
        ObjectDisposedException.ThrowIf(_library == 0, this);
        // Oodle's worst-case overhead is less than 274 bytes per 256 KiB block.
        // An extra 64 KiB also accommodates the small-input case across supported library versions.
        var capacity = checked(source.Length + ((source.Length / 262144) + 1) * 274 + 65536);
        var output = new byte[capacity];
        var sourceHandle = GCHandle.Alloc(source, GCHandleType.Pinned);
        var outputHandle = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            // Mermaid = 9, Normal = 4. Both are supported by oo2core_5 and newer.
            var actualSize = _compress(9, sourceHandle.AddrOfPinnedObject(), source.Length,
                outputHandle.AddrOfPinnedObject(), 4, 0, 0, 0, 0, 0);
            if (actualSize <= 0 || actualSize > output.Length)
            {
                throw new InvalidDataException($"Oodle compression returned an invalid size: {actualSize}.");
            }
            Array.Resize(ref output, checked((int)actualSize));
        }
        finally
        {
            outputHandle.Free();
            sourceHandle.Free();
        }

        // Check the native result before it can become part of a replay file.
        if (!Decompress(output, source.Length).AsSpan().SequenceEqual(source))
        {
            throw new InvalidDataException("The Oodle compression round trip did not preserve the replay data.");
        }
        return output;
    }

    public void Dispose()
    {
        if (_library != 0)
        {
            NativeLibrary.Free(_library);
            _library = 0;
        }
    }
}
