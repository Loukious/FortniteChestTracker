import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

// Import the actual browser module without requiring a package.json or npm dependencies.
const source = readFileSync(new URL('../wwwroot/replay-cache.js', import.meta.url), 'utf8');
const { createReplayCache, PARSER_CACHE_VERSION, readCachedResult, writeCachedResult } =
  await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

const HASH = 'a'.repeat(64);
const OTHER_HASH = 'b'.repeat(64);
const SINCE = '2026-11-01';
const matchResult = () => ({ match: {
  filePath: 'Demos/first.replay', matchId: 'match-a', ownerId: 'owner-a',
  players: [{ playerId: 'owner-a', isOwner: true, chests: 7, coverage: 'Chests opened; replay may miss distant openings' }]
} });

class IndexedDBFixture {
  constructor() { this.stores = new Map(); this.openCalls = 0; this.readCalls = 0; }
  open() {
    this.openCalls++;
    const request = {};
    queueMicrotask(() => {
      if (this.failOpen) { request.error = new Error('Storage disabled'); request.onerror?.(); return; }
      request.result = {
        objectStoreNames: { contains: name => this.stores.has(name) },
        createObjectStore: name => this.stores.set(name, new Map()),
        transaction: (name, mode) => this.transaction(name, mode),
        close() {}
      };
      if (!this.stores.has('results')) request.onupgradeneeded?.();
      request.onsuccess?.();
    });
    return request;
  }
  transaction(name, mode) {
    if (this.failTransaction) throw new Error('Database connection unavailable');
    const transaction = {};
    const requestFor = (operation, value) => {
      const request = {};
      queueMicrotask(() => {
        if (this.failRequest) { transaction.error = new Error('Request failed'); transaction.onerror?.(); return; }
        if (operation === 'get') { this.readCalls++; request.result = structuredClone(this.stores.get(name).get(value)); }
        else { assert.equal(mode, 'readwrite'); request.result = value.key; }
        request.onsuccess?.();
        queueMicrotask(() => {
          if (this.failCommit) { transaction.error = new Error('Quota exceeded'); transaction.onabort?.(); return; }
          if (operation === 'put') this.stores.get(name).set(value.key, structuredClone(value));
          transaction.oncomplete?.();
        });
      });
      return request;
    };
    transaction.objectStore = () => ({ get: key => requestFor('get', key), put: value => requestFor('put', value) });
    return transaction;
  }
}

test('public worker APIs are available and unsupported storage is optional', async () => {
  assert.equal(typeof readCachedResult, 'function');
  assert.equal(typeof writeCachedResult, 'function');
  assert.ok(PARSER_CACHE_VERSION);
  const cache = createReplayCache({ indexedDB: null });
  assert.equal(await cache.readCachedResult(HASH, SINCE), null);
  assert.equal(await cache.writeCachedResult(HASH, SINCE, matchResult()), false);
});

test('content hashes isolate overwritten or unrelated files and survive a new cache instance', async () => {
  const indexedDB = new IndexedDBFixture();
  const first = createReplayCache({ indexedDB });
  const result = matchResult();
  assert.equal(await first.writeCachedResult(HASH, SINCE, result), true);
  result.match.players[0].chests = 999;
  const nextVisit = createReplayCache({ indexedDB });
  assert.equal((await nextVisit.readCachedResult(HASH.toUpperCase(), SINCE)).match.players[0].chests, 7);
  assert.equal(await nextVisit.readCachedResult(OTHER_HASH, SINCE), null);
});

test('a different cutoff or parser version never reuses the previous result', async () => {
  const indexedDB = new IndexedDBFixture();
  const oldParser = createReplayCache({ indexedDB, parserVersion: 'parser-v1' });
  await oldParser.writeCachedResult(HASH, SINCE, matchResult());
  assert.equal(await oldParser.readCachedResult(HASH, '2026-11-02'), null);
  const newParser = createReplayCache({ indexedDB, parserVersion: 'parser-v2' });
  assert.equal(await newParser.readCachedResult(HASH, SINCE), null);
  assert.equal(await newParser.writeCachedResult(HASH, SINCE, { skipped: { fileName: 'first.replay', reason: 'Outside standard BR / Zero Build: Creative' } }), true);
  assert.equal((await oldParser.readCachedResult(HASH, SINCE)).match.matchId, 'match-a');
});

test('workers can rewrite returned source paths without changing the cached result', async () => {
  const indexedDB = new IndexedDBFixture();
  const cache = createReplayCache({ indexedDB });
  await cache.writeCachedResult(HASH, SINCE, matchResult());
  const copy = await cache.readCachedResult(HASH, SINCE);
  copy.match.filePath = 'AnotherFolder/copy.replay';
  copy.match.players[0].chests = 1000;
  const again = await cache.readCachedResult(HASH, SINCE);
  assert.equal(again.match.filePath, 'Demos/first.replay');
  assert.equal(again.match.players[0].chests, 7);
  const skip = { skipped: { fileName: 'older.replay', reason: 'Recorded before 2026-11-01.' } };
  await cache.writeCachedResult(OTHER_HASH, SINCE, skip);
  const skippedCopy = await cache.readCachedResult(OTHER_HASH, SINCE);
  skippedCopy.skipped.fileName = 'copy.replay';
  assert.equal((await cache.readCachedResult(OTHER_HASH, SINCE)).skipped.fileName, 'older.replay');
});

test('live recordings and transient read failures are never persisted', async () => {
  const indexedDB = new IndexedDBFixture();
  const cache = createReplayCache({ indexedDB });
  for (const reason of ['Still recording; scan again when the match finishes.', 'Could not read replay: missing chunk.', 'Failed to read file: access denied.', 'Unable to read replay.']) {
    assert.equal(await cache.writeCachedResult(HASH, SINCE, { skipped: { fileName: 'file.replay', reason } }), false);
    assert.equal(await cache.readCachedResult(HASH, SINCE), null);
  }
  assert.equal(await cache.writeCachedResult(HASH, SINCE, { error: 'Reader failed' }), false);
});

test('opening, request, transaction, and quota failures degrade to ordinary parsing', async () => {
  for (const flag of ['failOpen', 'failTransaction', 'failRequest', 'failCommit']) {
    const indexedDB = new IndexedDBFixture();
    indexedDB[flag] = true;
    const cache = createReplayCache({ indexedDB });
    assert.equal(await cache.readCachedResult(HASH, SINCE), null, flag);
    assert.equal(await cache.writeCachedResult(HASH, SINCE, matchResult()), false, flag);
    assert.equal(await cache.readCachedResult(HASH, SINCE), null, flag);
  }
  const cache = createReplayCache({ get indexedDB() { throw new Error('Security policy denied storage'); } });
  assert.equal(await cache.readCachedResult(HASH, SINCE), null);
  assert.equal(await cache.writeCachedResult(HASH, SINCE, matchResult()), false);
});

test('malformed cache records and invalid keys are safely ignored', async () => {
  const indexedDB = new IndexedDBFixture();
  const cache = createReplayCache({ indexedDB });
  assert.equal(await cache.readCachedResult('not-a-hash', SINCE), null);
  assert.equal(await cache.writeCachedResult(HASH, undefined, matchResult()), false);
  await cache.writeCachedResult(HASH, SINCE, matchResult());
  const record = [...indexedDB.stores.get('results').values()][0];
  record.resultJson = '{broken';
  assert.equal(await createReplayCache({ indexedDB }).readCachedResult(HASH, SINCE), null);
  record.resultJson = JSON.stringify({ skipped: { reason: 'Still recording' } });
  assert.equal(await createReplayCache({ indexedDB }).readCachedResult(HASH, SINCE), null);
});

test('unchanged results already read by a worker reuse its memory copy', async () => {
  const indexedDB = new IndexedDBFixture();
  await createReplayCache({ indexedDB }).writeCachedResult(HASH, SINCE, matchResult());
  const cache = createReplayCache({ indexedDB });
  await cache.readCachedResult(HASH, SINCE);
  await cache.readCachedResult(HASH, SINCE);
  assert.equal(indexedDB.readCalls, 1);
});
