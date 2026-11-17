using Unreal.Core.Models;
using Xunit;

namespace Unreal.Core.Test;

public class NetGuidCacheTest
{
    private const string ChestPath = "/Game/Building/ActorBlueprints/Containers/Tiered_Chest_Athena.Tiered_Chest_Athena_C";

    [Fact]
    public void ActorBeforeExportTable_IsRetriedWhenTableArrives()
    {
        var cache = new NetGuidCache();
        cache.NetGuidToPathName[41] = "Tiered_Chest_Athena_C";
        Assert.Null(cache.GetNetFieldExportGroup(41u));

        var expected = AddGroup(cache, ChestPath, 3);

        Assert.Same(expected, cache.GetNetFieldExportGroup(41u));
        Assert.Same(expected, cache.GetNetFieldExportGroup(41u));
    }

    [Theory]
    [InlineData("FortAbilitySystemComponent", "FortAbilitySystemComponentAthena", "FortAbilitySystemComponentAthena")]
    [InlineData("FortAbilitySystemComponent", "FortAbilitySystemComponentAthena", "/Script/FortniteGame.FortAbilitySystemComponentAthena")]
    [InlineData("BP_Chest_C", "BP_Chest_Cool_C", "BP_Chest_Cool_C")]
    public void ExactClassLeaf_PrefersSubclassOverAnEarlierShorterName(string parent, string subclass, string actorPath)
    {
        var cache = new NetGuidCache();
        AddGroup(cache, $"/Script/FortniteGame.{parent}", 1);
        var expected = AddGroup(cache, $"/Script/FortniteGame.{subclass}", 2);
        cache.NetGuidToPathName[41] = actorPath;

        Assert.Same(expected, cache.GetNetFieldExportGroup(41u));
    }

    [Fact]
    public void LateSubclassTable_ReplacesCachedLegacyBaseClassMatch()
    {
        var cache = new NetGuidCache();
        var parent = AddGroup(cache, "/Script/FortniteGame.FortAbilitySystemComponent", 1);
        var unrelated = AddGroup(cache, ChestPath, 2);
        cache.NetGuidToPathName[41] = "FortAbilitySystemComponentAthena";
        cache.NetGuidToPathName[43] = "Tiered_Chest_Athena_C";
        Assert.Same(parent, cache.GetNetFieldExportGroup(41u));
        Assert.Same(unrelated, cache.GetNetFieldExportGroup(43u));

        var expected = AddGroup(cache, "/Script/FortniteGame.FortAbilitySystemComponentAthena", 3);

        Assert.Same(expected, cache.GetNetFieldExportGroup(41u));
        Assert.Same(unrelated, cache.NetFieldExportGroupMapPathFixed[43]);
        Assert.Same(unrelated, cache.GetNetFieldExportGroup(43u));
    }

    [Fact]
    public void LateExactDigitEndingClass_ReplacesCachedTrimmedInstanceMatch()
    {
        var cache = new NetGuidCache();
        var parent = AddGroup(cache, "/Game/BP_Chest_V.BP_Chest_V_C", 1);
        cache.NetGuidToPathName[41] = "BP_Chest_V2_UAID_1234_10";
        Assert.Same(parent, cache.GetNetFieldExportGroup(41u));

        var expected = AddGroup(cache, "/Game/BP_Chest_V2.BP_Chest_V2_C", 2);

        Assert.Same(expected, cache.GetNetFieldExportGroup(41u));
    }

    [Fact]
    public void AmbiguousPlainClassLeaf_DoesNotUseSubstringFallback()
    {
        var cache = new NetGuidCache();
        AddGroup(cache, "/Game/First/BP_Chest.BP_Chest_C", 1);
        AddGroup(cache, "/Game/Second/BP_Chest.BP_Chest_C", 2);
        cache.NetGuidToPathName[41] = "BP_Chest_C";

        Assert.Null(cache.GetNetFieldExportGroup(41u));
    }

    [Fact]
    public void ExactFullPath_DisambiguatesClassesWithTheSameLeaf()
    {
        var cache = new NetGuidCache();
        AddGroup(cache, "/Game/First/BP_Chest.BP_Chest_C", 1);
        var expected = AddGroup(cache, "/Game/Second/BP_Chest.BP_Chest_C", 2);
        cache.NetGuidToPathName[41] = "/Game/Second/BP_Chest.BP_Chest_C";

        Assert.Same(expected, cache.GetNetFieldExportGroup(41u));
    }

    [Fact]
    public void PlacedVariantBeforeExportTable_IsRetriedWithoutAliasingTheBaseChest()
    {
        var cache = new NetGuidCache();
        AddGroup(cache, ChestPath, 1);
        cache.NetGuidToPathName[41] = "Tiered_Chest_Athena_Cool22_UAID_1234_10";
        Assert.Null(cache.GetNetFieldExportGroup(41u));

        var expected = AddGroup(cache, "/Game/Tiered_Chest_Athena_Cool.Tiered_Chest_Athena_Cool_C", 2);

        Assert.Same(expected, cache.GetNetFieldExportGroup(41u));
    }

    [Theory]
    [InlineData("Tiered_Chest_Athena47_UAID_387C764928FB54FC02_fc860ac2af69b777_1632776713")]
    [InlineData("Tiered_Chest_Athena5_6e02a0a859c897db_11")]
    [InlineData("Tiered_Chest_Athena_6E02A0A859C897DB_2")]
    [InlineData("Tiered_Chest_Athena47")]
    [InlineData("Tiered_Chest_Athena_C_27")]
    [InlineData("Tiered_Chest_Athena_C_UAID_387C76492A3DF10603_6e37e326c4949f74_1727058880")]
    [InlineData("/Game/Maps/Delilah.Delilah:PersistentLevel.Tiered_Chest_Athena47_UAID_387C764928FB54FC02_1632776713")]
    public void PlacedChestInstances_ResolveTheExactExportedClass(string instance)
    {
        var cache = new NetGuidCache();
        AddGroup(cache, "/Game/Containers/Tiered_Chest_Athena_FactionChest.Tiered_Chest_Athena_FactionChest_C", 1);
        var expected = AddGroup(cache, ChestPath, 2);
        cache.NetGuidToPathName[41] = instance;

        Assert.Same(expected, cache.GetNetFieldExportGroup(41u));
    }

    [Theory]
    [InlineData("Tiered_Chest_Athena_FactionChest_NoLocks22_UAID_1234_10", "Tiered_Chest_Athena_FactionChest_NoLocks")]
    [InlineData("Tiered_Chest_Athena_Cool22_UAID_1234_10", "Tiered_Chest_Athena_Cool")]
    [InlineData("BP_Chest_V2_UAID_1234_10", "BP_Chest_V2")]
    public void VariantAndDigitEndingClasses_ArePreserved(string instance, string className)
    {
        var cache = new NetGuidCache();
        AddGroup(cache, ChestPath, 1);
        AddGroup(cache, "/Game/BP_Chest_V.BP_Chest_V_C", 2);
        var expected = AddGroup(cache, $"/Game/{className}.{className}_C", 3);
        cache.NetGuidToPathName[41] = instance;

        Assert.Same(expected, cache.GetNetFieldExportGroup(41u));
    }

    [Theory]
    [InlineData("Tiered_Chest_Athena_Cool22_UAID_1234_10")]
    [InlineData("Tiered_Chest_Athena_Unknown22_6e02a0a859c897db_11")]
    [InlineData("Tiered_Chest_Athena22_notAValidHash_11")]
    public void UnknownInstanceVariant_DoesNotUseAnotherChestLayout(string instance)
    {
        var cache = new NetGuidCache();
        AddGroup(cache, ChestPath, 1);
        cache.NetGuidToPathName[41] = instance;

        Assert.Null(cache.GetNetFieldExportGroup(41u));
    }

    [Fact]
    public void AmbiguousClassLeaf_DoesNotPickAnArbitraryPackage()
    {
        var cache = new NetGuidCache();
        AddGroup(cache, "/Game/First/BP_Chest.BP_Chest_C", 1);
        AddGroup(cache, "/Game/Second/BP_Chest.BP_Chest_C", 2);
        cache.NetGuidToPathName[41] = "BP_Chest3_UAID_1234_10";

        Assert.Null(cache.GetNetFieldExportGroup(41u));
    }

    [Theory]
    [InlineData("BP_Chest3_UAID_1234_10")]
    [InlineData("BP_Chest3_6e02a0a859c897db_11")]
    [InlineData("BP_Chest3")]
    public void LaterDuplicateClassLeaf_InvalidatesPreviouslyResolvedNumberedInstance(string instance)
    {
        var cache = new NetGuidCache();
        var first = AddGroup(cache, "/Game/First/BP_Chest.BP_Chest_C", 1);
        cache.NetGuidToPathName[41] = instance;
        Assert.Same(first, cache.GetNetFieldExportGroup(41u));

        AddGroup(cache, "/Game/Second/BP_Chest.BP_Chest_C", 2);

        Assert.False(cache.NetFieldExportGroupMapPathFixed.ContainsKey(41));
        Assert.Null(cache.GetNetFieldExportGroup(41u));
    }

    [Fact]
    public void LaterDuplicateBaseClass_DoesNotInvalidateAnExactDigitEndingClass()
    {
        var cache = new NetGuidCache();
        AddGroup(cache, "/Game/First/BP_Chest.BP_Chest_C", 1);
        var exact = AddGroup(cache, "/Game/BP_Chest3.BP_Chest3_C", 2);
        cache.NetGuidToPathName[41] = "BP_Chest3_UAID_1234_10";
        Assert.Same(exact, cache.GetNetFieldExportGroup(41u));

        AddGroup(cache, "/Game/Second/BP_Chest.BP_Chest_C", 3);

        Assert.Same(exact, cache.NetFieldExportGroupMapPathFixed[41]);
        Assert.Same(exact, cache.GetNetFieldExportGroup(41u));
    }

    [Fact]
    public void ClearExportGroups_RemovesResolvedAndFailedActorMappings()
    {
        var cache = new NetGuidCache();
        AddGroup(cache, ChestPath, 1);
        cache.NetGuidToPathName[41] = "Tiered_Chest_Athena_C";
        cache.NetGuidToPathName[43] = "BP_RareChest_C";
        Assert.NotNull(cache.GetNetFieldExportGroup(41u));
        Assert.Null(cache.GetNetFieldExportGroup(43u));

        cache.ClearExportGroups();

        Assert.Null(cache.GetNetFieldExportGroup(41u));
        Assert.Null(cache.GetNetFieldExportGroupFromIndex(1));
        var replacement = AddGroup(cache, "/Game/BP_RareChest.BP_RareChest_C", 1);
        Assert.Same(replacement, cache.GetNetFieldExportGroup(43u));
        cache.NetGuidToPathName[45] = "BP_RareChest2_UAID_1234_10";
        Assert.Same(replacement, cache.GetNetFieldExportGroup(45u));
    }

    [Fact]
    public void ClearExportGroups_ResetsGameplayTagTable()
    {
        var cache = new NetGuidCache();
        var oldTable = AddGroup(cache, "NetworkGameplayTagNodeIndex", 1);
        Assert.Same(oldTable, cache.NetworkGameplayTagNodeIndex);

        cache.ClearExportGroups();

        Assert.Null(cache.NetworkGameplayTagNodeIndex);
        var replacement = AddGroup(cache, "NetworkGameplayTagNodeIndex", 1);
        Assert.Same(replacement, cache.NetworkGameplayTagNodeIndex);
    }

    [Fact]
    public void ReplacingExportTable_DiscardsOldActorAndIndexMappings()
    {
        var cache = new NetGuidCache();
        var oldTable = AddGroup(cache, ChestPath, 1);
        cache.NetGuidToPathName[41] = "Tiered_Chest_Athena_C";
        Assert.Same(oldTable, cache.GetNetFieldExportGroup(41u));

        var replacement = AddGroup(cache, ChestPath, 2);

        Assert.Same(replacement, cache.GetNetFieldExportGroup(41u));
        Assert.Null(cache.GetNetFieldExportGroupFromIndex(1));
        Assert.Same(replacement, cache.GetNetFieldExportGroupFromIndex(2));
    }

    [Fact]
    public void Cleanup_DiscardsDirectIndexCache()
    {
        var cache = new NetGuidCache();
        AddGroup(cache, ChestPath, 1);
        Assert.NotNull(cache.GetNetFieldExportGroupFromIndex(1));

        cache.Cleanup();

        Assert.Null(cache.GetNetFieldExportGroupFromIndex(1));
        Assert.Empty(cache.NetFieldExportGroupMapPathFixed);
    }

    private static NetFieldExportGroup AddGroup(NetGuidCache cache, string path, uint index)
    {
        var group = new NetFieldExportGroup { PathName = path, PathNameIndex = index };
        cache.AddToExportGroupMap(path, group);
        return group;
    }
}
