using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace FortniteReplayReader.Models.NetFieldExports;

/// <summary>Nullable element values distinguish omitted sparse updates from asset-table index zero.</summary>
public sealed class CosmeticAssetIndex : INetFieldExportGroup
{
    [NetFieldExport("AssetIndices", RepLayoutCmdType.PropertyInt)]
    public int? Value { get; set; }
}

/// <summary>The variant-array length determines the object-reference slots preceding the equipped cosmetics.</summary>
public sealed class CosmeticVariantChannel : INetFieldExportGroup
{
    [NetFieldExport("ItemVariantIsUsedFor", RepLayoutCmdType.Property)]
    public ItemDefinition? ItemVariantIsUsedFor { get; set; }
}
