import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
const moduleAt = async path => import(`data:text/javascript;base64,${Buffer.from(readFileSync(new URL(path, import.meta.url), 'utf8')).toString('base64')}`);
const { paceBins, timedRecording, cycleEstimate, calendarKey, periodTotals, regionKey, regionTotals, sameComparisonGroup, barChart } = await moduleAt('../wwwroot/analytics.js');
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
