import { recordedCount, paceBins, cycleEstimate, calendarKey, periodTotals, sameComparisonGroup, barChart, escapeHtml } from './analytics.js';
import { startTreasureTimer } from './treasure-hour.js';

export function createAnalytics({ document, window, getMatches, getSelectedMatch }) {
  const el = id => document.getElementById(id);
  const fmt = value => value == null ? 'Unavailable' : Number(value).toLocaleString(undefined, { maximumFractionDigits: 2 });
  const key = match => JSON.stringify([match.matchId, match.ownerId]);
  const controls = ['analytics-scope', 'calendar-group', 'calendar-metric', 'analytics-period', 'pace-window', 'pace-downtime'];
  let chosenMatch = '', selectedTableMatch = null;
  startTreasureTimer(document, window);
  function options(id, values, value) {
    const select = el(id);
    select.innerHTML = values.map(v => `<option value="${escapeHtml(v.value)}">${escapeHtml(v.label)}</option>`).join('');
    select.value = values.some(v => v.value === value) ? value : values[0]?.value ?? '';
  }
  function render() {
    const matches = getMatches(), scope = el('analytics-scope').value || 'owner';
    const group = el('calendar-group').value || 'day', metric = el('calendar-metric').value || 'count';
    const groups = periodTotals(matches, scope, group);
    const selectedPeriod = el('analytics-period').value;
    options('analytics-period', [{ value: '', label: 'All included dates' }, ...groups.slice().reverse().map(g =>
      ({ value: g.key, label: `${group === 'week' ? 'Week of ' : ''}${g.key}` }))], selectedPeriod);
    const period = el('analytics-period').value;
    const periodMatches = matches.filter(m => !period || calendarKey(m.matchDate, group) === period);
    const selected = getSelectedMatch();
    if (selected !== selectedTableMatch) { selectedTableMatch = selected; chosenMatch = selected ? key(selected) : ''; }
    options('pace-match', periodMatches.map(m => ({ value: key(m),
      label: `${new Date(m.matchDate).toLocaleString()} · ${m.ownerName} · ${m.mode}` })), chosenMatch);
    chosenMatch = el('pace-match').value;
    const match = periodMatches.find(m => key(m) === chosenMatch);
    const today = periodTotals(matches, scope, 'day').find(g => g.key === calendarKey(Date.now()));
    const week = periodTotals(matches, scope, 'week').find(g => g.key === calendarKey(Date.now(), 'week'));
    const totalLabel = g => !g ? '0' : g.knownMatches === 0 ? 'Unknown' : fmt(g.count) + (g.partial ? '+' : '');
    el('daily-total').textContent = totalLabel(today);
    el('weekly-total').textContent = totalLabel(week);
    el('daily-rate').textContent = today ? `${fmt(today.rate)} opened/min · ${today.matches} matches` : 'No included matches today';
    el('weekly-rate').textContent = week ? `${fmt(week.rate)} opened/min · ${week.matches} matches` : 'No included matches this week';
    const periodCounts = periodMatches.map(m => recordedCount(m, scope));
    el('period-total').textContent = periodCounts.length && !periodCounts.some(c => c.available) ? 'Unknown' :
      fmt(periodCounts.reduce((n, c) => n + c.count, 0)) + (periodCounts.some(c => c.partial || !c.available) ? '+' : '');
    el('period-label').textContent = `${period ? (group === 'week' ? 'Week of ' : '') + period : 'All included dates'} · ${periodMatches.length} matches · recorded minimum`;
    el('calendar-chart').innerHTML = barChart(groups.map(g => ({ ...g, count: g.knownMatches ? g.count : null })),
      { label: `${group === 'week' ? 'Weekly' : 'Daily'} ${scope} ${metric === 'rate' ? 'recorded chest rate' : 'recorded openings'}`,
        metric, xLabel: group === 'week' ? 'Local week beginning Monday' : 'Local match date', selectedKey: period });
    el('calendar-table').innerHTML = groups.map(g => `<tr><td>${escapeHtml(g.key)}</td><td class="number">${totalLabel(g)}</td><td class="number">${fmt(g.rate)}</td><td class="number">${g.matches}</td><td class="number">${fmt(g.minutes)}</td><td>${g.rateMatches} of ${g.matches} matches have counts and duration</td></tr>`).join('');
    el('calendar-summary').textContent = `Showing ${scope} counts from the included matches. Weeks start Monday in your browser's local timezone. Rates divide recorded openings by recorded minutes, including lobby/drop time; queue/loading time is excluded. Dates with no saved matches are omitted.`;
    renderPace(match, periodMatches, scope);
  }
  function renderPace(match, matches, scope) {
    const width = Number(el('pace-window').value) || 2, downtime = Number(el('pace-downtime').value);
    const comparisons = match ? matches.filter(m => sameComparisonGroup(m, match)) : [];
    const selected = paceBins(match ? [match] : [], scope, width), average = paceBins(comparisons, scope, width);
    const bins = Array.from({ length: Math.max(selected.bins.length, average.bins.length) }, (_, i) => ({
      ...(selected.bins[i] ?? { rate: null, count: null, minutes: 0, matches: 0 }), label: `${i * width}–${(i + 1) * width}`
    }));
    el('pace-chart').innerHTML = barChart(bins, { label: `${scope} recorded openings per replay minute`, comparison: average.bins });
    el('pace-table').innerHTML = bins.map((bin, i) => `<tr><td>${bin.label} min</td><td class="number">${fmt(bin.count)}</td><td class="number">${fmt(bin.rate)}</td><td class="number">${fmt(bin.minutes)}</td><td class="number">${fmt(average.bins[i]?.rate)}</td><td class="number">${average.bins[i]?.matches ?? 0}</td></tr>`).join('');
    el('pace-summary').textContent = match ? `${match.ownerName} · ${match.playlist} · ${selected.included ? 'selected match timing available' : 'selected match timing unavailable'} · comparison uses ${average.included} timed matches for this owner and playlist (${average.excluded} excluded).` : 'No match in the selected date period.';
    el('pace-missing').hidden = selected.included === 1 && average.excluded === 0;
    el('pace-missing').textContent = 'Rescan available replays to add opening times to older history. Recordings with missing or invalid opening timestamps or duration are excluded from pace charts. Saved totals are retained.';
    const cycle = cycleEstimate(comparisons, scope, width, downtime);
    el('pace-estimate').textContent = cycle && cycle.best.count > 0 ? `Best observed cutoff: ${fmt(cycle.best.minute)} replay minutes · ${fmt(cycle.best.rate)} opened/min including ${fmt(cycle.downtime)} min downtime per replay. Full recordings: ${fmt(cycle.full.rate)} opened/min across ${cycle.matches} matches.` : 'A restart estimate needs timed matches with recorded openings.';
  }
  for (const id of controls) el(id).addEventListener('change', () => {
    if (id === 'calendar-group') el('analytics-period').value = '';
    if (id === 'pace-downtime') el(id).value = String(Math.min(60, Math.max(0, Number(el(id).value) || 0)));
    savePreferences(); render();
  });
  el('pace-match').addEventListener('change', () => { chosenMatch = el('pace-match').value; render(); });
  function savePreferences() {
    try { window.localStorage?.setItem('fortnite-chest-tracker-analytics-v1', JSON.stringify(Object.fromEntries(
      controls.filter(id => id !== 'analytics-period').map(id => [id, el(id).value])))); } catch { /* Optional preferences. */ }
  }
  try {
    const data = JSON.parse(window.localStorage?.getItem('fortnite-chest-tracker-analytics-v1') || 'null');
    for (const [id, values] of Object.entries({ 'analytics-scope': ['owner', 'teammates', 'squad'], 'calendar-group': ['day', 'week'],
      'calendar-metric': ['count', 'rate'], 'pace-window': ['1', '2', '5'] })) if (values.includes(data?.[id])) el(id).value = data[id];
    if (data && Number.isFinite(Number(data['pace-downtime']))) el('pace-downtime').value = String(Math.min(60, Math.max(0, Number(data['pace-downtime']))));
  } catch { /* Optional preferences. */ }
  let lastDay = calendarKey(Date.now());
  const timer = window.setInterval?.(() => {
    const day = calendarKey(Date.now());
    if (day !== lastDay) { lastDay = day; render(); }
  }, 30000);
  window.addEventListener('beforeunload', () => window.clearInterval?.(timer));
  return { render };
}
