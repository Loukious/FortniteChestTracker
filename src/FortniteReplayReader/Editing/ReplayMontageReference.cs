using Unreal.Core;

namespace FortniteReplayReader.Editing;

internal sealed record ReplayMontageReference(int Start, int Length, uint Guid, bool IsStopped)
{
    public static ReplayMontageReference Read(ReplayBits bits)
    {
        using var reader = new NetBitReader(bits.Bytes, bits.Length);
        if (reader.ReadBit()) reader.ReadIntPacked(); // quantized playback position
        else reader.ReadBitsToInt(7); // section ID
        // Version45 serializes PlayInstanceId later instead of ForcePlayBit.
        var stopped = reader.ReadBit();
        reader.ReadBit(); // skip position correction
        reader.ReadBit(); // skip play rate
        var start = reader.Position;
        var guid = reader.ReadIntPacked();
        if (reader.IsError) throw new InvalidDataException("Truncated replicated montage reference.");
        return new(start, reader.Position - start, guid, stopped);
    }
}
