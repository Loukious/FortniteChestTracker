import { recordedCount, paceBins, cycleEstimate, calendarKey, periodTotals, sameComparisonGroup, regionKey, regionName, regionTotals, barChart, escapeHtml } from './analytics.js';
import { startTreasureTimer } from './treasure-hour.js';

export function createAnalytics({ document, window, getMatches, getSelectedMatch }) {
  const el = id => document.getElementById(id);
  const fmt = value => value == null ? 'Unavailable' : Number(value).toLocaleString(undefined, { maximumFractionDigits: 2 });
  const key = match => JSON.stringify([match.matchId, match.ownerId]);
  const controls = ['analytics-scope', 'analytics-region', 'calendar-group', 'calendar-metric', 'analytics-period', 'pace-window', 'pace-downtime', 'pace-view', 'pace-average-group'];
  let chosenMatch = '', chosenRegion = '', selectedTableMatch = null;
  startTreasureTimer(document, window);
  function options(id, values, value) {
    const select = el(id);
    select.innerHTML = values.map(v => `<option value="${escapeHtml(v.value)}">${escapeHtml(v.label)}</option>`).join('');
    select.value = values.some(v => v.value === value) ? value : values[0]?.value ?? '';
  }
  function render() {
    const rawMatches = getMatches(), scope = el('analytics-scope').value || 'owner';
    el('pace-scope').value = scope;
    options('analytics-region', [{ value: '', label: 'All reported regions' }, ...[...new Set(rawMatches.map(regionKey))].sort()
      .map(value => ({ value, label: regionName(value) }))], chosenRegion);
    chosenRegion = el('analytics-region').value;
    const matches = rawMatches.filter(m => !chosenRegion || regionKey(m) === chosenRegion);
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
    const regionMatches = match ? rawMatches.filter(m => (!period || calendarKey(m.matchDate, group) === period) && sameComparisonGroup(m, match)) : [];
    const regions = regionTotals(regionMatches, scope);
    el('region-chart').innerHTML = barChart(regions, { label: `${scope} recorded openings per minute by reported region`, xLabel: 'Reported matchmaking region' });
    el('region-table').innerHTML = regions.map(r => `<tr><td>${escapeHtml(r.label)}</td><td class="number">${r.knownMatches ? fmt(r.count) + (r.partial ? '+' : '') : 'Unknown'}</td><td class="number">${fmt(r.rate)}</td><td class="number">${r.matches}</td><td class="number">${fmt(r.minutes)}</td></tr>`).join('');
    el('region-summary').textContent = match ? `Comparing ${scope} chest counts for ${match.ownerName} · ${match.mode} across all reported regions in the chosen date period (${regionMatches.length} matches).` : 'Choose a match to compare the same player and game mode across regions.';
  }
  function renderPace(match, matches, scope) {
    const width = Number(el('pace-window').value) || 2, downtime = Number(el('pace-downtime').value);
    const allMatches = el('pace-average-group').value === 'all', averageOnly = el('pace-view').value === 'average';
    const comparisons = allMatches ? matches : match ? matches.filter(m => sameComparisonGroup(m, match)) : [];
    const selected = paceBins(match ? [match] : [], scope, width), average = paceBins(comparisons, scope, width);
    const source = averageOnly ? average : selected;
    const bins = Array.from({ length: averageOnly ? average.bins.length : Math.max(selected.bins.length, average.bins.length) }, (_, i) => ({
      ...(source.bins[i] ?? { rate: null, count: null, minutes: 0, matches: 0 }), label: `${i * width}–${(i + 1) * width}`
    }));
    const scopeLabel = { owner: "Replay owner's chests only (teammates excluded)", teammates: 'Teammates only (owner excluded)', squad: 'Whole squad (owner + teammates)' }[scope];
    const averageLabel = allMatches ? 'Average of all filtered matches' : 'Average for same player and game mode';
    el('pace-chart').innerHTML = barChart(bins, { label: `${scopeLabel}: ${averageOnly ? averageLabel : 'this match'} chests per minute`, xLabel: 'Minutes since replay started', comparison: averageOnly ? [] : average.bins });
    const headings = averageOnly ? ['Minute in recording', 'Average chests per minute', 'Chests opened across matches', 'Total minutes recorded', 'Matches used in average']
      : ['Minute in recording', 'This match: chests opened', 'This match: chests per minute', 'This match: minutes recorded', 'Average chests per minute', 'Matches used in average'];
    el('pace-table-head').innerHTML = `<tr>${headings.map((h, i) => `<th${i ? ' class="number"' : ''}>${h}</th>`).join('')}</tr>`;
    el('pace-table').innerHTML = bins.map((bin, i) => `<tr><td>${bin.label} min</td>${(averageOnly ?
      [fmt(bin.rate), fmt(bin.count), fmt(bin.minutes), bin.matches] :
      [fmt(bin.count), fmt(bin.rate), fmt(bin.minutes), fmt(average.bins[i]?.rate), average.bins[i]?.matches ?? 0])
      .map(value => `<td class="number">${value}</td>`).join('')}</tr>`).join('');
    el('pace-scope-summary').textContent = `Counting: ${scopeLabel}.`;
    el('pace-bars-label').textContent = averageOnly ? averageLabel : 'This match';
    el('pace-average-label').textContent = averageLabel;
    el('pace-average-legend').hidden = averageOnly;
    el('pace-intro').textContent = `${averageOnly ? 'Bars show the average rate across matches.' : 'Bars show this match; the line shows the average rate across matches.'} Minute 0 is the start of the replay, including recorded lobby/drop time. Average rate = chests opened ÷ recorded minutes in each time window; matches stop contributing when their recording ends.`;
    el('pace-summary').textContent = `${match ? `${match.ownerName} · ${match.mode} · ${regionName(regionKey(match))}. ` : ''}${averageLabel}: ${average.included} matches used; ${average.excluded} have no usable opening times. ${!averageOnly && !selected.included ? 'This match has no opening times.' : ''}`;
    el('pace-missing').hidden = (averageOnly || selected.included === 1) && average.excluded === 0;
    el('pace-missing').textContent = 'Rescan available replays to include older saved matches in this graph. Matches with missing opening times or recording length keep their saved totals but are left out of the average.';
    const cycle = cycleEstimate(comparisons, scope, width, downtime);
    el('pace-estimate').textContent = cycle && cycle.best.count > 0 ? `Highest estimated rate at minute ${fmt(cycle.best.minute)} → ${fmt(cycle.best.rate)} chests/min, including ${fmt(cycle.downtime)} min downtime per replay. Playing full recordings: ${fmt(cycle.full.rate)} chests/min (${cycle.matches} matches).` : 'The restart estimate needs matches with recorded opening times.';
  }
  for (const id of controls) el(id).addEventListener('change', () => {
    if (id === 'analytics-region') chosenRegion = el(id).value;
    if (id === 'calendar-group') el('analytics-period').value = '';
    if (id === 'pace-downtime') el(id).value = String(Math.min(60, Math.max(0, Number(el(id).value) || 0)));
    savePreferences(); render();
  });
  el('pace-scope').addEventListener('change', () => { el('analytics-scope').value = el('pace-scope').value; savePreferences(); render(); });
  el('pace-match').addEventListener('change', () => { chosenMatch = el('pace-match').value; render(); });
  function savePreferences() {
    try { window.localStorage?.setItem('fortnite-chest-tracker-analytics-v1', JSON.stringify(Object.fromEntries(
      controls.filter(id => id !== 'analytics-period').map(id => [id, el(id).value])))); } catch { /* Optional preferences. */ }
  }
  try {
    const data = JSON.parse(window.localStorage?.getItem('fortnite-chest-tracker-analytics-v1') || 'null');
    if (typeof data?.['analytics-region'] === 'string') chosenRegion = data['analytics-region'];
    for (const [id, values] of Object.entries({ 'analytics-scope': ['owner', 'teammates', 'squad'], 'calendar-group': ['day', 'week'],
      'calendar-metric': ['count', 'rate'], 'pace-window': ['1', '2', '5'], 'pace-view': ['match', 'average'], 'pace-average-group': ['same', 'all'] })) if (values.includes(data?.[id])) el(id).value = data[id];
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
