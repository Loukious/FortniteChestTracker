using System.Buffers.Binary;

namespace FortniteReplayReader.Editing;

// Unreal's bit archives store the least significant bit first.
internal sealed record ReplayBits
{
    public byte[] Bytes { get; }
    public int Length { get; }

    public ReplayBits(byte[] bytes, int length)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (length < 0 || (long)length > bytes.LongLength * 8)
            throw new InvalidDataException("Bit length exceeds its backing replay buffer.");
        Bytes = bytes;
        Length = length;
    }

    public static ReplayBits Empty { get; } = new([], 0);

    public ReplayBits Slice(int start, int count)
    {
        if (start < 0 || count < 0 || start > Length - count)
            throw new InvalidDataException("Bit range exceeds its enclosing replay record.");
        var output = new byte[(count + 7) / 8];
        Copy(Bytes, start, output, 0, count);
        return new(output, count);
    }

    public static ReplayBits Join(IEnumerable<ReplayBits> values)
    {
        var parts = values.ToArray();
        var length = parts.Sum(p => p.Length);
        var output = new byte[(length + 7) / 8];
        var offset = 0;
        foreach (var part in parts)
        {
            Copy(part.Bytes, 0, output, offset, part.Length);
            offset += part.Length;
        }
        return new(output, length);
    }

    public ReplayBits Patch(IEnumerable<ReplayBitEdit> edits)
    {
        var parts = new List<ReplayBits>();
        var offset = 0;
        foreach (var edit in edits.OrderBy(e => e.Start))
        {
            if (edit.Start < 0 || edit.Length < 0 || edit.Start > Length - edit.Length)
                throw new InvalidDataException("Replay edit exceeds its enclosing bit record.");
            if (edit.Start < offset)
                throw new InvalidDataException("Overlapping replay edits.");
            parts.Add(Slice(offset, edit.Start - offset));
            parts.Add(edit.Value);
            offset = checked(edit.Start + edit.Length);
        }
        parts.Add(Slice(offset, Length - offset));
        return Join(parts);
    }

    public static ReplayBits UInt(uint value, int bits)
    {
        if (bits is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(bits), "An unsigned integer contains at most 32 bits.");
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return new(bytes, bits);
    }

    public static ReplayBits Packed(uint value)
    {
        var bytes = new List<byte>();
        do
        {
            var next = (byte)((value & 127) << 1);
            value >>= 7;
            bytes.Add((byte)(next | (value != 0 ? 1 : 0)));
        } while (value != 0);
        return new(bytes.ToArray(), bytes.Count * 8);
    }

    public static ReplayBits Serialized(uint value, uint max)
    {
        if (value >= max) throw new InvalidDataException("Value exceeds replay bit-field range.");
        uint decoded = 0;
        var bits = new List<ReplayBits>();
        for (ulong mask = 1; decoded + mask < max; mask <<= 1)
        {
            var bit = value & (uint)mask;
            bits.Add(UInt(bit != 0 ? 1u : 0u, 1));
            decoded |= bit;
        }
        return Join(bits);
    }

    private static void Copy(byte[] source, int sourceBit, byte[] target, int targetBit, int count)
    {
        if ((sourceBit & 7) == 0 && (targetBit & 7) == 0)
        {
            var fullBytes = count / 8;
            source.AsSpan(sourceBit / 8, fullBytes).CopyTo(target.AsSpan(targetBit / 8));
            sourceBit += fullBytes * 8;
            targetBit += fullBytes * 8;
            count -= fullBytes * 8;
        }
        for (var i = 0; i < count; i++)
            if ((source[(sourceBit + i) >> 3] & (1 << ((sourceBit + i) & 7))) != 0)
                target[(targetBit + i) >> 3] |= (byte)(1 << ((targetBit + i) & 7));
    }
}

internal sealed record ReplayBitEdit(int Start, int Length, ReplayBits Value)
{
    public int Delta => Value.Length - Length;
}
