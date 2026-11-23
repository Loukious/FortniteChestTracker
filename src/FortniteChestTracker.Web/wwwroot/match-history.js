export const HISTORY_SCHEMA_VERSION = 1;
const DATABASE = 'fortnite-chest-tracker-match-history';
const STORE = 'matches';
const MIGRATED = '\u0000cache-import-v1';
const TRUSTED_CACHE_VERSIONS = [
  'chest-parser-2026-11-17-cycles-and-receipts-v1',
  'chest-parser-2026-11-19-other-modes-v1',
  'chest-parser-2026-11-20-decompression-bounds-v1',
  'chest-parser-2026-11-23-persistent-history-v1'
];
const STANDARD = new Set(['playlist_defaultsolo', 'playlist_defaultduo', 'playlist_defaulttrio', 'playlist_trios',
  'playlist_defaultsquad', 'playlist_nobuildbr_solo', 'playlist_nobuildbr_duo', 'playlist_nobuildbr_trio', 'playlist_nobuildbr_squad']);
const identity = value => typeof value === 'string' ? value.trim().toLowerCase() : '';
const clone = value => JSON.parse(JSON.stringify(value));

export function historyKey(match) {
  const matchId = identity(match?.matchId), playerId = identity(match?.ownerId);
  return matchId && playerId ? JSON.stringify([matchId, playerId]) : null;
}

function validMatch(match) {
  if (!historyKey(match) || !Number.isFinite(Date.parse(match.matchDate)) || !Array.isArray(match.players)) return false;
  const owners = match.players.filter(p => p?.isOwner === true);
  return owners.length === 1 && identity(owners[0].playerId) === identity(match.ownerId) &&
    match.players.every(p => p && typeof p.playerId === 'string' && p.playerId.trim() &&
      (p.chests === null || Number.isSafeInteger(p.chests) && p.chests >= 0));
}

function quality(match) {
  const name = String(match.filePath ?? match.fileName ?? '').split(/[\\/]/).pop();
  return [Number(match.players.find(p => p.isOwner)?.chests != null),
    Number(/^UnsavedReplay-/i.test(name) && !/-edited/i.test(name)),
    Number(!/unreadable|unavailable/i.test(match.coverage ?? '')),
    Number.isFinite(match.recordingLengthInMs) ? match.recordingLengthInMs : 0];
}

function preferred(existing, incoming) {
  if (!existing) return incoming;
  const left = quality(existing.match), right = quality(incoming.match);
  for (let i = 0; i < left.length; i++) if (left[i] !== right[i]) return right[i] > left[i] ? incoming : existing;
  return String(incoming.parserVersion ?? '') >= String(existing.parserVersion ?? '') ? incoming : existing;
}

export function mergeSavedMatches(existing, incoming) {
  const records = new Map();
  for (const match of [...existing, ...incoming]) {
    if (!validMatch(match)) continue;
    const key = historyKey(match);
    records.set(key, preferred(records.get(key), { match, parserVersion: '' }));
  }
  return [...records.values()].map(r => clone(r.match)).sort((a, b) => Date.parse(b.matchDate) - Date.parse(a.matchDate));
}

export function filterSavedMatches(matches, since, includeOtherModes) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(since)) return [];
  const cutoff = new Date(`${since}T00:00:00`).getTime();
  return matches.filter(match => Date.parse(match.matchDate) >= cutoff && (includeOtherModes ||
    (typeof match.isStandardMode === 'boolean' ? match.isStandardMode :
      STANDARD.has(String(match.playlist ?? '').trim().replace(/^['"]|['"]$/g, '').split('/').pop().split('.').pop().toLowerCase()))));
}

export function parseHistoryBackup(text) {
  const data = JSON.parse(text);
  if (data?.schemaVersion !== HISTORY_SCHEMA_VERSION || !Array.isArray(data.records) ||
      data.records.some(r => !r || !validMatch(r.match))) throw new Error('This is not a supported match-history backup.');
  return data.records;
}

export function createMatchHistory(options = {}) {
  const getFactory = () => Object.hasOwn(options, 'indexedDB') ? options.indexedDB : globalThis.indexedDB;
  const version = options.parserVersion ?? 'current';
  const memory = new Map();
  let databasePromise, persistent = false;

  function open(name, store) {
    return new Promise(resolve => {
      let factory;
      try { factory = getFactory(); } catch { resolve(null); return; }
      if (!factory?.open) { resolve(null); return; }
      let request;
      try { request = factory.open(name, 1); } catch { resolve(null); return; }
      request.onupgradeneeded = () => {
        if (!request.result.objectStoreNames.contains(store)) request.result.createObjectStore(store, { keyPath: 'key' });
      };
      request.onsuccess = () => resolve(request.result);
      request.onerror = request.onblocked = () => resolve(null);
    });
  }

  async function database() {
    if (!databasePromise) databasePromise = open(DATABASE, STORE).then(db => {
      persistent = Boolean(db);
      if (db) db.onversionchange = () => { db.close(); databasePromise = undefined; persistent = false; };
      return db;
    });
    return databasePromise;
  }

  async function transaction(mode, action) {
    const db = await database();
    if (!db) return null;
    try {
      return await new Promise((resolve, reject) => {
        const tx = db.transaction(STORE, mode);
        let result;
        action(tx.objectStore(STORE), value => { result = value; });
        tx.oncomplete = () => resolve(result);
        tx.onerror = tx.onabort = () => reject(tx.error);
      });
    } catch { persistent = false; return null; }
  }

  async function saveRecord(record) {
    if (!validMatch(record?.match)) return false;
    const key = historyKey(record.match);
    const incoming = { key, match: clone(record.match), parserVersion: record.parserVersion ?? version };
    const saved = await transaction('readwrite', (store, done) => {
      const request = store.get(key);
      request.onsuccess = () => {
        const choice = preferred(request.result, incoming);
        store.put(choice).onsuccess = () => done(choice);
      };
    });
    memory.set(key, preferred(memory.get(key), saved ?? incoming));
    return saved !== null;
  }

  async function saveMatch(match) { return saveRecord({ match, parserVersion: version }); }
  async function saveMatches(matches) {
    let durable = true;
    for (const match of matches) if (validMatch(match) && !await saveMatch(match)) durable = false;
    return durable;
  }

  async function read() {
    const stored = await transaction('readonly', (store, done) => { store.getAll().onsuccess = e => done(e.target.result); });
    for (const record of stored ?? []) if (validMatch(record?.match)) memory.set(record.key, preferred(memory.get(record.key), record));
    return { matches: [...memory.values()].filter(r => validMatch(r.match)).map(r => clone(r.match)), persistent };
  }

  async function importCachedMatches() {
    const marker = await transaction('readonly', (store, done) => { store.get(MIGRATED).onsuccess = e => done(Boolean(e.target.result)); });
    if (marker || memory.has(MIGRATED)) return 0;
    let records = [];
    if (options.readCachedRecords) records = await options.readCachedRecords();
    else {
      const db = await open('fortnite-chest-tracker-replay-cache', 'results');
      if (!db) return 0;
      if (db?.objectStoreNames.contains('results')) {
        try {
          records = await new Promise((resolve, reject) => {
            const tx = db.transaction('results', 'readonly');
            const request = tx.objectStore('results').getAll();
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
          });
        } catch { records = []; }
      }
      db?.close();
    }
    const imported = new Set();
    for (const record of records) {
      if (!TRUSTED_CACHE_VERSIONS.some(v => String(record.key).startsWith(v + ':'))) continue;
      try {
        const match = JSON.parse(record.resultJson).match;
        if (!validMatch(match)) continue;
        await saveRecord({ match, parserVersion: record.key.split(':')[0] });
        imported.add(historyKey(match));
      } catch { /* A damaged cache entry must not block other matches. */ }
    }
    await transaction('readwrite', (store, done) => { store.put({ key: MIGRATED }).onsuccess = () => done(true); });
    memory.set(MIGRATED, { key: MIGRATED });
    return imported.size;
  }

  async function backup() {
    await read();
    return { schemaVersion: HISTORY_SCHEMA_VERSION, exportedAt: new Date().toISOString(),
      records: [...memory.values()].filter(r => validMatch(r.match)).map(r => clone(r)) };
  }

  async function clear() {
    const cleared = await transaction('readwrite', (store, done) => {
      store.clear().onsuccess = () => { store.put({ key: MIGRATED }).onsuccess = () => done(true); };
    });
    memory.clear();
    memory.set(MIGRATED, { key: MIGRATED });
    return cleared === true;
  }

  return { saveMatch, saveMatches, saveRecord, read, importCachedMatches, backup, clear };
}
