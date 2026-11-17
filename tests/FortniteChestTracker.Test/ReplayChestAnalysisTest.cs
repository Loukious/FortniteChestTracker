using FortniteReplayReader.Analysis;
using FortniteReplayReader.Editing;
using Unreal.Core;
using Unreal.Core.Models;
using Xunit;

namespace FortniteReplayReader.Test;

public class ReplayChestAnalysisTest
{
    private const string ChestClass = "/Game/Building/Containers/Tiered_Chest_Athena.Tiered_Chest_Athena_C";

    [Fact]
    public void CompletionClearingTheSearchingPawnUsesTheLastActiveInteraction()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, false, 200, true, 1);
        data.Update(100, ChestClass, true, 0, true, 2);

        var opening = Assert.Single(data.Finish(new Dictionary<uint, uint> { [200] = 300 },
            new Dictionary<uint, string> { [300] = "owner-id" }));

        Assert.Equal(100u, opening.ChestActorGuid);
        Assert.Equal(200u, opening.SearchingPawnGuid);
        Assert.Equal(300u, opening.PlayerStateActorGuid);
        Assert.Equal("owner-id", opening.PlayerId);
        Assert.Equal(2f, opening.ReplayTimeSeconds);
    }

    [Fact]
    public void CancelledInteractionDoesNotAttributeASubsequentUnobservedSearch()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, false, 200, true, 1);
        data.Update(100, ChestClass, null, 0, true, 2);
        data.Update(100, ChestClass, true, null, false, 3);

        var opening = Assert.Single(data.Finish(new Dictionary<uint, uint> { [200] = 300 },
            new Dictionary<uint, string> { [300] = "owner-id" }));

        Assert.Null(opening.SearchingPawnGuid);
        Assert.Null(opening.PlayerId);
    }

    [Fact]
    public void LatestSearchingPawnBeforeCompletionWins()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, false, 200, true, 1);
        data.Update(100, ChestClass, null, 201, true, 2);
        data.Update(100, ChestClass, true, null, false, 3);

        Assert.Equal(201u, Assert.Single(data.Finish(new Dictionary<uint, uint>(),
            new Dictionary<uint, string>())).SearchingPawnGuid);
    }

    [Fact]
    public void RepeatedActorExportsDoNotDuplicateOrReplaceAnAttributedOpening()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, true, 200, true, 1);
        data.Update(100, ChestClass, true, 201, true, 2);
        data.Update(100, ChestClass, false, null, false, 3);
        data.Update(100, ChestClass, true, 202, true, 4);
        data.Update(101, ChestClass, true, 200, true, 5);

        var openings = data.Finish(new Dictionary<uint, uint>(), new Dictionary<uint, string>());
        Assert.Equal(2, openings.Count);
        var first = Assert.Single(openings, opening => opening.ChestActorGuid == 100);
        Assert.Equal(200u, first.SearchingPawnGuid);
        Assert.Equal(1f, first.ReplayTimeSeconds);
        Assert.Equal(2, data.ChestActorsSeen);
    }

    [Fact]
    public void SearchingWithoutCompletionDoesNotCountAsAnOpening()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, false, 200, true, 1);
        data.Update(101, ChestClass, null, 201, true, 2);

        Assert.Empty(data.Finish(new Dictionary<uint, uint>(), new Dictionary<uint, string>()));
        Assert.Equal(2, data.ChestActorsSeen);
    }

    [Fact]
    public void SplitCompletionAndPawnUpdatesResolveIdentityRecordedLater()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, true, null, false, 1);
        data.Update(100, ChestClass, null, 200, true, 1);

        Assert.Null(Assert.Single(data.Finish(new Dictionary<uint, uint>(),
            new Dictionary<uint, string>())).PlayerId);
        Assert.Equal("teammate-id", Assert.Single(data.Finish(new Dictionary<uint, uint> { [200] = 300 },
            new Dictionary<uint, string> { [300] = "teammate-id" })).PlayerId);
    }

    [Fact]
    public void ExplicitResetCountsEachCompletionWithItsOwnPlayerAndTime()
    {
        var data = new ReplayChestAccumulator();
        // Recorded Nov 10 timeline: sarrautSavastano opens actor 15901,
        // the chest resets, then the replay owner opens the same actor.
        data.Update(15901, ChestClass, new(true, 27942, true, null, 1, 2), 736.7189f);
        data.Update(15901, ChestClass, new(false, null, false, null, 0, 1), 880.07196f);
        data.Update(15901, ChestClass, new(true, 17988, true, null, 1, 2), 948.23883f);
        data.Update(15901, ChestClass, new(true, 201, true, null, 1, 2), 949f);

        var openings = data.Finish(new Dictionary<uint, uint> { [27942] = 301, [17988] = 302 },
            new Dictionary<uint, string> { [301] = "other-id", [302] = "owner-id" });

        Assert.Equal(2, openings.Count);
        Assert.Equal(1, data.ChestActorsSeen);
        Assert.Collection(openings,
            first =>
            {
                Assert.Equal(15901u, first.ChestActorGuid);
                Assert.Equal(0, first.ResetCycleIndex);
                Assert.Equal(27942u, first.SearchingPawnGuid);
                Assert.Equal("other-id", first.PlayerId);
                Assert.Equal(736.7189f, first.ReplayTimeSeconds);
            },
            second =>
            {
                Assert.Equal(15901u, second.ChestActorGuid);
                Assert.Equal(1, second.ResetCycleIndex);
                Assert.Equal(17988u, second.SearchingPawnGuid);
                Assert.Equal("owner-id", second.PlayerId);
                Assert.Equal(948.23883f, second.ReplayTimeSeconds);
            });
    }

    [Fact]
    public void SamePlacedInstanceWithANewGuidDoesNotDuplicateItsCompletedSnapshot()
    {
        var data = new ReplayChestAccumulator();
        const string path = "/Game/Athena/Maps/Athena_Terrain.Athena_Terrain:PersistentLevel.Tiered_Chest_Athena_C_UAID_E04F43E65CE8C2F902_5e46178587d4e57a_2001470511";
        // The Nov 10 replay re-exports this placed instance under a new GUID.
        data.Update(18989, ChestClass, new(true, 36992, true, null, 1, 2), 683.2327f, path);
        data.Update(20667, ChestClass, new(true, 36992, true, null, 1, 2), 718.90393f, path);

        var opening = Assert.Single(data.Finish(new Dictionary<uint, uint>(), new Dictionary<uint, string>()));
        Assert.Equal(18989u, opening.ChestActorGuid);
        Assert.Equal(36992u, opening.SearchingPawnGuid);
        Assert.Equal(683.2327f, opening.ReplayTimeSeconds);
        Assert.Equal(0, opening.ResetCycleIndex);
        Assert.Equal(1, data.ChestActorsSeen);
    }

    [Fact]
    public void SamePlacedInstanceCanResetAndReopenAcrossGuidChanges()
    {
        var data = new ReplayChestAccumulator();
        const string path = "/Game/Athena/Maps/Athena_Terrain.Athena_Terrain:PersistentLevel.Tiered_Chest_Athena_C_UAID_First";
        data.Update(100, ChestClass, new(true, 200, true, null, 1, 2), 1, path);
        data.Update(101, ChestClass, new(false, null, false, null, 0, 1), 2, path);
        data.Update(102, ChestClass, new(true, 201, true, null, 1, 2), 3, path);

        var openings = data.Finish(new Dictionary<uint, uint>(), new Dictionary<uint, string>());
        Assert.Equal(2, openings.Count);
        Assert.All(openings, opening => Assert.Equal(100u, opening.ChestActorGuid));
        Assert.Equal(0, openings[0].ResetCycleIndex);
        Assert.Equal(200u, openings[0].SearchingPawnGuid);
        Assert.Equal(1, openings[1].ResetCycleIndex);
        Assert.Equal(201u, openings[1].SearchingPawnGuid);
        Assert.Equal(3f, openings[1].ReplayTimeSeconds);
        Assert.Equal(1, data.ChestActorsSeen);
    }

    [Fact]
    public void SameActorWithStableIdentityRetainsExistingGuidDeduplication()
    {
        var data = new ReplayChestAccumulator();
        const string path = "/Game/Athena/Maps/Athena_Terrain.Athena_Terrain:PersistentLevel.Tiered_Chest_Athena_C_UAID_First";
        data.Update(100, ChestClass, true, 200, true, 1, path);
        data.Update(100, ChestClass, true, 201, true, 2, path);

        var opening = Assert.Single(data.Finish(new Dictionary<uint, uint>(), new Dictionary<uint, string>()));
        Assert.Equal(100u, opening.ChestActorGuid);
        Assert.Equal(200u, opening.SearchingPawnGuid);
        Assert.Equal(1, data.ChestActorsSeen);
    }

    [Fact]
    public void DifferentPlacedInstancePathsRemainSeparateChests()
    {
        var data = new ReplayChestAccumulator();
        const string firstPath = "/Game/Athena/Maps/Athena_Terrain.Athena_Terrain:PersistentLevel.Tiered_Chest_Athena_C_UAID_First";
        const string secondPath = "/Game/Athena/Maps/Athena_Terrain.Athena_Terrain:PersistentLevel.Tiered_Chest_Athena_C_UAID_Second";
        data.Update(100, ChestClass, true, 200, true, 1, firstPath);
        data.Update(101, ChestClass, true, 200, true, 1, secondPath);

        var openings = data.Finish(new Dictionary<uint, uint>(), new Dictionary<uint, string>());
        Assert.Equal(2, openings.Count);
        Assert.Equal(100u, openings[0].ChestActorGuid);
        Assert.Equal(101u, openings[1].ChestActorGuid);
        Assert.Equal(2, data.ChestActorsSeen);
    }

    [Fact]
    public void DifferentGuidsWithoutStableIdentityRemainSeparateChests()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, new(true, 200, true, null, 1, 2), 1);
        data.Update(101, ChestClass, new(true, 200, true, null, 1, 2), 1);

        Assert.Equal(2, data.Finish(new Dictionary<uint, uint>(), new Dictionary<uint, string>()).Count);
        Assert.Equal(2, data.ChestActorsSeen);
    }

    [Fact]
    public void KnownGuidAliasKeepsItsPlacedStateWhenAPathIsAbsentFromALaterUpdate()
    {
        var data = new ReplayChestAccumulator();
        const string path = "/Game/Athena/Maps/Athena_Terrain.Athena_Terrain:PersistentLevel.Tiered_Chest_Athena_C_UAID_First";
        data.Update(100, ChestClass, true, 200, true, 1, path);
        data.Update(101, ChestClass, true, 200, true, 2, path);
        data.Update(101, ChestClass, true, 201, true, 3);

        var opening = Assert.Single(data.Finish(new Dictionary<uint, uint>(), new Dictionary<uint, string>()));
        Assert.Equal(100u, opening.ChestActorGuid);
        Assert.Equal(200u, opening.SearchingPawnGuid);
        Assert.Equal(1, data.ChestActorsSeen);
    }

    [Fact]
    public void ResetRequiresTheFalseFlagAndZeroCounterInTheSameUpdate()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, new(true, 200, true, null, 1, 2), 1);
        data.Update(100, ChestClass, new(false, null, false, null, null, 1), 2);
        data.Update(100, ChestClass, new(null, null, false, null, 0, 0), 3);
        data.Update(100, ChestClass, new(false, null, false, null, null, 1), 4);
        data.Update(100, ChestClass, new(true, 201, true, null, 1, 2), 5);

        var opening = Assert.Single(data.Finish(new Dictionary<uint, uint>(), new Dictionary<uint, string>()));
        Assert.Equal(0, opening.ResetCycleIndex);
        Assert.Equal(200u, opening.SearchingPawnGuid);
        Assert.Equal(1f, opening.ReplayTimeSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALaterCycleCannotFillAnEarlierUnidentifiedOpening(bool completeNextCycle)
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, new(true, null, false, null, 1, 1), 1);
        data.Update(100, ChestClass, new(false, 201, true, null, 0, 2), 2);
        if (completeNextCycle)
            data.Update(100, ChestClass, new(true, 0, true, null, 1, 2), 3);

        var openings = data.Finish(new Dictionary<uint, uint> { [201] = 301 },
            new Dictionary<uint, string> { [301] = "next-opener" });

        Assert.Equal(completeNextCycle ? 2 : 1, openings.Count);
        Assert.Null(openings[0].SearchingPawnGuid);
        Assert.Null(openings[0].PlayerId);
        if (completeNextCycle)
        {
            Assert.Equal(201u, openings[1].SearchingPawnGuid);
            Assert.Equal("next-opener", openings[1].PlayerId);
            Assert.Equal(1, openings[1].ResetCycleIndex);
        }
    }

    [Fact]
    public void ResetClearsThePreviousCyclesActivePawn()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, new(true, 200, true, null, 1, 2), 1);
        data.Update(100, ChestClass, new(false, null, false, null, 0, 1), 2);
        data.Update(100, ChestClass, new(true, null, false, null, 1, 1), 3);

        var openings = data.Finish(new Dictionary<uint, uint>(), new Dictionary<uint, string>());
        Assert.Equal(2, openings.Count);
        Assert.Equal(200u, openings[0].SearchingPawnGuid);
        Assert.Null(openings[1].SearchingPawnGuid);
    }

    [Fact]
    public void RepeatedZeroStateExportsDoNotEraseTheCurrentInteractionOrAddCycles()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, new(true, 200, true, null, 1, 2), 1);
        data.Update(100, ChestClass, new(false, null, false, null, 0, 1), 2);
        data.Update(100, ChestClass, new(null, 201, true, null, null, 1), 3);
        data.Update(100, ChestClass, new(false, null, false, null, 0, 1), 4);
        data.Update(100, ChestClass, new(true, 0, true, null, 1, 2), 5);

        var openings = data.Finish(new Dictionary<uint, uint>(), new Dictionary<uint, string>());
        Assert.Equal(2, openings.Count);
        Assert.Equal(1, openings[1].ResetCycleIndex);
        Assert.Equal(201u, openings[1].SearchingPawnGuid);
        Assert.Equal(5f, openings[1].ReplayTimeSeconds);
    }

    [Fact]
    public void InitialResetMarkerClearsAnUnfinishedInteraction()
    {
        var data = new ReplayChestAccumulator();
        data.Update(100, ChestClass, false, 200, true, 1);
        data.Update(100, ChestClass, new(false, null, false, null, 0, 1), 2);
        data.Update(100, ChestClass, new(true, null, false, null, 1, 1), 3);

        var opening = Assert.Single(data.Finish(new Dictionary<uint, uint>(), new Dictionary<uint, string>()));
        Assert.Equal(0, opening.ResetCycleIndex);
        Assert.Null(opening.SearchingPawnGuid);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChestFieldsDecodeByNameAndSkipUnrelatedUnalignedData(bool checksum)
    {
        var group = Group(ChestClass, "SearchingPawn", "Health", "bAlreadySearched");
        var values = new List<ReplayBits>();
        if (checksum) values.Add(ReplayBits.UInt(0, 1));
        values.AddRange([Property(1, ReplayBits.Packed(98765)), Property(2, ReplayBits.UInt(0b10111, 5)),
            Property(3, ReplayBits.UInt(1, 1)), ReplayBits.Packed(0)]);
        var bytes = ReplayBits.Join(values);
        using var reader = new NetBitReader(bytes.Bytes, bytes.Length);

        Assert.True(ReplayChestAnalysisReader.TryReadChestUpdate(reader, group, checksum,
            out var searched, out var pawn, out var hasPawn, out var fields));
        Assert.True(searched);
        Assert.Equal(98765u, pawn);
        Assert.True(hasPawn);
        Assert.Equal(2, fields);
        Assert.True(reader.AtEnd());
        Assert.False(reader.IsError);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void SearchedFalseAndNullPawnAreRecordedUpdates(uint searchedBit)
    {
        var bits = ReplayBits.Join([Property(1, ReplayBits.UInt(searchedBit, 1)),
            Property(2, ReplayBits.Packed(0)), ReplayBits.Packed(0)]);
        using var reader = new NetBitReader(bits.Bytes, bits.Length);

        Assert.True(ReplayChestAnalysisReader.TryReadChestUpdate(reader,
            Group(ChestClass, "bAlreadySearched", "SearchingPawn"), false,
            out var searched, out var pawn, out var hasPawn, out var fields));
        Assert.Equal(searchedBit == 1, searched);
        Assert.Equal(0u, pawn);
        Assert.True(hasPawn);
        Assert.Equal(2, fields);
    }

    [Fact]
    public void TruncatedOrIncorrectlySizedSearchFieldsAreRejected()
    {
        var group = Group(ChestClass, "bAlreadySearched", "SearchingPawn");
        var invalid = new[]
        {
            ReplayBits.Join([ReplayBits.Packed(1), ReplayBits.Packed(10), ReplayBits.UInt(1, 1)]),
            ReplayBits.Join([Property(1, ReplayBits.UInt(1, 2)), ReplayBits.Packed(0)]),
            ReplayBits.Join([Property(2, ReplayBits.UInt(1, 7)), ReplayBits.Packed(0)]),
            ReplayBits.Join([ReplayBits.Packed(3), ReplayBits.Packed(1), ReplayBits.UInt(1, 1), ReplayBits.Packed(0)]),
            Property(1, ReplayBits.UInt(1, 1))
        };

        foreach (var bits in invalid)
        {
            using var reader = new NetBitReader(bits.Bytes, bits.Length);
            Assert.False(ReplayChestAnalysisReader.TryReadChestUpdate(reader, group, false,
                out _, out _, out _, out _));
        }
    }

    [Theory]
    [InlineData("Tiered_Chest_Athena_C", "bAlreadySearched", true)]
    [InlineData("Tiered_Chest_Athena_FactionChest_C", "bAlreadySearched", true)]
    [InlineData("Tiered_Ammo_Chest_C", "bAlreadySearched", false)]
    [InlineData("Tiered_Chest_Athena_C", "Health", false)]
    [InlineData("SupplyDrop_C", "bAlreadySearched", false)]
    public void SearchableChestClassesAreDistinguishedFromAmmoAndOtherContainers(string path, string field, bool expected)
    {
        Assert.Equal(expected, ReplayChestAnalysisReader.IsChestGroup(Group(path, field)));
    }

    [Fact]
    public void ChestReportRetainsOwnerMatchDateAndModeFieldsWhileSkippingWorldState()
    {
        var group = Group("/Game/Athena/Athena_GameState.Athena_GameState_C",
            "RecorderPlayerState", "GameSessionId", "UtcTimeStartedMatch", "EventTournamentRound", "bIsLargeTeamGame",
            "TeamSize", "ReplicatedWorldTimeSeconds", "TeamFlightPaths");

        ReplayChestAnalysisReader.KeepReportFields(group);

        Assert.All(group.NetFieldExports.Take(6), field => Assert.False(field.Incompatible));
        Assert.All(group.NetFieldExports.Skip(6), field => Assert.True(field.Incompatible));
    }

    [Fact]
    public void ChestReportRetainsHumanAndBotIdentityTeamAndNamesWhileSkippingCosmetics()
    {
        var group = Group("/Script/FortniteGame.FortPlayerStateAthena",
            "PlayerId", "PlayerID", "UniqueId", "UniqueID", "PlatformUniqueNetId", "BotUniqueId", "bIsABot",
            "bOnlySpectator", "TeamIndex", "PlayerNameCustomOverride", "StreamerModeName", "SoftParts", "HeroType");

        ReplayChestAnalysisReader.KeepReportFields(group);

        Assert.All(group.NetFieldExports.Take(11), field => Assert.False(field.Incompatible));
        Assert.All(group.NetFieldExports.Skip(11), field => Assert.True(field.Incompatible));
    }

    [Fact]
    public void ChestReportPawnOnlyNeedsPlayerStateAttribution()
    {
        var group = Group("/Game/Athena/PlayerPawn_Athena.PlayerPawn_Athena_C",
            "PlayerState", "ReplicatedMovement", "PrimaryAssets", "Dances", "CurrentWeapon");

        ReplayChestAnalysisReader.KeepReportFields(group);

        Assert.False(group.NetFieldExports[0].Incompatible);
        Assert.All(group.NetFieldExports.Skip(1), field => Assert.True(field.Incompatible));
    }

    private static ReplayBits Property(uint handle, ReplayBits value) =>
        ReplayBits.Join([ReplayBits.Packed(handle), ReplayBits.Packed((uint)value.Length), value]);

    private static NetFieldExportGroup Group(string path, params string[] fields) => new()
    {
        PathName = path,
        NetFieldExportsLength = (uint)fields.Length,
        NetFieldExports = fields.Select((name, handle) => new NetFieldExport
        {
            Name = name, Handle = (uint)handle
        }).ToArray()
    };
}
