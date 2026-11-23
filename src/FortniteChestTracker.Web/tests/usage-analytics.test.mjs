import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import vm from 'node:vm';

const config = { measurementId: 'G-ZM2VXKTLBC', origin: 'https://loukious.github.io', path: '/FortniteChestTracker/' };
const key = 'fortnite-chest-tracker-usage-consent-v1';
const consent = (choice = 'granted', expires = Date.now() + 86400000) => ({ choice, expires, clientId: choice === 'granted' ? randomUUID() : null });
const source = readFileSync(new URL('../wwwroot/usage-analytics.js', import.meta.url), 'utf8').replace(/^import .*;\r?\n/, '').replace('export function ', 'function ');

function setup({ saved, navigator = {}, location = {}, storageBlocked = false, override = config } = {}) {
  const elements = new Map(), listeners = new Map(), frames = [], timers = new Map(), storage = new Map(), writes = [];
  if (saved !== undefined) storage.set(key, typeof saved === 'string' ? saved : JSON.stringify(saved));
  function element() {
    return { hidden: true, attributes: {}, events: {}, setAttribute(k, v) { this.attributes[k] = v; },
      addEventListener(k, fn) { this.events[k] = fn; }, remove() { this.removed = true; }, focus() {} };
  }
  const document = { getElementById(id) { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); },
    createElement() { const f = element(); f.sent = []; f.contentWindow = { postMessage: (data, origin) => f.sent.push({ data, origin }) }; frames.push(f); return f; },
    body: { append() {} } };
  const window = { location: { origin: config.origin, protocol: 'https:', pathname: config.path, ...location }, navigator,
    crypto: { randomUUID }, addEventListener: (name, fn) => listeners.set(name, fn),
    setTimeout(fn) { const id = randomUUID(); timers.set(id, fn); return id; }, clearTimeout: id => timers.delete(id),
    localStorage: { getItem(k) { if (storageBlocked) throw new Error('blocked'); return storage.get(k); },
      setItem(k, value) { if (storageBlocked) throw new Error('blocked'); writes.push(k); storage.set(k, value); } } };
  // No globals named window/document: exercise the exported factory without auto-init.
  const context = vm.createContext({ USAGE_ANALYTICS: config });
  vm.runInContext(source, context);
  const usage = context.createUsageAnalytics({ window, document, config: override });
  function load(f = frames.at(-1)) { f.events.load(); }
  function reply(type, extra = {}) { const f = frames.at(-1), channel = f.sent.find(m => m.data.type === 'configure')?.data.channel;
    listeners.get('message')({ source: f.contentWindow, origin: 'null', data: { type, channel }, ...extra }); }
  return { usage, window, document, frames, elements, storage, writes, timers, load, reply, listeners };
}

test('No frame or usage ID before consent; refusal survives reload', () => {
  const s = setup(); s.usage.track('scan_started'); assert.equal(s.frames.length, 0);
  s.usage.decline(); assert.equal(s.frames.length, 0);
  assert.equal(JSON.parse(s.storage.get(key)).clientId, null);
  const next = setup({ saved: JSON.parse(s.storage.get(key)) }); assert.equal(next.frames.length, 0);
});

test('Opt-in uses an opaque sandbox and only fixed configuration and event envelopes', () => {
  const s = setup({ location: { search: '?playerId=PRIVATE', hash: '#MATCH' } });
  s.usage.allow(); s.load(); s.reply('ready'); s.usage.track('scan_started'); s.usage.track('PRIVATE-PLAYER');
  const f = s.frames[0]; assert.equal(f.attributes.sandbox, 'allow-scripts'); assert.equal(f.attributes.referrerpolicy, 'no-referrer');
  const cfg = f.sent[0].data; assert.equal(cfg.pageLocation, config.origin + config.path);
  assert.deepEqual(Array.from(f.sent.slice(1), m => m.data.name), ['page_view', 'scan_started']);
  assert.ok(!JSON.stringify(f.sent).includes('PRIVATE'));
  assert.deepEqual(Array.from(s.writes), [key]);
});

test('Fake ready messages cannot release queued usage events', () => {
  const s = setup(); s.usage.allow(); s.load();
  s.reply('ready', { origin: config.origin }); s.reply('ready', { source: {} }); s.reply('ready', { data: { type: 'ready', channel: randomUUID() } });
  assert.equal(s.frames[0].sent.length, 1); s.reply('ready'); assert.equal(s.frames[0].sent.length, 2);
});

test('Saved consent retains the usage ID across reloads', () => {
  const saved = consent(); const s = setup({ saved }); s.load(); s.reply('ready');
  assert.equal(s.frames[0].sent[0].data.clientId, saved.clientId);
  assert.equal(s.frames[0].sent[1].data.name, 'page_view');
});

test('Button tracking sees a scan before its target handler disables the button', () => {
  const s = setup(), handlers = [];
  s.document.addEventListener = (name, fn, capture) => handlers.push({ fn, capture });
  const context = vm.createContext({ USAGE_ANALYTICS: config, window: s.window, document: s.document });
  vm.runInContext(source, context);
  s.elements.get('usage-allow').events.click(); s.load(); s.reply('ready');
  const button = { id: 'scan', disabled: false }, event = { target: { closest: () => button } };
  for (const h of handlers.filter(h => h.capture)) h.fn(event);
  button.disabled = true;
  for (const h of handlers.filter(h => !h.capture)) h.fn(event);
  assert.equal(s.frames.at(-1).sent.at(-1).data.name, 'scan_started');
});

test('Expired, corrupt or unrecognized consent never starts tracking', () => {
  for (const saved of [consent('granted', Date.now() - 1000), '{bad', { choice: 'yes', expires: Date.now() + 10000 }, { ...consent(), clientId: 'PLAYER' }]) {
    const s = setup({ saved }); s.usage.track('scan_started'); assert.equal(s.frames.length, 0);
    assert.equal(s.elements.get('usage-privacy').hidden, false);
  }
});

test('GPC and DNT block saved consent and the Allow button', () => {
  for (const navigator of [{ globalPrivacyControl: true }, { doNotTrack: '1' }]) {
    const s = setup({ saved: consent(), navigator }); s.usage.allow(); assert.equal(s.frames.length, 0);
    assert.equal(s.elements.get('usage-allow').disabled, true);
  }
});

test('Invalid IDs, other origins, HTTP, paths and missing config fail closed', () => {
  for (const options of [{ override: { ...config, measurementId: 'secret' } }, { location: { origin: 'https://fork.github.io' } },
    { location: { protocol: 'http:' } }, { location: { pathname: '/preview/' } }, { override: null }, { override: { ...config, path: '/preview/' } }]) {
    const s = setup(options); s.usage.allow(); assert.equal(s.frames.length, 0); assert.equal(s.elements.get('usage-allow').disabled, true);
  }
});

test('Blocked local storage requires fresh consent on every page', () => {
  const s = setup({ saved: consent(), storageBlocked: true }); assert.equal(s.frames.length, 0);
  s.usage.allow(); s.load(); s.reply('ready'); assert.equal(s.frames[0].sent[1].data.name, 'page_view');
  assert.equal(setup({ storageBlocked: true }).frames.length, 0);
});

test('Withdrawal disables the frame, clears queued events and permits a fresh opt-in', () => {
  const s = setup(); s.usage.allow(); s.load(); s.reply('ready'); s.usage.decline(); s.usage.track('scan_started');
  assert.equal(s.frames[0].sent.at(-1).data.type, 'disable');
  s.usage.allow(); assert.equal(s.frames[0].removed, true); s.load(); s.reply('ready');
  assert.equal(s.frames[1].sent[1].data.name, 'page_view'); assert.equal(s.timers.size, 0);
});

test('Cross-tab opt-out or consent deletion stops tracking without storage loops', () => {
  for (const newValue of [JSON.stringify(consent('denied')), null, '{bad']) {
    const s = setup({ saved: consent() }); s.load(); s.reply('ready');
    s.listeners.get('storage')({ key, newValue }); s.usage.track('scan_started');
    assert.equal(s.frames[0].sent.at(-1).data.type, 'disable'); assert.equal(s.writes.length, 0);
    s.reply('disabled'); assert.equal(s.frames[0].removed, true);
  }
});

test('Unavailable tag removes the frame and leaves a usable opt-in retry', () => {
  const s = setup(); s.usage.allow(); s.load(); s.reply('failed');
  assert.equal(s.frames[0].removed, true); assert.match(s.elements.get('usage-status').textContent, /could not load/);
  s.usage.track('scan_started'); assert.equal(s.frames.length, 1);
  s.usage.allow(); assert.equal(s.frames.length, 2);
});

test('Collector ignores untrusted configurations and unlisted event payloads', () => {
  const listeners = new Map(), replies = [], scripts = [];
  const parent = { postMessage: (data, origin) => replies.push({ data, origin }) };
  const document = { createElement: () => ({}), head: { append: s => scripts.push(s) } };
  const context = vm.createContext({ document, parent, addEventListener: (name, fn) => listeners.set(name, fn) });
  vm.runInContext('window = globalThis;', context);
  vm.runInContext(readFileSync(new URL('../wwwroot/usage-analytics-frame.js', import.meta.url), 'utf8'), context);
  const channel = randomUUID(), clientId = randomUUID();
  const data = { type: 'configure', measurementId: config.measurementId, channel, clientId, pageLocation: config.origin + config.path };
  const send = (d, origin = config.origin, from = parent) => listeners.get('message')({ source: from, origin, data: d });
  send(data, 'https://attacker.example'); send(data, config.origin, {}); send({ ...data, pageLocation: 'PRIVATE' }); assert.equal(scripts.length, 0);
  send(data); assert.equal(scripts.length, 1); scripts[0].onload();
  send({ type: 'event', channel, name: 'scan_started', playerId: 'PRIVATE' }); send({ type: 'event', channel, name: 'PRIVATE' });
  const commands = JSON.parse(vm.runInContext('JSON.stringify(dataLayer.map(c => Array.from(c)))', context));
  assert.equal(commands[0][2].analytics_storage, 'denied'); assert.equal(commands.filter(c => c[0] === 'event').length, 1);
  assert.ok(!JSON.stringify(commands).includes('PRIVATE'));
  assert.equal(vm.runInContext('document.cookie', context), '');
  send({ type: 'disable', channel }); send({ type: 'event', channel, name: 'scan_started' });
  assert.equal(vm.runInContext("window['ga-disable-G-ZM2VXKTLBC']", context), true);
  assert.equal(vm.runInContext('dataLayer.filter(c => c[0] === "event").length', context), 1);
});
