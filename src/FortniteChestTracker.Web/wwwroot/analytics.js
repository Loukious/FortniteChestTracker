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
