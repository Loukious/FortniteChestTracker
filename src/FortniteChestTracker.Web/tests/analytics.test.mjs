import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
const moduleAt = async path => import(`data:text/javascript;base64,${Buffer.from(readFileSync(new URL(path, import.meta.url), 'utf8')).toString('base64')}`);
const { paceBins, timedRecording, cycleEstimate, calendarKey, periodTotals, regionKey, regionTotals, sameComparisonGroup, farmingInsights, timeOfDayTotals, teammateRegionInsights, barChart } = await moduleAt('../wwwroot/analytics.js');
const { treasureStatus, countdown } = await moduleAt('../wwwroot/treasure-hour.js');
function match(times = [], seconds = 300, changes = {}) {
  return { ownerId: 'owner', playlist: 'BR', matchDate: '2026-11-23T12:00:00', recordingLengthInMs: seconds * 1000,
    players: [{ playerId: 'owner', isOwner: true, chests: times.length, coverage: 'Complete chest data' }],
    openingTimeline: times.map(t => ({ playerId: 'OWNER', replayTimeSeconds: t })), ...changes };
}

test('bin boundaries and final endpoint preserve all openings with partial exposure', () => {
  const { bins } = paceBins([match([0, 119.9, 120, 240, 300])]);
  assert.deepEqual(bins.map(b => b.count), [2, 1, 2]);
  assert.deepEqual(bins.map(b => b.minutes), [2, 2, 1]);
  assert.deepEqual(bins.map(b => b.rate), [1, .5, 2]);
});
test('pooled rates use actual exposure and do not invent zeroes after shorter replays end', () => {
  const { bins } = paceBins([match([60], 90), match([60, 180, 300], 360)]);
  assert.deepEqual(bins.map(b => b.matches), [2, 1, 1]);
  assert.deepEqual(bins.map(b => b.minutes), [3.5, 2, 2]);
  assert.equal(bins[0].rate, 2 / 3.5);
});
test('valid zero-opening matches contribute exposure; legacy or incomplete times do not', () => {
  const old = match([], 300, { openingTimeline: null });
  const incomplete = match([20], 300, { players: [{ playerId: 'owner', isOwner: true, chests: 2 }] });
  const result = paceBins([match([], 300), old, incomplete, match([NaN]), match([-1]), match([301]), match([], 0)]);
  assert.equal(result.included, 1);
  assert.equal(result.excluded, 6);
  assert.equal(result.bins[2].rate, 0);
});
test('a short recording endpoint stays in its observed bin within a longer pooled curve', () => {
  const { bins } = paceBins([match([120], 120), match([120, 240], 360)]);
  assert.deepEqual(bins.map(b => b.count), [1, 1, 1]);
  assert.deepEqual(bins.map(b => b.minutes), [4, 2, 2]);
  assert.deepEqual(bins.map(b => b.matches), [2, 1, 1]);
});
test('scopes attribute timings case-insensitively and ignore opponents', () => {
  const m = match([10], 120, { players: [
    { playerId: 'owner', isOwner: true, chests: 1 }, { playerId: 'mate', isOwner: false, chests: 2 }
  ], openingTimeline: [{ playerId: 'OWNER', replayTimeSeconds: 10 }, { playerId: 'mate', replayTimeSeconds: 20 },
    { playerId: 'MATE', replayTimeSeconds: 30 }, { playerId: 'enemy', replayTimeSeconds: 40 }] });
  assert.equal(paceBins([m], 'owner').bins[0].rate, .5);
  assert.equal(paceBins([m], 'teammates').bins[0].rate, 1);
  assert.equal(paceBins([m], 'squad').bins[0].rate, 1.5);
  assert.equal(timedRecording({ ...m, coverage: 'team membership unavailable' }, 'teammates')?.times.length, 2);
});
test('daily rates pair counts with duration, retaining older totals without opening times', () => {
  const groups = periodTotals([match([10, 20], 120), match([10], 60, { openingTimeline: null }),
    match([10, 20, 30], 0), match([], 300, { players: [{ playerId: 'owner', isOwner: true, chests: null }] })]);
  assert.equal(groups[0].count, 6);
  assert.equal(groups[0].rate, 1);
  assert.equal(groups[0].minutes, 3);
  assert.equal(groups[0].rateMatches, 2);
  assert.equal(groups[0].partial, true);
});
test('local calendar weeks begin Monday across month and year boundaries', () => {
  assert.equal(calendarKey('2026-11-22T23:59:59', 'week'), '2026-11-16');
  assert.equal(calendarKey('2026-11-23T00:00:00', 'week'), '2026-11-23');
  assert.equal(calendarKey('2027-01-01T12:00:00', 'week'), '2026-12-28');
  assert.equal(calendarKey('invalid'), null);
  assert.equal(periodTotals([match([], 60, { matchDate: '2026-11-22T12:00:00' }), match()], 'owner', 'week').length, 2);
});
test('cycle estimates include downtime once per replay and retain actual shorter durations', () => {
  const m = match([20, 40, 60, 90], 600);
  const result = cycleEstimate([m], 'owner', 2, 3);
  assert.equal(result.best.minute, 2);
  assert.equal(result.best.rate, 4 / 5);
  assert.equal(result.full.rate, 4 / 13);
  const mixed = cycleEstimate([m, match([20], 60)], 'owner', 2, 3);
  assert.equal(mixed.best.rate, 5 / 9);
  assert.equal(cycleEstimate([match([], 0)]), null);
});
test('comparisons keep owners and playlists separate', () => {
  assert.equal(sameComparisonGroup(match(), match([], 300, { ownerId: 'OWNER', playlist: 'br' })), true);
  assert.equal(sameComparisonGroup(match(), match([], 300, { ownerId: 'mate' })), false);
  assert.equal(sameComparisonGroup(match(), match([], 300, { playlist: 'Rumble' })), false);
});
test('region comparisons pool observed minutes and keep legacy/conflicting data separate', () => {
  const totals = regionTotals([match([10, 20], 120, { matchmakingRegion: 'EU' }), match([10], 60, { matchmakingRegion: 'eu' }),
    match([10, 20, 30], 120, { matchmakingRegion: 'NA' }), match([10], 60), match([], 0, { matchmakingRegion: 'NA' })]);
  assert.equal(totals.find(r => r.key === 'EU').rate, 1);
  assert.equal(totals.find(r => r.key === 'NA').rate, 1.5);
  assert.equal(totals.find(r => r.key === 'unknown').count, 1);
  assert.equal(regionKey({ matchmakingRegion: '<script>' }), 'unknown');
});
test('SVG escapes imported labels and exposes chart data for keyboard and assistive users', () => {
  const svg = barChart([{ label: '<script>"', count: 2, matches: 1 }], { label: '<danger>', metric: 'count' });
  assert.ok(svg.includes('tabindex="0"'));
  assert.ok(svg.includes('&lt;script&gt;&quot;'));
  assert.ok(!svg.includes('<script>'));
});

function farmingMatches(count, { region = 'EU', hour = 18, days = 2, seconds = 600, chests = 10, timed = false } = {}) {
  return Array.from({ length: count }, (_, i) => match(Array.from({ length: chests }, (_, j) => (j + 1) * 5), seconds, {
    matchId: `farm-${region}-${hour}-${i}`, matchmakingRegion: region,
    matchDate: `2026-11-${String(20 + i % days).padStart(2, '0')}T${String(hour).padStart(2, '0')}:00:00`,
    ...(!timed ? { openingTimeline: null } : {})
  }));
}
test('farming notes require two adequately sampled groups, ignoring lucky sparse sessions and unknown regions', () => {
  const notes = farmingInsights([...farmingMatches(5, { region: 'EU', chests: 10 }),
    ...farmingMatches(5, { region: 'NA', chests: 5 }), ...farmingMatches(1, { region: 'ME', chests: 100 }),
    ...farmingMatches(10, { region: undefined, chests: 200 }).map(m => ({ ...m, matchmakingRegion: null }))]);
  assert.equal(notes.region.kind, 'leader');
  assert.equal(notes.region.best.key, 'EU');
  assert.equal(notes.region.unknownMatches, 10);
  assert.equal(notes.region.best.rate, 50 / 65);
  assert.equal(farmingInsights(farmingMatches(5)).region.kind, 'single');
  assert.equal(farmingInsights(farmingMatches(20, { days: 1 })).region.kind, 'insufficient');
});
test('region ties are presented as similar rates rather than a strong winner', () => {
  const notes = farmingInsights([...farmingMatches(5, { region: 'EU', chests: 10 }), ...farmingMatches(5, { region: 'NA', chests: 10 })]);
  assert.equal(notes.region.kind, 'close');
});
test('time slots use local start time, distinct days, and paired duration including downtime', () => {
  const matches = [...farmingMatches(5, { hour: 18, chests: 10 }), ...farmingMatches(5, { hour: 6, chests: 3 }),
    ...farmingMatches(1, { hour: 23, chests: 100 })];
  const notes = farmingInsights(matches);
  assert.equal(notes.time.kind, 'leader');
  assert.equal(notes.time.best.label, '18:00–21:00');
  assert.equal(notes.time.best.days, 2);
  const midnight = timeOfDayTotals([match([1], 60, { matchDate: '2026-11-21T00:00:00' })], 'owner', 0);
  assert.equal(midnight[0].label, '00:00–03:00');
  assert.equal(midnight[0].rate, 1);
  assert.equal(notes.time.groups.find(g => g.key === '21').label, '21:00–24:00');
});
test('time and duration notes follow the region filter while region ranking retains all regions', () => {
  const notes = farmingInsights([...farmingMatches(5, { region: 'EU', hour: 18 }), ...farmingMatches(5, { region: 'NA', hour: 6 })], 'owner', { region: 'NA' });
  assert.equal(notes.region.groups.length, 2);
  assert.equal(notes.time.groups.length, 1);
  assert.equal(notes.time.best.key, '6');
});
test('restart notes use timestamped matches and actual partial recordings, including zero-opening matches', () => {
  const matches = farmingMatches(5, { timed: true });
  const notes = farmingInsights([...matches, ...farmingMatches(3, { chests: 0, seconds: 60, timed: true })]);
  assert.equal(notes.duration.kind, 'restart');
  assert.equal(notes.duration.best.minute, 2);
  assert.equal(notes.duration.best.matches, 5);
  assert.equal(notes.duration.best.rate, 50 / 37); // 5*(2+3) + 3*(1+3) minutes.
  assert.equal(notes.duration.fullRate, 50 / 77);
});
test('restart notes reject cutoffs reached by too few long survivors', () => {
  const matches = [...farmingMatches(5, { seconds: 180, chests: 1, timed: true }),
    ...farmingMatches(1, { seconds: 1800, chests: 100, timed: true })];
  const notes = farmingInsights(matches);
  assert.ok(!notes.duration.best || notes.duration.best.matches >= 5);
  assert.ok(!notes.duration.best || notes.duration.best.minute < 3);
});
test('uniform sustained openings produce a full-match note instead of a restart suggestion', () => {
  const matches = farmingMatches(5, { timed: true }).map(m => ({ ...m,
    openingTimeline: Array.from({ length: 10 }, (_, i) => ({ playerId: 'owner', replayTimeSeconds: (i + .5) * 60 })) }));
  assert.equal(farmingInsights(matches).duration.kind, 'continue');
  assert.equal(farmingInsights(matches, 'owner', { downtimeMinutes: 0 }).duration.kind, 'continue');
});
test('missing timestamps retain region/time notes but cannot produce restart advice', () => {
  const notes = farmingInsights([...farmingMatches(5, { region: 'EU', hour: 18 }), ...farmingMatches(5, { region: 'NA', hour: 6, chests: 5 })]);
  assert.equal(notes.region.kind, 'leader');
  assert.equal(notes.time.kind, 'leader');
  assert.equal(notes.duration.kind, 'insufficient');
  assert.equal(notes.duration.matches, 0);
  assert.equal(notes.duration.excluded, 10);
});
test('unreadable data, unknown scoped counts, and unavailable team membership cannot rank farming choices', () => {
  const matches = farmingMatches(5, { timed: true });
  assert.equal(farmingInsights(matches.map(m => ({ ...m, coverage: 'Some replay data unreadable' }))).samples, 0);
  assert.equal(farmingInsights(matches.map(m => ({ ...m, players: [{ playerId: 'owner', isOwner: true, chests: null }] }))).samples, 0);
  assert.equal(farmingInsights(matches.map(m => ({ ...m, coverage: 'team membership unavailable' })), 'squad').samples, 0);
  assert.equal(farmingInsights(farmingMatches(5, { chests: 0, timed: true })).duration.kind, 'no-openings');
});

function teammateMatches(region, earlyCount = 0) {
  return farmingMatches(5, { region, timed: true }).map((m, i) => ({ ...m,
    players: [...m.players, { playerId: 'mate', isOwner: false, chests: region === 'NA' ? 20 : 10, coverage: 'Complete chest data' }],
    disconnectMonitoringAvailable: true,
    playerDisconnects: i < earlyCount ? [{ playerId: 'MATE', replayTimeSeconds: 120 }] : []
  }));
}
test('teammate contribution and recorded disconnect rates can favor different regions', () => {
  const notes = teammateRegionInsights([...teammateMatches('EU', 0), ...teammateMatches('NA', 3)]);
  assert.equal(notes.contribution.best.key, 'NA');
  assert.equal(notes.stability.kind, 'leader');
  assert.equal(notes.stability.best.key, 'EU');
  assert.equal(notes.stability.runnerUp.rate, .6);
});
test('early disconnects are distinct from owner exits, later departures, and repeated teammate events', () => {
  const matches = teammateMatches('EU').map((m, i) => ({ ...m, playerDisconnects: i === 0 ? [
    { playerId: 'owner', replayTimeSeconds: 50 }, { playerId: 'mate', replayTimeSeconds: 500 }
  ] : i === 1 ? [{ playerId: 'mate', replayTimeSeconds: 120 }, { playerId: 'mate', replayTimeSeconds: 121 }] : [] }));
  const result = teammateRegionInsights(matches);
  assert.equal(result.stability.best.disconnectMatches, 1);
  assert.equal(result.stability.best.rate, .2);
});
test('legacy/short/untimed recordings are unavailable tracking, rather than evidence of teammates staying', () => {
  const matches = teammateMatches('EU');
  matches[0].disconnectMonitoringAvailable = null;
  matches[1].recordingLengthInMs = 300000;
  matches[2].playerDisconnects = [{ playerId: 'mate', replayTimeSeconds: null }];
  const result = teammateRegionInsights(matches);
  assert.equal(result.stability.groups[0].matches, 2);
  assert.equal(result.stability.unavailable, 3);
  assert.equal(result.stability.kind, 'insufficient');
  assert.equal(result.contribution.groups[0].matches, 5);
});
test('disconnect cutoff is adjustable and recordings need a minute beyond it', () => {
  const matches = teammateMatches('EU', 5);
  const earlier = teammateRegionInsights(matches, { earlyMinutes: 2 });
  assert.equal(earlier.stability.best.rate, 0); // Exactly at the cutoff is not before it.
  const later = teammateRegionInsights(matches, { earlyMinutes: 3 });
  assert.equal(later.stability.best.rate, 1);
  assert.equal(teammateRegionInsights(matches, { earlyMinutes: 10 }).stability.kind, 'insufficient');
});
test('Treasure Hour transitions start inclusively, end exclusively, and stop after listed dates', () => {
  const before = treasureStatus(Date.parse('2026-11-28T18:59:59Z'));
  assert.equal(before.remainingMs, 1000);
  assert.equal(before.confirmed, false);
  const active = treasureStatus(Date.parse('2026-11-28T19:00:00Z'));
  assert.equal(active.active, true);
  assert.equal(active.remainingMs, 7200000);
  assert.equal(treasureStatus(Date.parse('2026-11-28T21:00:00Z')).active, false);
  assert.equal(treasureStatus(Date.parse('2026-11-29T04:00:00Z')), null);
  assert.equal(countdown(90061000), '1d 01:01:01');
  assert.equal(countdown(-10), '00:00:00');
});
