using System.Text;

namespace FortniteReplayReader.Editing;

internal sealed class ReplayCosmeticGuidExports
{
    private readonly Dictionary<string, uint> objects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> packages = new(StringComparer.OrdinalIgnoreCase);
    private uint next;
    public List<SkinGuidDefinition> Definitions { get; } = [];

    public ReplayCosmeticGuidExports(IEnumerable<ReplaySkinLayoutReader> scans)
    {
        var maximum = scans.SelectMany(s => s.KnownGuids.Concat(s.Bunches.SelectMany(b => b.RequiredGuids))
            .Concat(s.Properties.Where(p => p.ObjectGuid.HasValue).Select(p => p.ObjectGuid!.Value))).DefaultIfEmpty(1u).Max();
        next = checked((maximum | 1u) + 2);
    }

    public uint Import(string path)
    {
        var (package, leaf) = SplitPath(path);
        if (objects.TryGetValue(path, out var existing)) return existing;
        if (!packages.TryGetValue(package, out var outer))
        {
            outer = Allocate();
            packages[package] = outer;
            Definitions.Add(new(outer, 0, package, 1));
        }
        var guid = Allocate();
        objects[path] = guid;
        Definitions.Add(new(guid, outer, leaf, 1));
        return guid;
    }

    private uint Allocate()
    {
        var value = next;
        next = checked(next + 2);
        return value;
    }

    internal static (string Package, string Object) SplitPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path[0] != '/' || path.Contains('\0') || path.Contains('\\') || path.Contains('\''))
            throw new ArgumentException("A cosmetic needs its full Unreal object path, for example /BRCosmetics/Athena/Items/Cosmetics/Characters/Character_Example.Character_Example.");
        var split = path.LastIndexOf('.');
        if (split <= path.LastIndexOf('/') || split == path.Length - 1 || path[(split + 1)..].Contains(':'))
            throw new ArgumentException($"Invalid cosmetic object path: {path}.");
        return (path[..split], path[(split + 1)..]);
    }

    internal static byte[] EncodeDefinition(SkinGuidDefinition definition)
    {
        var parts = new List<ReplayBits> { ReplayBits.Packed(definition.Guid), ReplayBits.UInt(1, 8), ReplayBits.Packed(definition.Outer) };
        if (definition.Outer != 0) parts.Add(ReplayBits.UInt(0, 8));
        parts.Add(ReplaySkinSwapper.FString(definition.Path));
        return ReplayBits.Join(parts).Bytes;
    }

    public byte[] Prepend(SkinGuidExportSection section)
    {
        using var stream = new MemoryStream();
        using var writer = new System.IO.BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(ReplayBits.Packed(checked(section.Count + (uint)Definitions.Count)).Bytes);
        foreach (var definition in Definitions)
        {
            var bytes = EncodeDefinition(definition);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
        writer.Write(section.Chunk.Data.AsSpan(section.Start + section.CountLength, section.Length - section.CountLength));
        return stream.ToArray();
    }
}
