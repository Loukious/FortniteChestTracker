using FortniteReplayReader.Analysis;
using Unreal.Core.Models;
using Xunit;

namespace FortniteReplayReader.Test;

public class ReplayDisconnectAnalysisTest
{
    [Fact]
    public void SurvivalEndsOnEliminationAndRestartsOnReboot()
    {
        var data = new ReplaySurvivalAccumulator();
        data.UpdatePawn(10, 100, false, 0);
        data.UpdatePlayer(100, true, null, null, null, 30);
        data.UpdatePawn(10, null, true, 60);
        data.UpdatePlayer(100, null, 1, null, null, 120);
        data.UpdatePawn(12, 100, false, 120);
        data.UpdatePawn(10, null, true, 121);
        data.UpdatePlayer(100, null, null, 2, null, 180);
        var points = data.Finish(new Dictionary<uint, string> { [100] = "owner" });
        Assert.Equal([true, false, true, false], points.Select(p => p.IsAlive));
        Assert.Equal([0f, 60f, 120f, 180f], points.Select(p => p.ReplayTimeSeconds!.Value));
    }

    [Fact]
    public void PlacementAndMatchEndStopSurvivalEvenIfThePawnRemainsVisible()
    {
        var data = new ReplaySurvivalAccumulator();
        data.UpdatePawn(10, 100, false, 0);
        data.UpdatePlayer(100, null, null, 1, null, 60);
        data.UpdatePawn(10, null, false, 61);
        Assert.Equal([true, false], data.Finish(new Dictionary<uint, string>()).Select(p => p.IsAlive));
        var winner = new ReplaySurvivalAccumulator();
        winner.UpdatePawn(12, 200, false, 0);
        winner.EndMatch(100);
        winner.UpdatePawn(14, 200, false, 101);
        Assert.Equal([true, false], winner.Finish(new Dictionary<uint, string>()).Select(p => p.IsAlive));
    }

    [Fact]
    public void DisconnectsRemoveSurvivalAndRespawnedPawnsReestablishIt()
    {
        var data = new ReplaySurvivalAccumulator();
        data.UpdatePawn(10, 100, false, 0);
        data.UpdatePlayer(100, null, null, null, true, 60);
        data.UpdatePawn(10, null, false, 61);
        data.UpdatePlayer(100, null, null, null, false, 70);
        data.UpdatePawn(12, 100, false, 71);
        Assert.Equal([true, false, true], data.Finish(new Dictionary<uint, string>()).Select(p => p.IsAlive));
    }
    [Fact]
    public void FirstTrueFlagIsRecordedAndRepeatedFlagsDoNotDuplicateIt()
    {
        var data = new ReplayDisconnectAccumulator();
        data.Update(100, null, 1);
        data.Update(100, true, 2);
        data.Update(100, true, 3);
        var result = Assert.Single(data.Finish(new Dictionary<uint, string> { [100] = "teammate" }));
        Assert.Equal("teammate", result.PlayerId);
        Assert.Equal(2, result.ReplayTimeSeconds);
    }

    [Fact]
    public void DisconnectAfterReconnectionIsASeparateRecordedEvent()
    {
        var data = new ReplayDisconnectAccumulator();
        data.Update(100, false, 1);
        data.Update(100, true, 10);
        data.Update(100, false, 20);
        data.Update(100, true, 30);
        Assert.Equal([10f, 30f], data.Finish(new Dictionary<uint, string> { [100] = "teammate" }).Select(d => d.ReplayTimeSeconds!.Value));
    }

    [Fact]
    public void LatePlayerIdentityCanResolveAnEarlierEventWithoutInventingTiming()
    {
        var data = new ReplayDisconnectAccumulator();
        data.Update(100, true, null);
        var unknown = Assert.Single(data.Finish(new Dictionary<uint, string>()));
        Assert.Null(unknown.PlayerId);
        var resolved = Assert.Single(data.Finish(new Dictionary<uint, string> { [100] = "owner" }));
        Assert.Equal("owner", resolved.PlayerId);
        Assert.Null(resolved.ReplayTimeSeconds);
    }

    [Fact]
    public void DisconnectionFieldIsRetainedWithRegionWhileUnrelatedFieldsAreSkipped()
    {
        var group = new NetFieldExportGroup { PathName = "/Script/FortniteGame.FortPlayerStateAthena",
            NetFieldExports = [new() { Name = "bIsDisconnected" }, new() { Name = "KeepPlayingTogetherMatchmakingRegion" }, new() { Name = "SoftParts" }] };
        ReplayChestAnalysisReader.KeepReportFields(group);
        Assert.False(group.NetFieldExports[0].Incompatible);
        Assert.False(group.NetFieldExports[1].Incompatible);
        Assert.True(group.NetFieldExports[2].Incompatible);
    }
}
