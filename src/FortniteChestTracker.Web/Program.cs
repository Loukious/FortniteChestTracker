using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using FortniteChestTracker.Core;
using FortniteReplayReader.Analysis;
using FortniteReplayReader.Editing;

namespace FortniteChestTracker.Web;

[SupportedOSPlatform("browser")]
public static partial class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Main() { }

    [JSExport]
    public static string Ping() => "Fortnite chest parser ready";

    [JSExport]
    public static string ScanReplay([JSMarshalAs<JSType.Array<JSType.Number>>] byte[] bytes,
        string filePath, string sinceText, string contentHash)
    {
        try
        {
            var since = DateOnly.ParseExact(sinceText, "yyyy-MM-dd");
            var metadata = new ReplayMetadataReader().Read(bytes);
            if (metadata.Info.IsLive) return Skip(filePath, "Still recording; scan again when the match finishes.");
            var recordingDate = ChestEligibility.LocalMatchDate(null, metadata.Info.Timestamp);
            if (DateOnly.FromDateTime(recordingDate) < since)
                return Skip(filePath, $"Recorded before {since:yyyy-MM-dd}.");
            if (ChestEligibility.IsOtherSubgame(metadata.Header?.GameSpecificData))
                return Skip(filePath, "Creative or another non-Battle Royale subgame.");

            using var container = ReplayContainer.ReadForAnalysis(bytes);
            var analysis = new ReplayChestAnalysisReader(container).Scan();
            var replay = analysis.Replay;
            var matchDate = ChestEligibility.LocalMatchDate(replay.GameData.UtcTimeStartedMatch, replay.Info.Timestamp);
            if (DateOnly.FromDateTime(matchDate) < since)
                return Skip(filePath, $"Match started before {since:yyyy-MM-dd}.");
            var mode = ChestEligibility.GetMode(replay.GameData.CurrentPlaylist,
                replay.GameData.IsTournamentRound, replay.GameData.IsLargeTeamGame == true);
            if (mode is null)
                return Skip(filePath, "Outside standard BR / Zero Build: " +
                    (replay.GameData.CurrentPlaylist ?? "playlist unavailable"));
            var owners = replay.PlayerData.Where(player => player.IsReplayOwner && !player.IsBot).ToArray();
            if (owners.Length != 1 || string.IsNullOrWhiteSpace(owners[0].PlayerId))
                return Skip(filePath, "The recorded owner could not be uniquely identified.");
            var matchId = !string.IsNullOrWhiteSpace(replay.GameData.GameSessionId) ? replay.GameData.GameSessionId
                : !string.IsNullOrWhiteSpace(replay.Header.Guid) ? replay.Header.Guid : contentHash;
            var match = ChestMatchFactory.Build(analysis, owners[0], filePath, matchId, mode, matchDate);
            return JsonSerializer.Serialize(new { match }, JsonOptions);
        }
        catch (Exception error)
        {
            return Skip(filePath, $"Could not read replay: {error.Message}");
        }
    }

    [JSExport]
    public static string BuildReport(string matchesJson, string skippedJson, int filesScanned)
    {
        var matches = JsonSerializer.Deserialize<ChestMatch[]>(matchesJson, JsonOptions) ?? [];
        var skipped = JsonSerializer.Deserialize<SkippedReplay[]>(skippedJson, JsonOptions) ?? [];
        return JsonSerializer.Serialize(ChestReportBuilder.Build(matches, skipped, filesScanned), JsonOptions);
    }

    private static string Skip(string filePath, string reason) =>
        JsonSerializer.Serialize(new { skipped = new SkippedReplay(filePath, reason) }, JsonOptions);
}
