// Rates use observed replay time. Replay time includes any recorded lobby/drop time.
const id = value => String(value ?? '').trim().toLowerCase();
const finiteCount = value => Number.isSafeInteger(value) && value >= 0;
const hasUnknownTeam = match => String(match.coverage ?? '').includes('team membership unavailable');
const inScope = (player, scope) => scope === 'squad' || (scope === 'teammates' ? !player.isOwner : player.isOwner);
const REGION_NAMES = { EU: 'Europe', NA: 'North America', NAE: 'NA East', NAW: 'NA West', NAC: 'NA Central',
  ASIA: 'Asia', BR: 'Brazil', OCE: 'Oceania', ME: 'Middle East', unknown: 'Unknown / conflicting' };
export function regionKey(match) {
  const key = String(match.matchmakingRegion ?? '').trim().toUpperCase();
  return Object.hasOwn(REGION_NAMES, key) ? key : 'unknown';
}
export function regionName(key) { return REGION_NAMES[key] ?? REGION_NAMES.unknown; }
export function regionTotals(matches, scope = 'owner') {
  const groups = new Map();
  for (const match of matches) {
    const key = regionKey(match), count = recordedCount(match, scope), seconds = recordingSeconds(match);
    if (!groups.has(key)) groups.set(key, { key, label: regionName(key), count: 0, minutes: 0, rateCount: 0, matches: 0, knownMatches: 0, partial: false, rate: null });
    const group = groups.get(key);
    group.matches++; group.count += count.count; group.partial ||= count.partial || !count.available;
    if (count.available) { group.knownMatches++; if (seconds) { group.rateCount += count.count; group.minutes += seconds / 60; } }
  }
  return [...groups.values()].sort((a, b) => a.label.localeCompare(b.label)).map(g => ({ ...g, rate: g.minutes > 0 ? g.rateCount / g.minutes : null }));
}

export function recordedCount(match, scope = 'owner') {
  const players = (match.players ?? []).filter(p => inScope(p, scope));
  const known = players.filter(p => finiteCount(p.chests));
  const unavailable = scope !== 'owner' && hasUnknownTeam(match);
  return { count: known.reduce((sum, p) => sum + p.chests, 0),
    available: known.length > 0 || players.length === 0 && !unavailable,
    partial: unavailable || known.length !== players.length || players.some(p => p.coverage !== 'Complete chest data') };
}

export function recordingSeconds(match) {
  const value = match.recordingLengthInMs / 1000;
  return Number.isFinite(value) && value > 0 && value <= 86400 ? value : null;
}

export function timedRecording(match, scope = 'owner') {
  const seconds = recordingSeconds(match), count = recordedCount(match, scope);
  if (!seconds || !count.available || !Array.isArray(match.openingTimeline)) return null;
  const players = new Set((match.players ?? []).filter(p => inScope(p, scope)).map(p => id(p.playerId)));
  const times = match.openingTimeline.filter(o => o && players.has(id(o.playerId)))
    .map(o => o.replayTimeSeconds);
  // Exclude incomplete timing coverage rather than manufacture zeroes or shrink the denominator.
  if (times.length !== count.count || times.some(t => !Number.isFinite(t) || t < 0 || t > seconds)) return null;
  return { seconds, times: times.sort((a, b) => a - b) };
}

export function paceBins(matches, scope = 'owner', windowMinutes = 2) {
  const width = [1, 2, 5].includes(Number(windowMinutes)) ? Number(windowMinutes) * 60 : 120;
  const recordings = matches.map(m => timedRecording(m, scope)).filter(Boolean);
  const bins = Array.from({ length: Math.ceil(Math.max(0, ...recordings.map(r => r.seconds)) / width) }, (_, i) =>
    ({ start: i * width / 60, end: (i + 1) * width / 60, count: 0, minutes: 0, matches: 0, rate: null }));
  for (const recording of recordings) {
    bins.forEach((bin, i) => {
      const exposure = Math.min(width, Math.max(0, recording.seconds - i * width)) / 60;
      if (exposure > 0) { bin.minutes += exposure; bin.matches++; }
    });
    // A final event on a boundary belongs to that recording's last observed bin,
    // even when a longer comparison recording creates later pooled bins.
    for (const time of recording.times) bins[Math.min(Math.ceil(recording.seconds / width) - 1, Math.floor(time / width))].count++;
  }
  for (const bin of bins) bin.rate = bin.minutes > 0 ? bin.count / bin.minutes : null;
  return { bins, included: recordings.length, excluded: matches.length - recordings.length };
}

export function cycleEstimate(matches, scope = 'owner', windowMinutes = 2, downtimeMinutes = 3) {
  const recordings = matches.map(m => timedRecording(m, scope)).filter(Boolean);
  if (!recordings.length) return null;
  const downtime = Math.min(60, Math.max(0, Number(downtimeMinutes) || 0));
  const width = [1, 2, 5].includes(Number(windowMinutes)) ? Number(windowMinutes) * 60 : 120;
  const longest = Math.max(...recordings.map(r => r.seconds));
  const stops = Array.from({ length: Math.ceil(longest / width) }, (_, i) => Math.min((i + 1) * width, longest));
  const points = stops.map(stop => {
    const minutes = recordings.reduce((sum, r) => sum + Math.min(stop, r.seconds) / 60 + downtime, 0);
    const count = recordings.reduce((sum, r) => sum + r.times.filter(t => t < stop || t === r.seconds && stop >= r.seconds).length, 0);
    return { minute: stop / 60, rate: count / minutes, count };
  });
  return { best: points.reduce((a, b) => b.rate > a.rate ? b : a), full: points.at(-1), matches: recordings.length, downtime };
}

export function calendarKey(value, period = 'day') {
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return null;
  date.setHours(12, 0, 0, 0);
  if (period === 'week') date.setDate(date.getDate() - (date.getDay() + 6) % 7);
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

export function periodTotals(matches, scope = 'owner', period = 'day') {
  const groups = new Map();
  for (const match of matches) {
    const key = calendarKey(match.matchDate, period);
    if (!key) continue;
    if (!groups.has(key)) groups.set(key, { key, count: 0, matches: 0, knownMatches: 0, partial: false,
      timedCount: 0, minutes: 0, rateMatches: 0, rate: null });
    const group = groups.get(key), count = recordedCount(match, scope), seconds = recordingSeconds(match);
    group.matches++;
    group.count += count.count;
    group.partial ||= count.partial || !count.available;
    if (count.available) {
      group.knownMatches++;
      if (seconds) { group.timedCount += count.count; group.minutes += seconds / 60; group.rateMatches++; }
    }
  }
  return [...groups.values()].sort((a, b) => a.key.localeCompare(b.key)).map(g =>
    ({ ...g, rate: g.minutes > 0 ? g.timedCount / g.minutes : null }));
}

export function sameComparisonGroup(a, b) {
  return id(a.ownerId) === id(b.ownerId) && id(a.playlist) === id(b.playlist);
}

// Descriptive farming notes use a fixed player/playlist cohort. The thresholds
// prevent a single lucky match or session from producing a recommendation;
// they are sample requirements, not a statistical confidence claim.
export const INSIGHT_MIN_MATCHES = 5;
export const INSIGHT_MIN_DAYS = 2;

function farmingSamples(matches, scope) {
  return matches.flatMap(match => {
    const count = recordedCount(match, scope), seconds = recordingSeconds(match);
    const players = (match.players ?? []).filter(p => inScope(p, scope));
    const date = calendarKey(match.matchDate);
    if (!date || !seconds || !count.available || players.some(p => !finiteCount(p.chests)) ||
      scope !== 'owner' && hasUnknownTeam(match) || /unreadable/i.test(match.coverage ?? '')) return [];
    return [{ match, count: count.count, minutes: seconds / 60, date }];
  });
}

function farmingGroups(samples, keyFor, labelFor, downtime) {
  const groups = new Map();
  for (const sample of samples) {
    const key = keyFor(sample.match);
    if (!groups.has(key)) groups.set(key, { key, label: labelFor(key), count: 0, minutes: 0, matches: 0, days: new Set() });
    const group = groups.get(key);
    group.count += sample.count; group.minutes += sample.minutes; group.matches++; group.days.add(sample.date);
  }
  return [...groups.values()].map(g => ({ ...g, days: g.days.size,
    rate: g.count / (g.minutes + downtime * g.matches),
    eligible: g.matches >= INSIGHT_MIN_MATCHES && g.days.size >= INSIGHT_MIN_DAYS }));
}

function rankFarmingGroups(groups) {
  const eligible = groups.filter(g => g.eligible).sort((a, b) => b.rate - a.rate || a.key.localeCompare(b.key));
  const [best, runnerUp] = eligible;
  return { groups, best, runnerUp, eligibleGroups: eligible.length,
    kind: !best ? 'insufficient' : best.count === 0 ? 'no-openings' : !runnerUp ? 'single'
      : best.rate <= runnerUp.rate * 1.1 ? 'close' : 'leader' };
}

export function timeOfDayTotals(matches, scope = 'owner', downtimeMinutes = 3) {
  const downtime = Math.min(60, Math.max(0, Number(downtimeMinutes) || 0));
  const label = key => `${String(key).padStart(2, '0')}:00–${String(Number(key) + 3).padStart(2, '0')}:00`;
  // Assign the whole recording to its local match-start block. Do not pretend
  // match metadata establishes an exact wall-clock origin for each replay event.
  return farmingGroups(farmingSamples(matches, scope), match => String(Math.floor(new Date(match.matchDate).getHours() / 3) * 3), label, downtime)
    .sort((a, b) => Number(a.key) - Number(b.key));
}

export function squadSurvivalWindow(match) {
  const seconds = recordingSeconds(match);
  if (!seconds || match.survivalMonitoringAvailable !== true || !Array.isArray(match.squadSurvivalTimeline)) return null;
  const ids = new Set((match.players ?? []).map(p => id(p.playerId)));
  const points = match.squadSurvivalTimeline.filter(p => p && ids.has(id(p.playerId)));
  if (!points.length || points.some(p => typeof p.isAlive !== 'boolean' || !Number.isFinite(p.replayTimeSeconds) || p.replayTimeSeconds < 0 || p.replayTimeSeconds > seconds)) return null;
  // Disconnects make a player unavailable; their stale pawn cannot keep the
  // survival window open after they have left. Elimination events at a shared
  // timestamp are applied before judging whether anyone remains alive.
  const departures = (Array.isArray(match.playerDisconnects) ? match.playerDisconnects : []).filter(d => d && ids.has(id(d.playerId)));
  if (departures.some(d => !Number.isFinite(d.replayTimeSeconds) || d.replayTimeSeconds < 0 || d.replayTimeSeconds > seconds)) return null;
  const timeline = [...points.map(p => ({ playerId: id(p.playerId), alive: p.isAlive, time: p.replayTimeSeconds })),
    ...departures.map(d => ({ playerId: id(d.playerId), alive: false, time: d.replayTimeSeconds }))]
    .sort((a, b) => a.time - b.time);
  const states = new Map(), intervals = [];
  let started = false, previous = 0, unknownSeconds = 0;
  for (let i = 0; i < timeline.length;) {
    const time = timeline[i].time;
    const alive = [...states.values()].some(Boolean);
    if (started && time > previous) {
      if (alive) intervals.push({ start: previous, end: time });
      else if (states.size < ids.size) unknownSeconds += time - previous;
    }
    while (i < timeline.length && timeline[i].time === time) {
      const point = timeline[i++]; states.set(point.playerId, point.alive);
    }
    if ([...states.values()].some(Boolean)) started = true;
    previous = time;
  }
  const aliveAtEnd = [...states.values()].some(Boolean);
  if (started && previous < seconds) {
    if (aliveAtEnd) intervals.push({ start: previous, end: seconds });
    else if (states.size < ids.size) unknownSeconds += seconds - previous;
  }
  const totalSeconds = intervals.reduce((sum, span) => sum + span.end - span.start, 0);
  if (!started || totalSeconds <= 0 || unknownSeconds > 0) return null;
  return { totalSeconds, intervals, timeline, censored: aliveAtEnd };
}

export function analyzeTeamDisconnects(match, survivalPercent = 100) {
  const window = squadSurvivalWindow(match);
  if (!window || match.disconnectMonitoringAvailable !== true || !Array.isArray(match.playerDisconnects)) return null;
  const cutoff = Math.min(100, Math.max(1, Number(survivalPercent) || 100));
  const teammates = new Set((match.players ?? []).filter(p => !p.isOwner).map(p => id(p.playerId)));
  const events = match.playerDisconnects.filter(d => d && teammates.has(id(d.playerId))).map(d => {
    const time = d.replayTimeSeconds, states = new Map();
    for (const point of window.timeline) { if (point.time > time) break; states.set(point.playerId, point.alive); }
    const otherAlive = [...states].some(([playerId, alive]) => playerId !== id(d.playerId) && alive);
    const elapsed = window.intervals.reduce((sum, span) => sum + Math.max(0, Math.min(time, span.end) - span.start), 0);
    const percent = 100 * elapsed / window.totalSeconds;
    return { ...d, otherAlive, survivalPercent: percent, premature: otherAlive && percent <= cutoff + 1e-9 };
  });
  return { window, events, cutoff, premature: events.some(d => d.premature) };
}

export function teammateRegionInsights(matches, { survivalPercent = 100, downtimeMinutes = 3 } = {}) {
  const cutoff = Math.min(100, Math.max(1, Number(survivalPercent) || 100));
  const downtime = Math.min(60, Math.max(0, Number(downtimeMinutes) || 0));
  const chestSamples = farmingSamples(matches, 'teammates').filter(s => (s.match.players ?? []).some(p => !p.isOwner));
  const contribution = rankFarmingGroups(farmingGroups(chestSamples.filter(s => regionKey(s.match) !== 'unknown'), regionKey, regionName, downtime));
  const groups = new Map();
  let unavailable = 0;
  for (const match of matches) {
    const region = regionKey(match), day = calendarKey(match.matchDate);
    const teammates = (match.players ?? []).filter(p => !p.isOwner);
    if (!day || region === 'unknown' || !teammates.length || teammates.some(p => !p.playerId || String(p.playerId).startsWith('unknown:')) || hasUnknownTeam(match) || /unreadable/i.test(match.coverage ?? '')) continue;
    if (!groups.has(region)) groups.set(region, { key: region, label: regionName(region), matches: 0, disconnectMatches: 0, days: new Set(), unavailable: 0 });
    const group = groups.get(region), assessment = analyzeTeamDisconnects(match, cutoff);
    if (!assessment) {
      group.unavailable++; unavailable++; continue;
    }
    group.matches++; group.days.add(day);
    if (assessment.premature) group.disconnectMatches++;
  }
  const stabilityGroups = [...groups.values()].map(g => ({ ...g, days: g.days.size,
    rate: g.matches ? g.disconnectMatches / g.matches : null,
    eligible: g.matches >= INSIGHT_MIN_MATCHES && g.days.size >= INSIGHT_MIN_DAYS }));
  const eligible = stabilityGroups.filter(g => g.eligible).sort((a, b) => a.rate - b.rate || a.key.localeCompare(b.key));
  const [best, runnerUp] = eligible;
  const stability = { groups: stabilityGroups, best, runnerUp, unavailable,
    kind: !best ? 'insufficient' : !runnerUp ? 'single' : runnerUp.rate - best.rate < .1 ? 'close' : 'leader' };
  return { contribution, stability, cutoff, downtime };
}

export function farmingInsights(matches, scope = 'owner', { downtimeMinutes = 3, windowMinutes = 2, region = '', survivalPercent = 100 } = {}) {
  const downtime = Math.min(60, Math.max(0, Number(downtimeMinutes) || 0));
  const samples = farmingSamples(matches, scope);
  const regions = rankFarmingGroups(farmingGroups(samples.filter(s => regionKey(s.match) !== 'unknown'), regionKey, regionName, downtime));
  regions.unknownMatches = samples.filter(s => regionKey(s.match) === 'unknown').length;
  regions.legacyMatches = samples.filter(s => !Object.hasOwn(s.match, 'matchmakingRegionCoverage')).length;
  const localSamples = samples.filter(s => !region || regionKey(s.match) === region);
  const time = rankFarmingGroups(timeOfDayTotals(localSamples.map(s => s.match), scope, downtime));
  const recordings = localSamples.map(s => timedRecording(s.match, scope)).filter(Boolean);
  const days = new Set(localSamples.filter(s => timedRecording(s.match, scope)).map(s => s.date)).size;
  const duration = { kind: 'insufficient', matches: recordings.length, days, excluded: localSamples.length - recordings.length,
    best: null, fullRate: null, improvement: null, downtime };
  if (recordings.length >= INSIGHT_MIN_MATCHES && days >= INSIGHT_MIN_DAYS) {
    const width = [1, 2, 5].includes(Number(windowMinutes)) ? Number(windowMinutes) : 2;
    const fullMinutes = recordings.reduce((sum, r) => sum + r.seconds / 60 + downtime, 0);
    duration.fullRate = recordings.reduce((sum, r) => sum + r.times.length, 0) / fullMinutes;
    const longest = Math.max(...recordings.map(r => r.seconds)) / 60;
    const candidates = [];
    for (let minute = width; minute < longest; minute += width) {
      if (minute < 2) continue;
      const reach = recordings.filter(r => r.seconds >= minute * 60).length;
      if (reach < INSIGHT_MIN_MATCHES || reach < recordings.length * .2) continue;
      const minutes = recordings.reduce((sum, r) => sum + Math.min(minute, r.seconds / 60) + downtime, 0);
      const count = recordings.reduce((sum, r) => sum + r.times.filter(t => t < minute * 60 || t === r.seconds && minute * 60 >= r.seconds).length, 0);
      candidates.push({ minute, rate: count / minutes, matches: reach });
    }
    const peak = Math.max(0, ...candidates.map(p => p.rate));
    // Prefer the earliest supported cutoff within 2% of the observed peak.
    const nearPeak = peak >= duration.fullRate * 1.05 ? Math.max(peak * .98, duration.fullRate * 1.05) : peak * .98;
    duration.best = candidates.find(p => peak > 0 && p.rate >= nearPeak) ?? null;
    duration.improvement = duration.fullRate > 0 && duration.best ? duration.best.rate / duration.fullRate - 1 : null;
    duration.kind = duration.fullRate === 0 ? 'no-openings' : !duration.best ? 'insufficient'
      : duration.improvement >= .05 ? 'restart' : 'continue';
  }
  return { scope, samples: samples.length, excluded: matches.length - samples.length, region: regions, time, duration,
    teammates: teammateRegionInsights(matches, { survivalPercent, downtimeMinutes: downtime }),
    selectedRegion: region, downtime, minMatches: INSIGHT_MIN_MATCHES, minDays: INSIGHT_MIN_DAYS };
}

export function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

// A responsive SVG with native hover titles, keyboard focus, and a companion data table.
export function barChart(rows, { label, metric = 'rate', comparison = [], xLabel = 'Replay minute', selectedKey = '' } = {}) {
  if (!rows.length) return '<p class="empty-state">No recorded data for this view.</p>';
  const width = Math.max(680, rows.length * 42 + 90), height = 260, left = 54, top = 20, plotHeight = 190;
  const step = (width - left - 20) / rows.length;
  const max = Math.max(1, ...rows.map(r => r[metric] ?? 0), ...comparison.map(r => r[metric] ?? 0));
  const y = value => top + plotHeight - (value ?? 0) / max * plotHeight;
  const fmt = value => value == null ? 'Unavailable' : Number(value).toLocaleString(undefined, { maximumFractionDigits: 2 });
  const title = r => `${r.label ?? r.key}: ${fmt(r[metric])} ${metric === 'rate' ? 'opened/min' : 'opened'}; ${r.matches} contributing matches`;
  const grid = [0, .5, 1].map(f => `<line x1="${left}" x2="${width - 20}" y1="${y(max * f)}" y2="${y(max * f)}" class="chart-grid"/><text x="${left - 8}" y="${y(max * f) + 4}" text-anchor="end">${fmt(max * f)}</text>`).join('');
  const bars = rows.map((r, i) => `<g tabindex="0" aria-label="${escapeHtml(title(r))}"><title>${escapeHtml(title(r))}</title><rect x="${left + i * step + step * .18}" y="${y(r[metric])}" width="${step * .64}" height="${r[metric] == null ? 0 : Math.max(1, top + plotHeight - y(r[metric]))}" class="chart-bar${r.key === selectedKey ? ' chart-picked' : ''}"/>${i % Math.max(1, Math.ceil(rows.length / 8)) === 0 ? `<text x="${left + (i + .5) * step}" y="${top + plotHeight + 20}" text-anchor="middle">${escapeHtml(r.label ?? r.key)}</text>` : ''}</g>`).join('');
  const line = comparison.map((r, i) => r[metric] == null ? '' : `${left + (i + .5) * step},${y(r[metric])}`).filter(Boolean).join(' ');
  const dots = comparison.map((r, i) => r[metric] == null ? '' : `<circle cx="${left + (i + .5) * step}" cy="${y(r[metric])}" r="3" fill="var(--warning)"><title>Comparison ${fmt(r[metric])} opened/min; ${r.matches} matches</title></circle>`).join('');
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${width} ${height}" style="min-width:${width}px" role="img" aria-label="${escapeHtml(label)}"><title>${escapeHtml(label)}</title>${grid}${bars}${line ? `<polyline points="${line}" class="chart-line"/>${dots}` : ''}<text x="${width / 2}" y="252" text-anchor="middle">${escapeHtml(xLabel)}</text></svg>`;
}
