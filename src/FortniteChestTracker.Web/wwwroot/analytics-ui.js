import { recordedCount, paceBins, cycleEstimate, calendarKey, periodTotals, sameComparisonGroup, regionKey, regionName, regionTotals, farmingInsights, barChart, escapeHtml } from './analytics.js';
import { startTreasureTimer } from './treasure-hour.js';

export function createAnalytics({ document, window, getMatches, getSelectedMatch }) {
  const el = id => document.getElementById(id);
  const fmt = value => value == null ? 'Unavailable' : Number(value).toLocaleString(undefined, { maximumFractionDigits: 2 });
  const key = match => JSON.stringify([match.matchId, match.ownerId]);
  const controls = ['analytics-scope', 'analytics-region', 'calendar-group', 'calendar-metric', 'analytics-period', 'pace-window', 'pace-downtime', 'pace-view', 'pace-average-group', 'insights-early-cutoff'];
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
    renderInsights(match, regionMatches, scope, chosenRegion);
  }
  function renderInsights(match, matches, scope, region) {
    const notes = farmingInsights(matches, scope, { region, earlyMinutes: el('insights-early-cutoff').value, downtimeMinutes: el('pace-downtime').value, windowMinutes: el('pace-window').value });
    const hourly = group => `${fmt(group.rate * 60)} chests/hour`;
    const evidence = group => `${group.matches} matches across ${group.days} days`;
    const threshold = `at least ${notes.minMatches} matches across ${notes.minDays} days in each comparison group`;
    const scopeName = { owner: 'owner only', teammates: 'teammates only', squad: 'owner + teammates' }[scope];
    el('insights-summary').textContent = match ? `${match.ownerName} · ${match.mode} · ${scopeName}. ${notes.samples} usable matches (${notes.excluded} excluded). Region notes compare all reported regions; time and restart notes use ${region ? regionName(region) : 'all regions'}. Teammate notes always count teammates.` : 'Choose a match to use its player and game mode for farming notes.';
    el('insights-method').textContent = `Rates include your ${fmt(notes.downtime)} min queue/loading estimate per match; actual waits can differ by region. Notes need ${threshold}; 5–19 supporting matches show “Early finding”. These are recorded minimums and observed patterns, not guaranteed results. Time slots use local match start times (${Intl.DateTimeFormat().resolvedOptions().timeZone}). Restart minutes include recorded lobby/drop time.`;
    function comparison(prefix, result, noun) {
      const { best, runnerUp, kind } = result;
      el(`${prefix}-badge`).textContent = !best || kind === 'single' ? 'More data needed' : best.matches < 20 || runnerUp && runnerUp.matches < 20 ? 'Early finding' : 'Recorded pattern';
      el(`${prefix}-title`).textContent = kind === 'leader' ? best.label : kind === 'close' ? 'No strong advantage' : kind === 'no-openings' ? 'No openings recorded' : 'More comparisons needed';
      el(`${prefix}-note`).textContent = kind === 'leader' ? `${best.label} has the highest observed rate: ${hourly(best)}, versus ${hourly(runnerUp)} for ${runnerUp.label}. Based on ${evidence(best)} and ${evidence(runnerUp)}. Try it and compare new matches.`
        : kind === 'close' ? `${best.label} (${hourly(best)}) and ${runnerUp.label} (${hourly(runnerUp)}) are within 10% of each other. There is little observed difference to act on.`
        : kind === 'single' ? `Only ${best.label} has enough data (${evidence(best)}, ${hourly(best)}). Sample another ${noun} before choosing a winner.`
        : kind === 'no-openings' ? 'These adequately sampled matches have no recorded chest openings, so there is no farming rate to recommend.'
        : `Collect ${threshold} for at least two ${noun === 'region' ? 'regions' : 'time slots'} using this player and game mode.`;
    }
    comparison('insight-region', notes.region, 'region');
    comparison('insight-time', notes.time, 'time slot');
    comparison('insight-team-chests', notes.teammates.contribution, 'region');
    const stability = notes.teammates.stability;
    const percent = group => `${fmt(group.rate * 100)}%`;
    el('insight-team-stability-badge').textContent = !stability.best || stability.kind === 'single' ? 'More data needed' : Math.min(stability.best.matches, stability.runnerUp.matches) < 20 ? 'Early finding' : 'Recorded pattern';
    el('insight-team-stability-title').textContent = stability.kind === 'leader' ? stability.best.label : stability.kind === 'close' ? 'Similar recorded disconnect rates' : 'More tracking needed';
    el('insight-team-stability-note').textContent = stability.kind === 'leader' ? `${stability.best.label} has fewer recorded teammate disconnects before replay minute ${fmt(notes.teammates.cutoff)}: ${percent(stability.best)} of tracked matches, versus ${percent(stability.runnerUp)} for ${stability.runnerUp.label}. Based on ${evidence(stability.best)} and ${evidence(stability.runnerUp)}.`
      : stability.kind === 'close' ? `${stability.best.label} (${percent(stability.best)}) and ${stability.runnerUp.label} (${percent(stability.runnerUp)}) are within 10 percentage points. There is little observed difference in early disconnects.`
      : stability.kind === 'single' ? `Only ${stability.best.label} has enough tracked matches. Rescan available replays and sample another region before comparing early disconnects.`
      : `Need ${threshold} in two regions with disconnect tracking and recordings at least ${fmt(notes.teammates.cutoff + 1)} minutes long. Rescan available replays to add the new tracking data.`;
    const teammateRows = new Map();
    for (const row of notes.teammates.contribution.groups) teammateRows.set(row.key, { ...row, chestMatches: row.matches });
    for (const row of stability.groups) teammateRows.set(row.key, { ...teammateRows.get(row.key), key: row.key, label: row.label, status: row });
    el('insights-team-table').innerHTML = [...teammateRows.values()].map(g => `<tr><td>${escapeHtml(g.label)}</td><td class="number">${g.chestMatches ? fmt(g.count / g.chestMatches) : 'Unavailable'}</td><td class="number">${g.chestMatches ? fmt(g.rate * 60) : 'Unavailable'}</td><td class="number">${g.chestMatches ?? 0}</td><td class="number">${g.status?.matches ? percent(g.status) : 'Unavailable'}</td><td class="number">${g.status?.matches ?? 0}</td><td class="number">${g.status?.unavailable ?? 0}</td></tr>`).join('');
    el('insights-team-method').textContent = `Teammate counts are recorded minimums. “Early” means a recorded disconnect before replay minute ${fmt(notes.teammates.cutoff)}, with at least one further minute recorded after that cutoff. ${stability.unavailable} matches lacked usable tracking, timing, or sufficient length. A disconnect may follow elimination or network loss; no recorded event does not prove a teammate stayed. These rates describe detected events, not the true probability of quitting.`;
    el('insight-region-missing').textContent = notes.region.unknownMatches ? `${notes.region.unknownMatches} matches have no usable region and are excluded from region rankings.${notes.region.legacyMatches ? ` ${notes.region.legacyMatches} older saved records need a rescan to add regions.` : ''}` : '';
    const duration = notes.duration;
    el('insight-stop-badge').textContent = duration.kind === 'insufficient' ? 'More data needed' : Math.min(duration.matches, duration.best?.matches ?? duration.matches) < 20 ? 'Early finding' : 'Recorded pattern';
    el('insight-stop-title').textContent = duration.kind === 'restart' ? `Try replay minute ${fmt(duration.best.minute)}` : duration.kind === 'continue' ? 'Full matches look competitive' : duration.kind === 'no-openings' ? 'No openings recorded' : 'More opening times needed';
    el('insight-stop-note').textContent = duration.kind === 'restart' ? `Restarting around minute ${fmt(duration.best.minute)} gives an estimated ${hourly(duration.best)}, compared with ${fmt(duration.fullRate * 60)} chests/hour for full recordings (${fmt(duration.improvement * 100)}% higher). ${duration.matches} matches used; ${duration.best.matches} reached that minute. Test this cutoff with your actual queue time.`
      : duration.kind === 'continue' ? `Full recordings give an estimated ${fmt(duration.fullRate * 60)} chests/hour. No supported early cutoff improves that by 5% in these ${duration.matches} matches. Staying longer looks reasonable in this sample.`
      : duration.kind === 'no-openings' ? 'No openings were recorded in these matches, so a useful restart time cannot be estimated.'
      : `${duration.matches} matches have usable opening times across ${duration.days} days. Need ${notes.minMatches}+ matches on ${notes.minDays}+ days that reach the proposed cutoff. Rescan available replays to include older history.`;
    el('insights-time-table').innerHTML = notes.time.groups.map(g => `<tr><td>${escapeHtml(g.label)}</td><td class="number">${fmt(g.rate * 60)}</td><td class="number">${g.count}</td><td class="number">${g.matches}</td><td class="number">${g.days}</td><td>${g.eligible ? 'Enough to compare' : 'More matches/days needed'}</td></tr>`).join('');
    el('insights-time-empty').hidden = notes.time.groups.length > 0;
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
    if (id === 'insights-early-cutoff') el(id).value = String(Math.min(30, Math.max(2, Number(el(id).value) || 5)));
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
    if (data && Number.isFinite(Number(data['insights-early-cutoff']))) el('insights-early-cutoff').value = String(Math.min(30, Math.max(2, Number(data['insights-early-cutoff']))));
  } catch { /* Optional preferences. */ }
  let lastDay = calendarKey(Date.now());
  const timer = window.setInterval?.(() => {
    const day = calendarKey(Date.now());
    if (day !== lastDay) { lastDay = day; render(); }
  }, 30000);
  window.addEventListener('beforeunload', () => window.clearInterval?.(timer));
  return { render };
}
