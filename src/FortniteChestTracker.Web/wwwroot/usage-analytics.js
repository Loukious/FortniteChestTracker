import { USAGE_ANALYTICS } from './usage-config.js';

const CONSENT_KEY = 'fortnite-chest-tracker-usage-consent-v1';
const CONSENT_DAYS = 90;
const EVENTS = new Set(['page_view', 'scan_started', 'csv_exported', 'history_exported', 'history_import_opened', 'folder_picker_opened', 'files_picker_opened']);
const BUTTON_EVENTS = { scan: 'scan_started', export: 'csv_exported', 'export-history': 'history_exported',
  'import-history': 'history_import_opened', 'choose-folder': 'folder_picker_opened', 'choose-files': 'files_picker_opened' };

export function createUsageAnalytics({ window, document, config = USAGE_ANALYTICS }) {
  const el = id => document.getElementById(id);
  const valid = /^G-[A-Z0-9]{6,20}$/.test(config?.measurementId) && config?.origin === USAGE_ANALYTICS.origin && config?.path === USAGE_ANALYTICS.path && window.location?.origin === config.origin &&
    window.location?.protocol === 'https:' && window.location?.pathname.startsWith(config.path);
  let consent = null, frame = null, channel = null, ready = false, queue = [], pageViewed = false, disableTimer = null, unavailable = false;
  const blocked = () => window.navigator?.globalPrivacyControl === true || window.navigator?.doNotTrack === '1' || window.doNotTrack === '1';
  const canTrack = () => valid && !blocked() && !unavailable && consent?.choice === 'granted' && consent.expires > Date.now();
  try {
    const saved = JSON.parse(window.localStorage?.getItem(CONSENT_KEY) || 'null');
    if (saved && ['granted', 'denied'].includes(saved.choice) && saved.expires > Date.now() &&
      (saved.choice !== 'granted' || /^[a-f\d-]{36}$/i.test(saved.clientId))) consent = saved;
  } catch { /* Storage is optional; a failed read never grants consent. */ }

  function status() {
    el('usage-status').textContent = !valid ? 'Usage analytics are disabled on this address.' : blocked() ? 'Usage analytics are off because your browser requests privacy.' :
      unavailable ? 'Usage analytics could not load. Your replay report still works.' : consent?.choice === 'granted' && consent.expires > Date.now() ? 'Usage analytics allowed. You can turn them off here.' : 'Usage analytics are off.';
    el('usage-allow').disabled = !valid || blocked();
  }
  function save(choice) {
    consent = { choice, expires: Date.now() + CONSENT_DAYS * 86400000,
      clientId: choice === 'granted' ? consent?.clientId || window.crypto.randomUUID() : null };
    try { window.localStorage?.setItem(CONSENT_KEY, JSON.stringify(consent)); } catch { /* Page-only consent if storage is blocked. */ }
    status();
  }
  function send(message) { frame?.contentWindow?.postMessage({ ...message, channel }, '*'); }
  function removeFrame() {
    window.clearTimeout(disableTimer); disableTimer = null; frame?.remove(); frame = null; ready = false; channel = null; queue = [];
  }
  function start() {
    if (!canTrack() || frame) return;
    channel = window.crypto.randomUUID();
    frame = document.createElement('iframe');
    frame.hidden = true; frame.title = 'Optional usage analytics';
    frame.setAttribute('sandbox', 'allow-scripts'); // No same-origin access, storage, navigation, or popups.
    frame.setAttribute('referrerpolicy', 'no-referrer');
    frame.setAttribute('allow', "geolocation 'none'; camera 'none'; microphone 'none'; clipboard-read 'none'; clipboard-write 'none'");
    frame.src = config.origin + config.path + 'usage-analytics-frame.html';
    frame.addEventListener('load', () => {
      if (!canTrack()) { removeFrame(); return; }
      send({ type: 'configure', measurementId: config.measurementId, clientId: consent.clientId,
        pageLocation: config.origin + config.path });
    });
    frame.addEventListener('error', failed);
    document.body.append(frame);
    disableTimer = window.setTimeout(() => { if (!ready && canTrack()) failed(); }, 15000);
  }
  function failed() { unavailable = true; removeFrame(); status(); }
  function track(name) {
    if (!canTrack() || !EVENTS.has(name)) return;
    if (ready) send({ type: 'event', name });
    else { if (queue.length < 20) queue.push(name); start(); }
  }
  function allow() {
    if (!valid || blocked()) return;
    if (disableTimer !== null) { removeFrame(); pageViewed = false; }
    unavailable = false;
    save('granted'); el('usage-privacy').hidden = true;
    start(); if (!pageViewed) { pageViewed = true; track('page_view'); }
  }
  function stopTracking() {
    window.clearTimeout(disableTimer); disableTimer = null;
    if (frame) { send({ type: 'disable' }); disableTimer = window.setTimeout(removeFrame, 100); }
    queue = []; pageViewed = false;
  }
  function decline() {
    save('denied'); el('usage-privacy').hidden = true; stopTracking();
  }
  window.addEventListener('message', event => {
    if (!frame || event.source !== frame.contentWindow || event.origin !== 'null' || event.data?.channel !== channel) return;
    if (event.data.type === 'disabled') { removeFrame(); return; }
    if (event.data.type === 'failed') { failed(); return; }
    if (event.data.type !== 'ready' || !canTrack()) return;
    window.clearTimeout(disableTimer); disableTimer = null;
    ready = true; const pending = queue; queue = [];
    for (const name of pending) track(name);
  });
  el('usage-allow').addEventListener('click', allow);
  el('usage-decline').addEventListener('click', decline);
  el('usage-preferences').addEventListener('click', () => { status(); el('usage-privacy').hidden = false; el('usage-decline').focus(); });
  window.addEventListener('storage', event => {
    if (event.key !== CONSENT_KEY) return;
    try { const saved = JSON.parse(event.newValue || 'null'); if (saved?.choice === 'granted') return; consent = saved?.choice === 'denied' ? saved : null; }
    catch { consent = null; }
    stopTracking(); status(); // Do not write back and cause a cross-tab storage loop.
  });
  status();
  el('usage-privacy').hidden = !valid || blocked() || consent !== null;
  if (canTrack()) { start(); pageViewed = true; track('page_view'); }
  return { track, allow, decline };
}

if (typeof window !== 'undefined' && typeof document !== 'undefined') {
  const usage = createUsageAnalytics({ window, document });
  document.addEventListener('click', event => {
    const button = event.target.closest?.('button');
    if (button && !button.disabled && BUTTON_EVENTS[button.id]) usage.track(BUTTON_EVENTS[button.id]);
  }, true); // Observe enabled buttons before their handlers begin an operation.
}
