using System.Globalization;
using System.Text;

namespace FortniteChestTracker.Core;

public static class ChestReportExporter
{
    public static void WriteCsv(ChestScanReport report, string path)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Match date,Mode,Playlist,Match ID,Replay file,Replay owner,Player,Player ID,Role,Chests opened,Owner chests,Teammate chests,Squad chests,Coverage,Row type,Reward category,Reward tier,Reward replay time,Prize template ID,Reward evidence");
        foreach (var match in report.Matches)
        {
            foreach (var player in match.Players)
                writer.WriteLine(string.Join(",", new[]
                {
                    match.MatchDate.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    match.Mode, match.Playlist, match.MatchId, match.FileName, match.OwnerName,
                    player.Name, player.PlayerId, player.IsOwner ? "Replay owner" : "Teammate",
                    Count(player.Chests), Count(match.OwnerChests), Count(match.TeammateChests), Count(match.SquadChests),
                    player.Coverage + "; " + match.Coverage, "Chest", "", "", "", "", ""
                }.Select(Escape)));
            foreach (var reward in match.Rewards)
                writer.WriteLine(string.Join(",", new[]
                {
                    match.MatchDate.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    match.Mode, match.Playlist, match.MatchId, match.FileName, match.OwnerName,
                    reward.PlayerName, reward.PlayerId ?? "", reward.IsOwner ? "Replay owner" : reward.IsTeammate ? "Teammate" :
                        reward.PlayerId is null ? "Unidentified player" : "Other player",
                    "", "", "", "", match.RewardCoverage, "Reward", reward.Category, reward.Tier,
                    ReplayTime(reward.ReplayTimeSeconds), reward.PrizeTemplateId, reward.Evidence
                }.Select(Escape)));
        }
    }

    private static string Count(int? count) => count?.ToString(CultureInfo.InvariantCulture) ?? "Unknown";
    private static string ReplayTime(float? seconds)
    {
        if (seconds is not float value || !float.IsFinite(value) || value < 0) return "Unknown";
        var total = (long)Math.Floor(value);
        return $"{total / 60}:{total % 60:00}";
    }
    private static string Escape(string value)
    {
        // Keep player-controlled names from becoming spreadsheet formulas.
        var trimmed = value.TrimStart();
        if (value.Length > 0 && ("\t\r\n".Contains(value[0]) ||
            trimmed.Length > 0 && "=+-@".Contains(trimmed[0]))) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
