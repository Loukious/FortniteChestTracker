using Unreal.Core;
using Unreal.Core.Models.Enums;

namespace FortniteReplayReader.Editing;

// RepLayout arrays are sparse updates. Keep unknown inner fields verbatim.
internal sealed class ReplayRepArray
{
    public int Count { get; private set; }
    public SortedDictionary<int, Dictionary<string, ReplayBits>> Elements { get; } = [];

    public static ReplayRepArray Read(SkinProperty property)
    {
        var value = property.Value;
        using var reader = new NetBitReader(value.Bytes, value.Length) { EngineNetworkVersion = (EngineNetworkVersionHistory)45 };
        var result = new ReplayRepArray { Count = checked((int)reader.ReadIntPacked()) };
        if (result.Count > 65535) throw new InvalidDataException("Cosmetic array is too large.");
        var terminated = false;
        while (!reader.AtEnd())
        {
            var wireIndex = reader.ReadIntPacked();
            if (wireIndex == 0) { terminated = true; break; }
            var index = checked((int)wireIndex - 1);
            if (index >= result.Count || result.Elements.ContainsKey(index)) throw new InvalidDataException("Invalid cosmetic array index.");
            var fields = new Dictionary<string, ReplayBits>();
            while (true)
            {
                var handle = reader.ReadIntPacked();
                if (handle == 0) break;
                if (!property.ExportGroup.IsValidIndex(handle - 1)) throw new InvalidDataException("Invalid cosmetic array field.");
                var name = property.ExportGroup.NetFieldExports[handle - 1]?.Name ?? throw new InvalidDataException("Unknown cosmetic array field.");
                var length = checked((int)reader.ReadIntPacked());
                if (reader.IsError || length > reader.GetBitsLeft()) throw new InvalidDataException("Truncated cosmetic array.");
                fields.Add(name, value.Slice(reader.Position, length));
                reader.SkipBits(length);
            }
            if (reader.IsError) throw new InvalidDataException("Truncated cosmetic array element.");
            result.Elements.Add(index, fields);
        }
        if (reader.IsError || !terminated || !reader.AtEnd()) throw new InvalidDataException("Invalid cosmetic array terminator.");
        return result;
    }

    public void Merge(ReplayRepArray update)
    {
        Count = update.Count;
        foreach (var index in Elements.Keys.Where(i => i >= Count).ToArray()) Elements.Remove(index);
        foreach (var (index, fields) in update.Elements)
        {
            if (!Elements.TryGetValue(index, out var target)) Elements[index] = target = [];
            foreach (var (name, value) in fields) target[name] = value;
        }
    }

    public ReplayBits? Field(int index, string name) => Elements.GetValueOrDefault(index)?.GetValueOrDefault(name);

    public int Int(int index, string name)
    {
        var bits = Field(index, name) ?? throw new InvalidDataException($"Missing cosmetic array value {name}[{index}].");
        if (bits.Length != 32) throw new InvalidDataException("Cosmetic asset index must be a signed int32.");
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bits.Bytes);
    }

    public string? Name(int index, string name)
    {
        if (Field(index, name) is not { } bits) return null;
        using var reader = new NetBitReader(bits.Bytes, bits.Length) { EngineNetworkVersion = (EngineNetworkVersionHistory)45 };
        var value = reader.ReadFName();
        if (reader.IsError || !reader.AtEnd()) throw new InvalidDataException("Invalid primary asset name.");
        return value;
    }

    public ReplayBits Write(SkinProperty property, int? count = null, IEnumerable<KeyValuePair<int, Dictionary<string, ReplayBits>>>? elements = null)
    {
        var length = count ?? Count;
        var parts = new List<ReplayBits> { ReplayBits.Packed(checked((uint)length)) };
        foreach (var (index, fields) in (elements ?? Elements).OrderBy(e => e.Key))
        {
            if (index < 0 || index >= length) throw new InvalidDataException("Cosmetic output array index is out of range.");
            parts.Add(ReplayBits.Packed((uint)index + 1));
            foreach (var field in fields.Select(pair =>
            {
                var export = property.ExportGroup.NetFieldExports.Skip((int)property.Handle + 1)
                    .FirstOrDefault(e => e?.Name == pair.Key) ?? throw new InvalidDataException($"Missing cosmetic array field {pair.Key}.");
                return (export.Handle, pair.Value);
            }).OrderBy(f => f.Handle))
            {
                parts.Add(ReplayBits.Packed(field.Handle + 1));
                parts.Add(ReplayBits.Packed((uint)field.Value.Length));
                parts.Add(field.Value);
            }
            parts.Add(ReplayBits.Packed(0));
        }
        parts.Add(ReplayBits.Packed(0));
        return ReplayBits.Join(parts);
    }
}
