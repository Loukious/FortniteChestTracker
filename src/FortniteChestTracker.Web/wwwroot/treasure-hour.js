// Explicit dates only. Expected sessions must remain visibly unconfirmed.
export const TREASURE_SOURCE = 'https://chestcounterfortnite.com/treasure-hours';
export const TREASURE_WINDOWS = [
  { start: '2026-11-21T19:00:00Z', end: '2026-11-21T21:00:00Z', confirmed: true },
  { start: '2026-11-22T02:00:00Z', end: '2026-11-22T04:00:00Z', confirmed: true },
  { start: '2026-11-28T19:00:00Z', end: '2026-11-28T21:00:00Z', confirmed: false },
  { start: '2026-11-29T02:00:00Z', end: '2026-11-29T04:00:00Z', confirmed: false }
];

export function treasureStatus(now = Date.now(), windows = TREASURE_WINDOWS) {
  const sessions = windows.map(w => ({ ...w, startMs: Date.parse(w.start), endMs: Date.parse(w.end) }))
    .filter(w => Number.isFinite(w.startMs) && w.endMs > w.startMs).sort((a, b) => a.startMs - b.startMs);
  const active = sessions.find(w => w.startMs <= now && now < w.endMs);
  const next = active ?? sessions.find(w => w.startMs > now);
  return next ? { ...next, active: Boolean(active), remainingMs: (active ? next.endMs : next.startMs) - now } : null;
}

export function countdown(milliseconds) {
  const seconds = Math.max(0, Math.ceil(milliseconds / 1000));
  const days = Math.floor(seconds / 86400), hours = Math.floor(seconds / 3600) % 24;
  return `${days ? `${days}d ` : ''}${String(hours).padStart(2, '0')}:${String(Math.floor(seconds / 60) % 60).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`;
}

export function startTreasureTimer(document, window) {
  const el = id => document.getElementById(id);
  const local = new Intl.DateTimeFormat(undefined, { weekday: 'short', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', timeZoneName: 'short' });
  const utc = new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', timeZone: 'UTC', timeZoneName: 'short', hour12: false });
  function update() {
    const session = treasureStatus();
    el('treasure-state').textContent = session ? (session.confirmed ? 'Confirmed' : 'Expected · unconfirmed') + (session.active ? ' window active' : ' next window') : 'Schedule unavailable';
    el('treasure-countdown').textContent = session ? `${session.active ? 'Ends in' : 'Starts in'} ${countdown(session.remainingMs)}` : 'No upcoming window listed';
    el('treasure-time').textContent = session ? `${local.format(session.startMs)} – ${local.format(session.endMs)} · ${utc.format(session.startMs)} – ${utc.format(session.endMs)}` : 'Check the schedule source for new announcements.';
  }
  update();
  const timer = window.setInterval?.(update, 1000);
  window.addEventListener('beforeunload', () => window.clearInterval?.(timer));
  window.addEventListener('pageshow', update);
  document.addEventListener?.('visibilitychange', update);
}
