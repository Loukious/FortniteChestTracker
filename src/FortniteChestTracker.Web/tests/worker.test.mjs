import assert from 'node:assert/strict';
import { webcrypto } from 'node:crypto';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const workerSource = readFileSync(new URL('../wwwroot/worker.js', import.meta.url), 'utf8')
  .replace(/^import .*;\r?$/gm, '');
const cacheSource = readFileSync(new URL('../wwwroot/replay-cache.js', import.meta.url), 'utf8');
const { createReplayCache, PARSER_CACHE_VERSION } =
  await import(`data:text/javascript;base64,${Buffer.from(cacheSource).toString('base64')}`);

// Run the real cache against an asynchronous storage adapter, including commit events.
class Storage {
  constructor() { this.records = new Map(); }
  open() {
    const request = {};
    queueMicrotask(() => {
      if (this.unavailable) { request.error = new Error('Storage unavailable'); request.onerror?.(); return; }
      request.result = {
        objectStoreNames: { contains: () => true }, close() {},
        transaction: () => {
          const transaction = {};
          const operation = (action, value) => {
            const result = {};
            queueMicrotask(() => {
              if (action === 'put') this.records.set(value.key, structuredClone(value));
              result.result = action === 'put' ? value.key : structuredClone(this.records.get(value));
              result.onsuccess?.();
              queueMicrotask(() => transaction.oncomplete?.());
            });
            return result;
          };
          transaction.objectStore = () => ({ get: key => operation('get', key), put: value => operation('put', value) });
          return transaction;
        }
      };
      request.onsuccess?.();
    });
    return request;
  }
}

function match(filePath, contentHash) {
  return { match: { filePath, fileName: filePath.split(/[\\/]/).pop(), matchId: contentHash,
    ownerId: 'owner', players: [{ playerId: 'owner', isOwner: true, chests: 11 }] } };
}

function replay(name, content = 'same replay bytes') {
  const bytes = new TextEncoder().encode(content);
  return { name, size: bytes.length, reads: 0,
    async arrayBuffer() { this.reads++; return bytes.slice().buffer; } };
}

async function fixture({ parse = (_bytes, path, _since, hash) => match(path, hash),
  build, cache, storage = new Storage() } = {}) {
  const messages = [], parseCalls = [], buildCalls = [], historyCalls = [], handlers = new Map();
  let actualCache;
  const parser = {
    ScanReplay(bytes, filePath, since, contentHash, includeOtherModes) {
      parseCalls.push({ bytes: Array.from(bytes), filePath, since, contentHash, includeOtherModes });
      return JSON.stringify(parse(bytes, filePath, since, contentHash, includeOtherModes));
    },
    BuildReport(matchesJson, skippedJson, totalFiles) {
      const input = { matches: JSON.parse(matchesJson), skipped: JSON.parse(skippedJson), totalFiles };
      buildCalls.push(input);
      return JSON.stringify(build ? build(input) : { matches: input.matches, skipped: input.skipped, filesScanned: totalFiles });
    }
  };
  const context = vm.createContext({ Uint8Array, crypto: webcrypto, Intl, PARSER_CACHE_VERSION,
    createMatchHistory: () => ({ async saveMatch(match) { historyCalls.push(structuredClone(match)); return true; } }),
    createReplayCache(options) {
      actualCache = cache ?? createReplayCache({ ...options, indexedDB: storage });
      return actualCache;
    },
    dotnet: { async create() { return { getConfig: () => ({ mainAssemblyName: 'test-parser' }),
      getAssemblyExports: async () => ({ FortniteChestTracker: { Web: { Program: parser } } }) }; } },
    self: { postMessage: message => messages.push(structuredClone(message)),
      addEventListener: (type, handler) => handlers.set(type, handler) }
  });
  vm.runInContext(workerSource, context);
  await vm.runInContext('ready', context);
  return { messages, parseCalls, buildCalls, historyCalls, cache: actualCache,
    async send(data) { await handlers.get('message')({ data }); return messages.at(-1); },
    scan(files, filePaths = files.map(file => file.name), requestId = 1, since = '2026-11-01', skipped = [], includeOtherModes = false) {
      return this.send({ type: 'scan', files, filePaths, requestId, since, skipped, includeOtherModes, totalFiles: files.length + skipped.length });
    } };
}

test('cached content avoids reparsing and rewrites copied paths without mutating the saved result', async () => {
  const worker = await fixture();
  const first = replay('original.replay'), copy = replay('copy.replay');
  await worker.scan([first], ['Demos/original.replay']);
  const copied = await worker.scan([copy], ['Selected\\copy.replay'], 2);
  assert.equal(worker.parseCalls.length, 1);
  assert.equal(copied.report.matches[0].filePath, 'Selected\\copy.replay');
  assert.equal(copied.report.matches[0].fileName, 'copy.replay');
  assert.equal(copied.report.matches[0].players[0].chests, 11);
  assert.equal(worker.buildCalls.at(-1).matches[0].filePath, 'Selected\\copy.replay');
  const saved = await worker.cache.readCachedResult(worker.parseCalls[0].contentHash, '2026-11-01');
  assert.equal(saved.match.filePath, 'Demos/original.replay');
  await worker.scan([first], ['Demos/original.replay'], 3);
  assert.equal(worker.parseCalls.length, 1);
  assert.equal(worker.messages.at(-1).report.matches[0].filePath, 'Demos/original.replay');
  assert.equal(first.reads, 2); // Contents are hashed again before trusting the cached report.
  assert.equal(worker.historyCalls.length, 3);
  assert.equal(worker.historyCalls[0].matchId, worker.historyCalls[1].matchId);
  assert.equal(worker.historyCalls[0].ownerId, worker.historyCalls[1].ownerId);
});

test('overwritten content and a different date cutoff cannot reuse stale parser results', async () => {
  const worker = await fixture();
  await worker.scan([replay('same-name.replay', 'old bytes')]);
  await worker.scan([replay('same-name.replay', 'new bytes')], undefined, 2);
  await worker.scan([replay('same-name.replay', 'new bytes')], undefined, 3, '2026-11-02');
  assert.equal(worker.parseCalls.length, 3);
  assert.notEqual(worker.parseCalls[0].contentHash, worker.parseCalls[1].contentHash);
  assert.equal(worker.parseCalls[1].contentHash, worker.parseCalls[2].contentHash);
  assert.equal(worker.parseCalls[2].since, '2026-11-02');
});

test('enabling other modes replaces cached playlist skips and turning it off restores the standard result', async () => {
  const worker = await fixture({ parse: (_bytes, path, _since, hash, includeOtherModes) => includeOtherModes
    ? match(path, hash) : { skipped: { fileName: path, reason: 'Outside standard BR / Zero Build: Playlist_Respawn_Alt_MiniMobile' } } });
  const file = replay('rumble.replay');
  assert.equal((await worker.scan([file])).report.matches.length, 0);
  const enabled = await worker.scan([file], undefined, 2, '2026-11-01', [], true);
  assert.equal(enabled.report.matches.length, 1);
  assert.equal(enabled.report.skipped.length, 0);
  assert.equal((await worker.scan([file], undefined, 3)).report.matches.length, 0);
  assert.equal((await worker.scan([file], undefined, 4, '2026-11-01', [], true)).report.matches.length, 1);
  assert.equal(worker.parseCalls.length, 2);
  assert.deepEqual(worker.parseCalls.map(call => call.includeOtherModes), [false, true]);
});

test('stable skips are cached with the selected file path while transient read and live skips retry', async () => {
  for (const [reason, expectedParses] of [
    ['Creative or another non-Battle Royale subgame.', 1],
    ['Still recording; scan again when the match finishes.', 2],
    ['Could not read replay: unexpected end of replay.', 2]
  ]) {
    const worker = await fixture({ parse: (_bytes, fileName) => ({ skipped: { fileName, reason } }) });
    await worker.scan([replay('first.replay')], ['Demos/first.replay']);
    const result = await worker.scan([replay('second.replay')], ['Copies/second.replay'], 2);
    assert.equal(worker.parseCalls.length, expectedParses, reason);
    assert.deepEqual(result.report.skipped, [{ fileName: 'Copies/second.replay', reason }]);
  }
});

test('scan and combined-report replies retain request IDs, initial skips and every duplicate candidate', async () => {
  const worker = await fixture();
  const initial = { fileName: 'old.replay', reason: 'Old file' };
  const complete = await worker.scan([replay('original.replay'), replay('edited.replay')],
    ['Demos/UnsavedReplay-original.replay', 'Copies/edited.replay'], 41, '2026-11-01', [initial]);
  assert.equal(complete.requestId, 41);
  assert.ok(worker.messages.filter(message => message.type === 'progress').every(message => message.requestId === 41));
  assert.equal(worker.messages.filter(message => message.type === 'progress').at(-1).completed, 3);
  assert.deepEqual(worker.buildCalls[0].skipped, [initial]);
  assert.deepEqual(worker.buildCalls[0].matches.map(candidate => candidate.filePath),
    ['Demos/UnsavedReplay-original.replay', 'Copies/edited.replay']);
  assert.equal(worker.buildCalls[0].matches[0].matchId, worker.buildCalls[0].matches[1].matchId);
  const additionalSkip = { fileName: 'creative.replay', reason: 'Unsupported mode' };
  const combined = await worker.send({ type: 'build-report', requestId: 42,
    matches: complete.report.matches, skipped: [initial, additionalSkip], totalFiles: 4 });
  assert.equal(combined.type, 'complete');
  assert.equal(combined.requestId, 42);
  assert.deepEqual(worker.buildCalls[1], { matches: complete.report.matches, skipped: [initial, additionalSkip], totalFiles: 4 });
  assert.equal(worker.parseCalls.length, 1);
});

test('one unreadable file, cache exception or parser exception does not stop later replay files', async () => {
  let cacheReads = 0;
  const cache = {
    async readCachedResult() { if (++cacheReads === 1) throw new Error('Cache read failed'); return null; },
    async writeCachedResult(_hash, _since, result) {
      if (result.match.filePath === 'bad-write.replay') throw new Error('Cache write failed');
      return false;
    }
  };
  const worker = await fixture({ cache, parse(bytes, path, since, hash) {
    if (path === 'bad-parser.replay') throw new Error('Parser failed');
    return match(path, hash);
  } });
  const unreadable = replay('bad-file.replay');
  unreadable.arrayBuffer = async () => { throw new Error('File read failed'); };
  const complete = await worker.scan([unreadable, replay('bad-cache.replay'), replay('bad-parser.replay'),
    replay('bad-write.replay'), replay('good.replay')], undefined, 55);
  assert.equal(complete.type, 'complete');
  assert.equal(complete.requestId, 55);
  assert.deepEqual(complete.report.matches.map(result => result.filePath), ['good.replay']);
  assert.deepEqual(complete.report.skipped.map(result => result.fileName),
    ['bad-file.replay', 'bad-cache.replay', 'bad-parser.replay', 'bad-write.replay']);
  assert.ok(complete.report.skipped.every(result => /Could not read replay: .*failed/.test(result.reason)));
  assert.equal(worker.messages.filter(message => message.type === 'progress').at(-1).completed, 5);
  assert.equal(worker.buildCalls[0].totalFiles, 5);
});

test('unavailable optional cache storage keeps valid replay parsing available', async () => {
  const storage = new Storage();
  storage.unavailable = true;
  const worker = await fixture({ storage });
  const result = await worker.scan([replay('first.replay'), replay('second.replay')]);
  assert.equal(result.report.matches.length, 2);
  assert.deepEqual(result.report.skipped, []);
  assert.equal(worker.parseCalls.length, 2);
});

test('a WebAssembly trap returns completed results and stops using the damaged parser', async () => {
  const worker = await fixture({ parse: (_bytes, path, _since, hash) => {
    if (path === 'bad.replay') { const error = new Error('memory access out of bounds'); error.name = 'RuntimeError'; throw error; }
    return match(path, hash);
  } });
  const result = await worker.scan([replay('good.replay', 'good'), replay('bad.replay', 'bad'), replay('later.replay', 'later')]);
  assert.equal(result.type, 'parser-crash');
  assert.equal(result.fileIndex, 1);
  assert.equal(result.completed, 1);
  assert.deepEqual(result.matches.map(m => m.filePath), ['good.replay']);
  assert.equal(worker.parseCalls.length, 2);
  assert.equal(worker.buildCalls.length, 0);
  const fresh = await fixture();
  const complete = await fresh.send({ type: 'scan', requestId: 2, since: '2026-11-01', totalFiles: 3,
    files: [replay('bad.replay', 'bad'), replay('later.replay', 'later')], matches: result.matches,
    skipped: result.skipped, completed: result.completed });
  assert.equal(complete.type, 'complete');
  assert.equal(complete.report.matches.length, 3);
  assert.equal(fresh.messages.filter(m => m.type === 'progress').at(-1).completed, 3);
});

test('a report-building trap returns all completed file results for recovery', async () => {
  const worker = await fixture({ build: () => { const e = new Error('memory access out of bounds'); e.name = 'RuntimeError'; throw e; } });
  const result = await worker.scan([replay('good.replay')]);
  assert.equal(result.type, 'parser-crash');
  assert.equal(result.fileIndex, 1);
  assert.equal(result.completed, 1);
  assert.equal(result.matches.length, 1);
});

test('uncached files recycle the heap in bounded batches while cache hits do not', async () => {
  const worker = await fixture();
  const files = Array.from({ length: 6 }, (_, i) => replay(`${i}.replay`, `bytes${i}`));
  const partial = await worker.scan(files);
  assert.equal(partial.type, 'parser-recycle');
  assert.equal(partial.fileIndex, 4);
  assert.equal(partial.completed, 4);
  assert.equal(partial.matches.length, 4);
  const fresh = await fixture();
  const complete = await fresh.send({ type: 'scan', requestId: 2, since: '2026-11-01', totalFiles: 6,
    files: files.slice(4), matches: partial.matches, skipped: partial.skipped, completed: partial.completed });
  assert.equal(complete.type, 'complete');
  assert.equal(complete.report.matches.length, 6);
  const cached = await worker.scan(files.slice(0, 4));
  assert.equal(cached.type, 'complete');
  assert.equal(worker.parseCalls.length, 4);
});

test('managed out-of-memory skips request recovery without committing the failed file', async () => {
  const worker = await fixture({ parse: (_bytes, path) => ({ skipped: { fileName: path, reason: 'Could not read replay: Arg_OutOfMemoryException' } }) });
  const result = await worker.scan([replay('large.replay')]);
  assert.equal(result.type, 'parser-crash');
  assert.equal(result.fileIndex, 0);
  assert.equal(result.completed, 0);
  assert.deepEqual(result.skipped, []);
});

test('report failures are correlated so the scanner pool can reject the active request', async () => {
  const worker = await fixture({ build() { throw new Error('Report failed'); } });
  const scanFailure = await worker.scan([replay('first.replay')], undefined, 71);
  assert.deepEqual(scanFailure, { type: 'error', message: 'Report failed', requestId: 71 });
  const mergeFailure = await worker.send({ type: 'build-report', matches: [], skipped: [], totalFiles: 0, requestId: 72 });
  assert.deepEqual(mergeFailure, { type: 'error', message: 'Report failed', requestId: 72 });
});
