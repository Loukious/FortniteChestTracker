import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
const source = readFileSync(new URL('../wwwroot/match-history.js', import.meta.url), 'utf8');
const { createMatchHistory, historyKey, mergeSavedMatches, filterSavedMatches, parseHistoryBackup } =
  await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

class Storage {
  constructor() { this.databases = new Map(); }
  open(name) {
    const request = {};
    queueMicrotask(() => {
      if (this.failOpen) { request.onerror?.(); return; }
      const fresh = !this.databases.has(name);
      if (fresh) this.databases.set(name, { stores: new Map(), tail: Promise.resolve() });
      const database = this.databases.get(name);
      request.result = { objectStoreNames: { contains: name => database.stores.has(name) },
        createObjectStore: name => database.stores.set(name, new Map()), close() {},
        transaction: (store, mode) => this.transaction(database, store, mode) };
      if (fresh) request.onupgradeneeded?.();
      request.onsuccess?.();
    });
    return request;
  }
  transaction(database, storeName, mode) {
    const tx = {};
    let release, working, pending = 0, finished = false;
    const previous = database.tail;
    if (mode === 'readwrite') database.tail = new Promise(resolve => { release = resolve; });
    const ready = previous.then(() => { working = new Map(database.stores.get(storeName)); });
    const operation = (method, value) => {
      pending++;
      const request = {};
      ready.then(() => queueMicrotask(() => {
        if (method === 'get') request.result = structuredClone(working.get(value));
        if (method === 'getAll') request.result = [...working.values()].map(v => structuredClone(v));
        if (method === 'put') { working.set(value.key, structuredClone(value)); request.result = value.key; }
        if (method === 'clear') working.clear();
        request.onsuccess?.({ target: request });
        pending--;
        queueMicrotask(() => {
          if (pending || finished) return;
          finished = true;
          if (this.failCommit && mode === 'readwrite') { tx.error = new Error('Quota exceeded'); tx.onabort?.(); }
          else { if (mode === 'readwrite') database.stores.set(storeName, working); tx.oncomplete?.(); }
          release?.();
        });
      }));
      return request;
    };
    tx.objectStore = () => ({ get: value => operation('get', value), getAll: () => operation('getAll'),
      put: value => operation('put', value), clear: () => operation('clear') });
    return tx;
  }
}

const match = (overrides = {}) => ({ matchId: 'match-a', ownerId: 'player-a', ownerName: 'Player A',
  matchDate: '2026-11-20T12:00:00', playlist: 'Playlist_DefaultSquad', mode: 'Battle Royale · Build',
  filePath: 'Demos/UnsavedReplay-original.replay', recordingLengthInMs: 600000, coverage: 'Opened counts',
  players: [{ playerId: 'player-a', name: 'Player A', isOwner: true, chests: 7, coverage: 'Opened counts' }], rewards: [], ...overrides });

test('timeline enrichment replaces legacy results and persists through reload and backup import', async () => {
  const storage = new Storage();
  const old = createMatchHistory({ indexedDB: storage, parserVersion: 'chest-parser-2026-11-23-persistent-history-v1' });
  await old.saveMatch(match());
  const oldBackup = await old.backup();
  const current = createMatchHistory({ indexedDB: storage, parserVersion: 'chest-parser-2026-11-23-timeline-v1' });
  const timeline = Array.from({ length: 7 }, (_, i) => ({ playerId: 'player-a', replayTimeSeconds: i * 30 }));
  await current.saveMatch(match({ openingTimeline: timeline }));
  await current.saveRecord(oldBackup.records[0]);
  const restored = (await createMatchHistory({ indexedDB: storage }).read()).matches;
  assert.equal(restored.length, 1);
  assert.deepEqual(restored[0].openingTimeline, timeline);
  assert.equal(restored[0].players[0].chests, 7);
  const imported = createMatchHistory({ indexedDB: null });
  for (const record of parseHistoryBackup(JSON.stringify(await current.backup()))) await imported.saveRecord(record);
  assert.deepEqual((await imported.read()).matches[0].openingTimeline, timeline);
});

test('match and player IDs preserve one result across copies, names, and visits', async () => {
  const storage = new Storage();
  const history = createMatchHistory({ indexedDB: storage });
  await history.saveMatch(match());
  await history.saveMatch(match({ matchId: 'MATCH-A', ownerId: 'PLAYER-A', ownerName: 'Renamed Player' }));
  await history.saveMatch(match({ filePath: 'copy.replay' }));
  const restored = await createMatchHistory({ indexedDB: storage }).read();
  assert.equal(restored.persistent, true);
  assert.equal(restored.matches.length, 1);
  assert.equal(restored.matches[0].players[0].chests, 7);
  assert.equal(restored.matches[0].ownerName, 'Renamed Player');
  assert.equal(historyKey(match()), historyKey(match({ matchId: 'MATCH-A', ownerId: 'PLAYER-A' })));
});

test('different match/player pairs remain separate even with the same display name', async () => {
  const history = createMatchHistory({ indexedDB: new Storage() });
  await history.saveMatches([match(), match({ matchId: 'match-b' }), match({ ownerId: 'player-b',
    players: [{ playerId: 'player-b', isOwner: true, chests: 5 }] })]);
  assert.equal((await history.read()).matches.length, 3);
});

test('shorter or unreadable rescans cannot replace a better recording', async () => {
  const history = createMatchHistory({ indexedDB: new Storage() });
  const good = match({ players: [{ playerId: 'player-a', isOwner: true, chests: 15 }] });
  await history.saveMatch(good);
  await history.saveMatch(match({ recordingLengthInMs: 10000, players: [{ playerId: 'player-a', isOwner: true, chests: 2 }] }));
  await history.saveMatch(match({ players: [{ playerId: 'player-a', isOwner: true, chests: null }], coverage: 'Unavailable' }));
  assert.equal((await history.read()).matches[0].players[0].chests, 15);
});

test('a newer parser can correct a count downward without old imports undoing it', async () => {
  const storage = new Storage();
  const old = createMatchHistory({ indexedDB: storage, parserVersion: 'parser-v1' });
  const current = createMatchHistory({ indexedDB: storage, parserVersion: 'parser-v2' });
  await old.saveMatch(match({ players: [{ playerId: 'player-a', isOwner: true, chests: 15 }] }));
  await current.saveMatch(match({ players: [{ playerId: 'player-a', isOwner: true, chests: 12 }] }));
  await old.saveMatch(match({ players: [{ playerId: 'player-a', isOwner: true, chests: 15 }] }));
  assert.equal((await createMatchHistory({ indexedDB: storage }).read()).matches[0].players[0].chests, 12);
});

test('concurrent writers choose one best match instead of adding counters', async () => {
  const storage = new Storage();
  const a = createMatchHistory({ indexedDB: storage }), b = createMatchHistory({ indexedDB: storage });
  await Promise.all([a.saveMatch(match({ recordingLengthInMs: 900000, players: [{ playerId: 'player-a', isOwner: true, chests: 20 }] })),
    b.saveMatch(match({ recordingLengthInMs: 10000, players: [{ playerId: 'player-a', isOwner: true, chests: 2 }] }))]);
  const rows = (await a.read()).matches;
  assert.equal(rows.length, 1); assert.equal(rows[0].players[0].chests, 20);
});

test('history filters dates and modes without deleting stored results', () => {
  const entries = [match({ matchDate: '2026-11-01T12:00:00' }), match({ matchId: 'rumble', isStandardMode: false }),
    match({ matchId: 'later', matchDate: '2026-11-22T12:00:00', playlist: 'Playlist_Respawn_Alt_MiniMobile' })];
  assert.equal(filterSavedMatches(entries, '2026-11-01', false).length, 1);
  assert.equal(filterSavedMatches(entries, '2026-11-01', true).length, 3);
  assert.equal(filterSavedMatches(entries, '2026-11-21', true).length, 1);
  assert.equal(mergeSavedMatches(entries, entries).length, 3);
});

test('trusted cached matches migrate once, ignoring skips and damaged or old entries', async () => {
  const storage = new Storage();
  let reads = 0;
  const readCachedRecords = async () => { reads++; return [
    { key: 'chest-parser-2026-11-20-decompression-bounds-v1:old-key', resultJson: JSON.stringify({ match: match() }) },
    { key: 'chest-parser-2026-11-19-other-modes-v1:other-key', resultJson: JSON.stringify({ match: match({ filePath: 'copy.replay' }) }) },
    { key: 'unknown-parser:key', resultJson: JSON.stringify({ match: match({ matchId: 'wrong' }) }) },
    { key: 'chest-parser-2026-11-20-decompression-bounds-v1:bad', resultJson: '{bad json' }
  ]; };
  const history = createMatchHistory({ indexedDB: storage, readCachedRecords });
  assert.equal(await history.importCachedMatches(), 1);
  assert.equal((await history.read()).matches.length, 1);
  assert.equal(await createMatchHistory({ indexedDB: storage, readCachedRecords }).importCachedMatches(), 0);
  assert.equal(reads, 1);
  await history.clear();
  const next = createMatchHistory({ indexedDB: storage, readCachedRecords });
  assert.equal(await next.importCachedMatches(), 0);
  assert.equal((await next.read()).matches.length, 0);
});

test('backups round-trip and reject invalid identities or negative counters', async () => {
  const history = createMatchHistory({ indexedDB: new Storage() });
  await history.saveMatch(match());
  const records = parseHistoryBackup(JSON.stringify(await history.backup()));
  const restored = createMatchHistory({ indexedDB: new Storage() });
  for (const record of records) await restored.saveRecord(record);
  assert.equal((await restored.read()).matches[0].players[0].chests, 7);
  assert.throws(() => parseHistoryBackup(JSON.stringify({ schemaVersion: 1, records: [{ match: match({ ownerId: '' }) }] })));
  assert.throws(() => parseHistoryBackup(JSON.stringify({ schemaVersion: 1, records: [{ match: match({ players: [{ playerId: 'player-a', isOwner: true, chests: -1 }] }) }] })));
});

test('unavailable storage retains results for the visit and permits a backup', async () => {
  const history = createMatchHistory({ indexedDB: null });
  assert.equal(await history.saveMatch(match()), false);
  const loaded = await history.read();
  assert.equal(loaded.persistent, false); assert.equal(loaded.matches.length, 1);
  assert.equal((await history.backup()).records.length, 1);
});

test('failed commits do not overwrite previously saved results', async () => {
  const storage = new Storage();
  const history = createMatchHistory({ indexedDB: storage });
  await history.saveMatch(match());
  storage.failCommit = true;
  assert.equal(await history.saveMatch(match({ players: [{ playerId: 'player-a', isOwner: true, chests: 99 }] })), false);
  storage.failCommit = false;
  assert.equal((await createMatchHistory({ indexedDB: storage }).read()).matches[0].players[0].chests, 7);
});
