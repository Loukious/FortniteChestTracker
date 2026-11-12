// Bump this when replay parsing, eligibility, ownership, or chest-counting behavior changes.
export const PARSER_CACHE_VERSION = 'chest-parser-2026-11-12-v1';

const DATABASE_NAME = 'fortnite-chest-tracker-replay-cache';
const STORE_NAME = 'results';
const MEMORY_ENTRIES = 256;

function cacheableResult(result) {
  if (!result || typeof result !== 'object' || result.error) return false;
  if (result.match && !result.skipped) {
    return typeof result.match === 'object' && Array.isArray(result.match.players);
  }
  if (result.skipped && !result.match && typeof result.skipped.reason === 'string') {
    return !/\bstill recording\b|^(?:could not read|failed to read|unable to read)\b/i.test(result.skipped.reason.trim());
  }
  return false;
}

// The injectable factory keeps the browser implementation testable without a storage dependency.
export function createReplayCache(options = {}) {
  const version = options.parserVersion ?? PARSER_CACHE_VERSION;
  const resolveFactory = Object.hasOwn(options, 'indexedDB') ? () => options.indexedDB : () => globalThis.indexedDB;
  const memory = new Map();
  let databasePromise;

  function keyFor(contentHash, since) {
    if (typeof contentHash !== 'string' || !/^[a-f\d]{64}$/i.test(contentHash) || typeof since !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(since)) return null;
    return `${version}:${since}:${contentHash.toLowerCase()}`;
  }

  function remember(key, json) {
    memory.delete(key);
    memory.set(key, json);
    if (memory.size > MEMORY_ENTRIES) memory.delete(memory.keys().next().value);
  }

  function database() {
    if (!databasePromise) {
      databasePromise = new Promise((resolve, reject) => {
        const factory = resolveFactory();
        if (!factory?.open) { resolve(null); return; }
        const request = factory.open(DATABASE_NAME, 1);
        let finished = false;
        request.onupgradeneeded = () => {
          if (!request.result.objectStoreNames.contains(STORE_NAME)) request.result.createObjectStore(STORE_NAME, { keyPath: 'key' });
        };
        request.onsuccess = () => {
          if (finished) { request.result.close(); return; }
          finished = true;
          const connection = request.result;
          connection.onversionchange = () => { connection.close(); databasePromise = undefined; };
          resolve(connection);
        };
        request.onerror = () => { finished = true; reject(request.error); };
        request.onblocked = () => { finished = true; reject(new Error('Replay cache storage is blocked.')); };
      }).catch(() => null);
    }
    return databasePromise;
  }

  async function transact(mode, operation) {
    const connection = await database();
    if (!connection) return null;
    return new Promise((resolve, reject) => {
      const transaction = connection.transaction(STORE_NAME, mode);
      const request = operation(transaction.objectStore(STORE_NAME));
      let result;
      request.onsuccess = () => { result = request.result; };
      transaction.oncomplete = () => resolve(result);
      transaction.onabort = () => reject(transaction.error);
      transaction.onerror = () => reject(transaction.error);
    });
  }

  async function readCachedResult(contentHash, since) {
    const key = keyFor(contentHash, since);
    if (!key) return null;
    try {
      let json = memory.get(key);
      if (!json) {
        const record = await transact('readonly', (store) => store.get(key));
        json = record?.resultJson;
      }
      if (typeof json !== 'string') return null;
      const result = JSON.parse(json);
      if (!cacheableResult(result)) return null;
      remember(key, json);
      // Parsing returns a fresh object, so a worker can rewrite source paths safely.
      return result;
    } catch {
      return null;
    }
  }

  async function writeCachedResult(contentHash, since, result) {
    const key = keyFor(contentHash, since);
    if (!key || !cacheableResult(result)) return false;
    try {
      const json = JSON.stringify(result);
      const writtenKey = await transact('readwrite', (store) => store.put({ key, resultJson: json }));
      if (writtenKey === null) return false;
      remember(key, json);
      return true;
    } catch {
      return false;
    }
  }

  return { readCachedResult, writeCachedResult };
}

const cache = createReplayCache();
export const readCachedResult = cache.readCachedResult;
export const writeCachedResult = cache.writeCachedResult;
