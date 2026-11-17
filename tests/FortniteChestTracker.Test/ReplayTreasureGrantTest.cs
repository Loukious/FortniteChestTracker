using System.Reflection;
using System.Text;
using FortniteReplayReader.Analysis;
using FortniteReplayReader.Editing;
using FortniteReplayReader.Models;
using Unreal.Core;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;
using Xunit;

namespace FortniteReplayReader.Test;

public class ReplayTreasureGrantTest
{
    private const string Handler = "Default__GA_TreasureHandler_Generic_C";
    private static readonly Dictionary<uint, string> Paths = new() { [5617] = Handler };

    [Fact]
    public void ObservedNamedAbilityPayloadDecodesCompletelyAndOnlyReferencesItsAbilityField()
    {
        // A complete public FastArray payload, including the unrelated second spec.
        // Actor identity is supplied separately and no private replay file is needed.
        const string sample = "6QAAAOYAAAAAAAAABAAAAIAAAAA8AEc2AAAAAAAAQIAAAAAARASSAP3///+bgIzJoAAFAAAAqAD9////s4CMWbkIhAEBAMABFBDQAaljBQAAAAQAAPAANBQBAAAAAAAAAQIAAAAQEUgC9P///28CMm6FAhQAAACgAvT////PAgHgIhAGBAAAB1BAQAekQB0AAAA=";
        using var archive = Archive(new(Convert.FromBase64String(sample), 1165));
        Assert.True(ReplayTreasureGrantDecoder.TryReadArray(archive, Group(), out var update));
        Assert.True(archive.AtEnd());
        Assert.Equal(116, update.ReplicationKey);
        Assert.Equal(115, update.BaseReplicationKey);
        Assert.Empty(update.DeletedIds);
        Assert.Equal(2, update.ChangedSpecs.Count);
        var handler = Assert.Single(update.ChangedSpecs, spec => spec.AbilityGuid == 5617);
        Assert.Equal(128, handler.Id);
        Assert.Equal(0u, handler.SourceObjectGuid);
        var data = new ReplayTreasureGrantAccumulator();
        data.Apply(52, 300, update, 96.3089f);
        var receipt = Assert.Single(Finish(data));
        Assert.Equal(52u, receipt.AbilitySystemActorGuid);
        Assert.Equal(128, receipt.AbilitySpecId);
        Assert.Equal("GA_TreasureHandler_Generic_C", receipt.HandlerClass);
        Assert.Equal(96.3089f, receipt.ReplayTimeSeconds);
    }

    [Fact]
    public void ReorderedExportsUseTheNamedAbilityAndNeverMatchArbitraryShortValuesOrSourceObjects()
    {
        var group = Group(ability: 7, source: 3);
        var body = Delta(1, [], Element(4, Property(2, ReplayBits.Packed(5617)),
            Property(4, ReplayBits.Packed(5617)), Property(8, ReplayBits.Packed(999))));
        var data = new ReplayTreasureGrantAccumulator();
        data.Apply(52, 300, Decode(body, group), 1);
        Assert.Empty(Finish(data));
        data.Apply(52, 300, Decode(Delta(2, [], Element(5, Property(8, ReplayBits.Packed(5617)))), group), 2);
        Assert.Equal(5, Assert.Single(Finish(data)).AbilitySpecId);
    }

    [Theory]
    [InlineData("GA_TreasureHandler_Generic_C", false)]
    [InlineData("/DelilahTreasureHuntGameplay/Gameplay/Abilities/PrizeHandlers/GA_TreasureHandler_Generic", false)]
    [InlineData("Default__GA_TreasureKey_InventoryEvents_C", false)]
    [InlineData("Default__GA_Prizes_EventListener_C", false)]
    [InlineData("Default__GA_TreasureHandler__C", false)]
    [InlineData("Default__GA_TreasureHandler_Generic_C", true)]
    [InlineData("/Plugin/Handler.Default__GA_TreasureHandler_NewHandler_C", true)]
    public void OnlyHandlerClassDefaultObjectsAreReceiptEvidence(string path, bool expected) =>
        Assert.Equal(expected, ReplayTreasureGrantDecoder.TryGetHandlerClass(path, out _));

    [Fact]
    public void RepeatedExportsDeduplicateAndRealDeletesPermitAnotherSequence()
    {
        var data = new ReplayTreasureGrantAccumulator();
        var first = Decode(Delta(1, [], Element(128, Property(19, ReplayBits.Packed(5617)))));
        data.Apply(52, 300, first, 1);
        data.Apply(52, 300, first, 2);
        data.Apply(52, 300, Decode(Delta(2, [], Element(128, Property(19, ReplayBits.Packed(5617))))), 3);
        Assert.Single(Finish(data));
        data.Apply(52, 300, Decode(Delta(3, [128])), 4);
        var readd = Decode(Delta(4, [], Element(128, Property(19, ReplayBits.Packed(5617)))));
        data.Apply(52, 300, readd, 5);
        data.Apply(52, 300, readd, 6);
        var receipts = Finish(data);
        Assert.Equal([1, 2], receipts.Select(receipt => receipt.GrantSequence));
        Assert.Equal([1f, 5f], receipts.Select(receipt => receipt.ReplayTimeSeconds!.Value));
    }

    [Fact]
    public void RepeatedDeleteAndReaddDeltaCannotManufactureAdditionalReceipts()
    {
        var data = new ReplayTreasureGrantAccumulator();
        var first = Decode(Delta(1, [], Element(128, Property(19, ReplayBits.Packed(5617)))));
        var second = Decode(Delta(2, [128], Element(128, Property(19, ReplayBits.Packed(5617)))));
        data.Apply(52, 300, first, 1);
        data.Apply(52, 300, second, 2);
        data.Apply(52, 300, second, 3);
        Assert.Equal(2, Finish(data).Count);
    }

    [Fact]
    public void IdenticalSpecIdsAreScopedToComponentAndRecipient()
    {
        var data = new ReplayTreasureGrantAccumulator();
        var body = Decode(Delta(1, [], Element(128, Property(19, ReplayBits.Packed(5617)))));
        data.Apply(52, 300, body, 1);
        data.Apply(53, 300, body, 2);
        data.Apply(52, 301, body, 3);
        var receipts = data.Finish(new Dictionary<uint, uint>(), new HashSet<uint> { 300, 301 },
            new Dictionary<uint, string> { [300] = "owner-id", [301] = "teammate-id" }, Paths);
        Assert.Equal(3, receipts.Count);
        Assert.Equal(2, receipts.Count(receipt => receipt.PlayerId == "owner-id"));
        Assert.Single(receipts, receipt => receipt.PlayerId == "teammate-id");
    }

    [Fact]
    public void RecipientAttributionRequiresAPlayerStateAndCanResolveLatePawnOrIdentityExports()
    {
        var data = new ReplayTreasureGrantAccumulator();
        data.Apply(52, 200, Decode(Delta(1, [], Element(128, Property(19, ReplayBits.Packed(5617))))), 1);
        Assert.Empty(Finish(data));
        Assert.Equal(1, data.UnattributedGrants);
        var anonymous = Assert.Single(data.Finish(new Dictionary<uint, uint> { [200] = 300 },
            new HashSet<uint> { 300 }, new Dictionary<uint, string>(), Paths));
        Assert.Equal(300u, anonymous.PlayerStateActorGuid);
        Assert.Null(anonymous.PlayerId);
        var identified = Assert.Single(data.Finish(new Dictionary<uint, uint> { [200] = 300 },
            new HashSet<uint> { 300 }, new Dictionary<uint, string> { [300] = "late-id" }, Paths));
        Assert.Equal("late-id", identified.PlayerId);
    }

    [Fact]
    public void PartialSpecUpdatesDoNotRequireAnAbilityReferenceUntilItIsActuallyExported()
    {
        var data = new ReplayTreasureGrantAccumulator();
        data.Apply(52, 300, Decode(Delta(1, [], Element(128, Property(20, ReplayBits.UInt(1, 32))))), 1);
        Assert.Empty(Finish(data));
        data.Apply(52, 300, Decode(Delta(2, [], Element(128, Property(19, ReplayBits.Packed(5617))))), 2);
        Assert.Equal(2f, Assert.Single(Finish(data)).ReplayTimeSeconds);
    }

    [Fact]
    public void AnEarlierNonhandlerReferenceCannotBlockALaterConfirmedHandlerOnTheSameSpec()
    {
        var data = new ReplayTreasureGrantAccumulator();
        data.Apply(52, 300, Decode(Delta(1, [], Element(128, Property(19, ReplayBits.Packed(999))))), 1);
        data.Apply(52, 300, Decode(Delta(2, [], Element(128, Property(19, ReplayBits.Packed(5617))))), 2);
        data.Apply(52, 300, Decode(Delta(3, [], Element(128, Property(19, ReplayBits.Packed(5617))))), 3);
        Assert.Equal(2f, Assert.Single(Finish(data)).ReplayTimeSeconds);
    }

    [Fact]
    public void EveryTruncatedOrTrailingArrayPayloadIsRejectedAtomically()
    {
        var body = Delta(1, [127], Element(128, Property(19, ReplayBits.Packed(5617))),
            Element(129, Property(19, ReplayBits.Packed(999))));
        for (var length = 0; length < body.Length; length++)
        {
            using var archive = Archive(new(body.Bytes, length));
            Assert.False(ReplayTreasureGrantDecoder.TryReadArray(archive, Group(), out var update));
            Assert.Null(update);
        }
        using var trailing = Archive(ReplayBits.Join([body, ReplayBits.UInt(0, 1)]));
        Assert.False(ReplayTreasureGrantDecoder.TryReadArray(trailing, Group(), out var rejected));
        Assert.Null(rejected);
    }

    [Fact]
    public void InvalidCountsIdsHandlesAndObjectReferenceFramingAreRejected()
    {
        ReplayBits[] invalid = [
            Delta(1, [-1]), Delta(1, [128, 128]),
            Delta(1, [], Element(128), Element(128)),
            Delta(1, [], Element(128, Property(19, ReplayBits.UInt(1, 7)))),
            Delta(1, [], Element(128, Property(19, ReplayBits.Join([ReplayBits.Packed(5617), ReplayBits.UInt(0, 8)])))),
            Delta(1, [], Element(128, Property(19, ReplayBits.Packed(5617)), Property(19, ReplayBits.Packed(5617)))),
            Delta(1, [], Element(128, Property(97, ReplayBits.Packed(5617)))),
            Delta(1, [], Element(128, Property(19, ReplayBits.UInt(uint.MaxValue, 32))))
        ];
        foreach (var body in invalid)
        {
            using var archive = Archive(body);
            Assert.False(ReplayTreasureGrantDecoder.TryReadArray(archive, Group(), out var update));
            Assert.Null(update);
        }
    }

    [Fact]
    public void UnsupportedUnnamedAbilityExportsCannotProduceReceipts()
    {
        var group = Group();
        group.NetFieldExports[18] = null;
        using var archive = Archive(Delta(1, [], Element(128, Property(19, ReplayBits.Packed(5617)))));
        Assert.False(ReplayTreasureGrantDecoder.TryReadArray(archive, group, out _));
    }

    [Fact]
    public void APlayerStateAndItsPawnResolveToTheSameRecipientScope()
    {
        var data = new ReplayTreasureGrantAccumulator();
        var update = Decode(Delta(1, [], Element(128, Property(19, ReplayBits.Packed(5617)))));
        data.Apply(52, 300, update, 1);
        data.Apply(52, 200, update, 2);
        Assert.Single(data.Finish(new Dictionary<uint, uint> { [200] = 300 }, new HashSet<uint> { 300 },
            new Dictionary<uint, string>(), Paths));
    }

    [Fact]
    public void ReaderPreservesTheOriginalCdoNameAndUsesTheActualComponentGuid()
    {
        using var container = ReplayTreasureRewardTest.EmptyReplay();
        var reader = new ReplayChestAnalysisReader(container);
        ExportObject(reader, 5617, Handler);
        ExportObject(reader, 5618, "GA_TreasureHandler_Generic_C");
        var cache = ReaderCache(reader);
        Assert.Equal("GA_TreasureHandler_Generic_C", cache.NetGuidToPathName[5617]);
        var group = SetAbilityTables(reader);
        SetRecipientChannel(reader, 7, 300);
        SetPlayerState(reader, 300);
        var header = ReplayBits.Join([ReplayBits.UInt(0, 1), ReplayBits.UInt(0, 1),
            ReplayBits.Packed(52), ReplayBits.UInt(1, 1)]); // Stably named component, class determined by its export.
        using var headerReader = Archive(header);
        Assert.Equal(52u, reader.ReadContentBlockHeader(new() { ChIndex = 7, Archive = headerReader }, out _, out _));
        var body = CachePayload(Delta(1, [], Element(128, Property(19, ReplayBits.Packed(5617))),
            Element(129, Property(19, ReplayBits.Packed(5618)))));
        using var payloadReader = Archive(body);
        Assert.True(reader.ReceivedReplicatorBunch(new() { ChIndex = 7, Archive = payloadReader }, payloadReader, 901, false));
        var receipt = Assert.Single(reader.Scan().Grants);
        Assert.Equal(52u, receipt.AbilitySystemActorGuid);
        Assert.Equal(300u, receipt.PlayerStateActorGuid);
        Assert.Equal(128, receipt.AbilitySpecId);
    }

    [Fact]
    public void TheNamedPlayerStateDefaultSubobjectUsesTheExportedAthenaSchema()
    {
        using var container = ReplayTreasureRewardTest.EmptyReplay();
        var reader = new ReplayChestAnalysisReader(container);
        ExportObject(reader, 5617, Handler);
        SetAbilityTables(reader);
        var cache = ReaderCache(reader);
        var generic = Group();
        generic.PathName = "/Script/GameplayAbilities.AbilitySystemComponent";
        generic.PathNameIndex = 3;
        generic.NetFieldExports[18] = null;
        cache.AddToExportGroupMap(generic.PathName, generic);
        cache.NetGuidToPathName[52] = "AbilitySystemComponent";
        SetRecipientChannel(reader, 7, 300);
        SetPlayerState(reader, 300);
        ReaderField<uint>(reader, "currentContentObject", 52);
        var body = CachePayload(Delta(1, [], Element(128, Property(19, ReplayBits.Packed(5617)))));
        using var payloadReader = Archive(body);
        Assert.True(reader.ReceivedReplicatorBunch(new() { ChIndex = 7, Archive = payloadReader }, payloadReader, 52, false));
        var analysis = reader.Scan();
        Assert.Equal(52u, Assert.Single(analysis.Grants).AbilitySystemActorGuid);
        Assert.False(analysis.HasGrantDecodeErrors);
    }

    [Fact]
    public void AnUntypedAbilitySubobjectWithoutAVerifiedPlayerStateOwnerCannotUseAGuessedSchema()
    {
        using var container = ReplayTreasureRewardTest.EmptyReplay();
        var reader = new ReplayChestAnalysisReader(container);
        ExportObject(reader, 5617, Handler);
        SetAbilityTables(reader);
        ReaderCache(reader).NetGuidToPathName[52] = "AbilitySystemComponent";
        SetRecipientChannel(reader, 7, 999);
        ReaderField<uint>(reader, "currentContentObject", 52);
        var body = CachePayload(Delta(1, [], Element(128, Property(19, ReplayBits.Packed(5617)))));
        using var payloadReader = Archive(body);
        Assert.True(reader.ReceivedReplicatorBunch(new() { ChIndex = 7, Archive = payloadReader }, payloadReader, 52, false));
        var analysis = reader.Scan();
        Assert.Empty(analysis.Grants);
        Assert.False(analysis.HasGrantDecodeErrors);
    }

    [Fact]
    public void ADeclaredContentTailWithMissingDataCannotCommitAnEarlierValidReceipt()
    {
        using var container = ReplayTreasureRewardTest.EmptyReplay();
        var reader = new ReplayChestAnalysisReader(container);
        ExportObject(reader, 5617, Handler);
        SetAbilityTables(reader);
        SetRecipientChannel(reader, 7, 300);
        SetPlayerState(reader, 300);
        ReaderField<uint>(reader, "currentContentObject", 52);
        var valid = CachePayload(Delta(1, [], Element(128, Property(19, ReplayBits.Packed(5617)))));
        var tail = ReplayBits.Join([ReplayBits.UInt(1, 6), ReplayBits.Packed(8)]);
        using var payloadReader = Archive(ReplayBits.Join([valid, tail]));
        Assert.False(reader.ReceivedReplicatorBunch(new() { ChIndex = 7, Archive = payloadReader }, payloadReader, 901, false));
        var analysis = reader.Scan();
        Assert.Empty(analysis.Grants);
        Assert.True(analysis.HasGrantDecodeErrors);
    }

    private static void ExportObject(ReplayChestAnalysisReader reader, uint guid, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name + '\0');
        var body = ReplayBits.Join([ReplayBits.Packed(guid), ReplayBits.UInt(1, 8), ReplayBits.Packed(0),
            ReplayBits.UInt((uint)bytes.Length, 32), new ReplayBits(bytes, bytes.Length * 8)]);
        using var archive = Archive(body);
        Assert.Equal(guid, reader.InternalLoadObject(archive, true).Value);
        Assert.True(archive.AtEnd());
    }
    private static NetFieldExportGroup SetAbilityTables(ReplayChestAnalysisReader reader)
    {
        var group = Group();
        var cache = ReaderCache(reader);
        cache.NetGuidToPathName[901] = "FortAbilitySystemComponentAthena";
        cache.AddToExportGroupMap(group.PathName, group);
        var fields = new NetFieldExport?[41];
        fields[1] = new() { Handle = 1, Name = "ActivatableAbilities" };
        cache.AddToExportGroupMap(group.PathName + "_ClassNetCache", new()
        {
            PathName = group.PathName + "_ClassNetCache", PathNameIndex = 2,
            NetFieldExportsLength = (uint)fields.Length, NetFieldExports = fields
        });
        return group;
    }
    private static ReplayBits CachePayload(ReplayBits array) =>
        ReplayBits.Join([ReplayBits.UInt(1, 6), ReplayBits.Packed((uint)array.Length), array]);
    private static NetGuidCache ReaderCache(ReplayChestAnalysisReader reader) =>
        (NetGuidCache)typeof(Unreal.Core.ReplayReader<FortniteReplay>)
            .GetField("_netGuidCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
    private static void SetRecipientChannel(ReplayChestAnalysisReader reader, uint channel, uint playerState)
    {
        var channels = (UChannel?[])typeof(Unreal.Core.ReplayReader<FortniteReplay>)
            .GetField("Channels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reader)!;
        channels[channel] = new() { Actor = new() { ActorNetGUID = new() { Value = playerState } } };
    }
    private static void SetPlayerState(ReplayChestAnalysisReader reader, uint playerState) =>
        ((HashSet<uint>)typeof(ReplayChestAnalysisReader).GetField("knownPlayerStates", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(reader)!).Add(playerState);
    private static void ReaderField<T>(ReplayChestAnalysisReader reader, string name, T value) =>
        typeof(ReplayChestAnalysisReader).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(reader, value);

    private static IReadOnlyList<ReplayTreasureGrant> Finish(ReplayTreasureGrantAccumulator data) =>
        data.Finish(new Dictionary<uint, uint>(), new HashSet<uint> { 300 },
            new Dictionary<uint, string> { [300] = "owner-id" }, Paths);
    private static NetBitReader Archive(ReplayBits body) => new(body.Bytes, body.Length)
        { EngineNetworkVersion = EngineNetworkVersionHistory.LATEST };
    private static ReplayAbilityArrayUpdate Decode(ReplayBits body, NetFieldExportGroup? group = null)
    {
        using var archive = Archive(body);
        Assert.True(ReplayTreasureGrantDecoder.TryReadArray(archive, group ?? Group(), out var update));
        Assert.True(archive.AtEnd());
        return update;
    }
    private static ReplayBits Delta(int key, int[] deletes, params ReplayBits[] changes) => ReplayBits.Join([
        ReplayBits.UInt(1, 1), ReplayBits.UInt((uint)key, 32), ReplayBits.UInt((uint)(key - 1), 32),
        ReplayBits.UInt((uint)deletes.Length, 32), ReplayBits.UInt((uint)changes.Length, 32),
        .. deletes.Select(id => ReplayBits.UInt((uint)id, 32)), .. changes]);
    private static ReplayBits Element(int id, params ReplayBits[] fields) =>
        ReplayBits.Join([ReplayBits.UInt((uint)id, 32), .. fields, ReplayBits.Packed(0)]);
    private static ReplayBits Property(uint wireHandle, ReplayBits value) =>
        ReplayBits.Join([ReplayBits.Packed(wireHandle), ReplayBits.Packed((uint)value.Length), value]);
    private static NetFieldExportGroup Group(int ability = 18, int source = 21)
    {
        var fields = new NetFieldExport?[96];
        fields[ability] = new() { Handle = (uint)ability, Name = "ability" };
        fields[source] = new() { Handle = (uint)source, Name = "SourceObject" };
        return new() { PathName = "/Script/FortniteGame.FortAbilitySystemComponentAthena",
            NetFieldExportsLength = (uint)fields.Length, NetFieldExports = fields };
    }
}
