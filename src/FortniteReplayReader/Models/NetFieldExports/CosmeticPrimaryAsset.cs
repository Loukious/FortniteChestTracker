using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models.Enums;

namespace FortniteReplayReader.Models.NetFieldExports;

/// <summary>A primary asset identifier from a pawn's replicated cosmetic loadout.</summary>
public class CosmeticPrimaryAsset : INetFieldExportGroup
{
    [NetFieldExport("Name", RepLayoutCmdType.PropertyName)]
    public string? Type { get; set; }

    [NetFieldExport("PrimaryAssetName", RepLayoutCmdType.PropertyName)]
    public string? Name { get; set; }
}
