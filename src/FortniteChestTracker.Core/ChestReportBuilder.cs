namespace FortniteChestTracker.Core;

public static class ChestReportBuilder
{
    public static ChestScanReport Build(IEnumerable<ChestMatch> matches, IEnumerable<SkippedReplay> skipped, int filesScanned)
    {
        var rejected = skipped.ToList();
        var kept = new List<ChestMatch>();
        foreach (var group in matches.GroupBy(m => (string.IsNullOrWhiteSpace(m.MatchId) ? m.FilePath : m.MatchId) + "|" + m.OwnerId, StringComparer.OrdinalIgnoreCase))
        {
            // Prefer the original recording, then the longest available recording. Never add counts from copies.
            var ordered = group.OrderByDescending(m => m.FileName.StartsWith("UnsavedReplay-", StringComparison.OrdinalIgnoreCase) &&
                    !m.FileName.Contains("-edited", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(m => m.RecordingLengthInMs)
                .ThenBy(m => m.SquadChests is null)
                .ThenBy(m => m.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
            kept.Add(ordered[0]);
            foreach (var duplicate in ordered.Skip(1))
                rejected.Add(new SkippedReplay(duplicate.FileName, $"Duplicate match for this owner; counted {ordered[0].FileName}."));
        }
        return new ChestScanReport(kept.OrderByDescending(m => m.MatchDate).ToArray(), rejected, filesScanned);
    }
}
