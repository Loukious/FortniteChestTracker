using Unreal.Core;
using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models.Enums;

namespace FortniteReplayReader.Models.NetFieldExports;

/// <summary>A character part path and its slot from the replicated player state.</summary>
public class SoftCharacterPart : INetFieldExportGroup
{
    [NetFieldExport("SoftPart", RepLayoutCmdType.Property)]
    public SoftCharacterPartReference? Part { get; set; }

    [NetFieldExport("Type", RepLayoutCmdType.Enum)]
    public int? Type { get; set; }
}

public class SoftCharacterPartReference : IProperty
{
    public string? Path { get; set; }

    public void Serialize(NetBitReader reader)
    {
        // Soft part references in the current replay stream start with a
        // presence bit followed by the full object path as an FString.
        Path = reader.ReadBit() ? reader.ReadFString() : null;
    }
}
