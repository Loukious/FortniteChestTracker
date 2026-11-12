import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const source = readFileSync(new URL('../wwwroot/scanner-pool.js', import.meta.url), 'utf8');
const { ReplayScanner } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function fixture(hardwareConcurrency = 8, ...memoryInput) {
  const deviceMemory = memoryInput.length ? memoryInput[0] : 8;
  const workers = [];
  class Worker {
    constructor(url, options) {
      this.url = url;
      this.options = options;
      this.listeners = new Map();
      this.messages = [];
      this.terminated = false;
      workers.push(this);
    }
    addEventListener(type, listener) { this.listeners.set(type, listener); }
    postMessage(data) { this.messages.push(data); }
    terminate() { this.terminated = true; }
    emit(data) { this.listeners.get('message')({ data }); }
    progress(completed, fileName = 'reading.replay', requestId = this.messages[0].requestId) {
      this.emit({ type: 'progress', requestId, completed, fileName });
    }
    complete(report, requestId = this.messages[0].requestId) {
      this.emit({ type: 'complete', requestId, report });
    }
    error(message) { this.listeners.get('error')({ message, preventDefault() {} }); }
  }
  const scanner = new ReplayScanner({ Worker, hardwareConcurrency, deviceMemory });
  const messages = [], errors = [];
  scanner.addEventListener('message', event => messages.push(event.data));
  scanner.addEventListener('error', event => { event.preventDefault(); errors.push(event.message); });
  return { scanner, workers, messages, errors };
}

const files = (sizes = [90, 70, 50, 30]) => sizes.map((size, index) => ({ name: `${index}.replay`, size }));
const scan = (chosenFiles = files(), skipped = []) => ({ type: 'scan', files: chosenFiles,
  filePaths: chosenFiles.map(file => `Demos/${file.name}`), since: '2026-11-01',
  skipped, totalFiles: chosenFiles.length + skipped.length });
const report = (matches = [], skipped = []) => ({ matches, skipped });

test('parallelism respects memory limits, reserves one CPU, and stays within the file count', () => {
  for (const [cores, memory, expected] of [[8, 2, 1], [8, 4, 2], [8, 8, 4], [8, undefined, 2],
    [8, NaN, 2], [8, 0, 2], [2, 8, 1], [1, 8, 1], [3, 8, 2], [64, 32, 4]]) {
    const { scanner, workers } = fixture(cores, memory);
    scanner.postMessage(scan());
    assert.equal(workers.length, expected, `cores=${cores}, memory=${memory}`);
    assert.ok(workers.every(worker => worker.url === './worker.js' && worker.options.type === 'module'));
  }
  const small = fixture();
  small.scanner.postMessage(scan(files([2, 1])));
  assert.equal(small.workers.length, 2);
});

test('largest files are balanced by size and retain their aligned paths', () => {
  const { scanner, workers } = fixture(3, 8);
  const chosenFiles = files([10, 90, 20, 80]);
  const data = scan(chosenFiles);
  scanner.postMessage(data);
  assert.deepEqual(workers.map(worker => worker.messages[0].files.map(file => file.size)), [[90, 10], [80, 20]]);
  for (const worker of workers) {
    const chunk = worker.messages[0];
    assert.equal(chunk.files.length, chunk.totalFiles);
    assert.deepEqual(chunk.skipped, []);
    assert.deepEqual(chunk.filePaths, chunk.files.map(file => `Demos/${file.name}`));
    assert.equal(chunk.since, data.since);
  }
  assert.deepEqual(chosenFiles.map(file => file.size), [10, 90, 20, 80]);
});

test('zero or unknown file sizes still populate every chunk without losing path fallbacks', () => {
  const { scanner, workers } = fixture(3, 8);
  const chosenFiles = [{ name: 'a.replay' }, { name: 'b.replay', size: NaN, webkitRelativePath: 'Folder/b.replay' }];
  scanner.postMessage({ ...scan(chosenFiles), filePaths: [] });
  assert.deepEqual(workers.map(worker => worker.messages[0].filePaths), [['a.replay'], ['Folder/b.replay']]);
});

test('ready is emitted once after all parser workers are ready', () => {
  const { scanner, workers, messages } = fixture(3, 8);
  scanner.postMessage(scan());
  workers[0].emit({ type: 'ready' });
  assert.deepEqual(messages, []);
  workers[1].emit({ type: 'ready' });
  workers[0].emit({ type: 'ready' });
  assert.deepEqual(messages, [{ type: 'ready' }]);
});

test('parallel progress includes initial skips once and remains monotonic', () => {
  const { scanner, workers, messages } = fixture(3, 8);
  scanner.postMessage(scan(files(), [{ fileName: 'old.replay', reason: 'Old' }, { fileName: 'unchecked.replay', reason: 'Not selected' }]));
  workers[0].progress(1);
  workers[1].progress(1);
  workers[0].progress(0);
  workers[1].progress(100);
  assert.deepEqual(messages.map(message => message.completed), [3, 4, 4, 5]);
  assert.ok(messages.every(message => message.total === 6));
  workers[0].complete(report());
  assert.equal(messages.at(-1).completed, 6);
});

test('chunk reports merge once through the C# builder with all skips and global totals', () => {
  const { scanner, workers, messages } = fixture(3, 8);
  const initial = { fileName: 'old.replay', reason: 'Old' };
  const skippedA = { fileName: 'broken.replay', reason: 'Unreadable' };
  const skippedB = { fileName: 'creative.replay', reason: 'Unsupported' };
  const original = { matchId: 'same', filePath: 'UnsavedReplay-original.replay' };
  const copy = { matchId: 'same', filePath: 'copy.replay' };
  scanner.postMessage(scan(files(), [initial]));
  workers[1].complete(report([copy], [skippedB]));
  workers[1].complete(report([copy], [skippedB]));
  assert.equal(workers[0].messages.length, 1);
  workers[0].complete(report([original], [skippedA]));
  const merge = workers[0].messages[1];
  assert.equal(merge.type, 'build-report');
  assert.deepEqual(merge.matches, [original, copy]);
  assert.deepEqual(merge.skipped, [initial, skippedA, skippedB]);
  assert.equal(merge.totalFiles, 5);
  assert.equal(messages.filter(message => message.type === 'complete').length, 0);
  workers[0].complete(report([original], [skippedA]));
  assert.equal(messages.filter(message => message.type === 'complete').length, 0);
  const finalReport = report([original], [...merge.skipped, { fileName: 'copy.replay', reason: 'Duplicate' }]);
  workers[0].complete(finalReport, merge.requestId);
  assert.equal(messages.at(-1).report, finalReport);
  assert.equal(messages.filter(message => message.type === 'complete').length, 1);
});

test('single-worker scans preserve skips, progress and complete reports without a merge', () => {
  const { scanner, workers, messages } = fixture(2, 8);
  const data = scan(files(), [{ fileName: 'old.replay', reason: 'Old' }]);
  scanner.postMessage(data);
  assert.deepEqual(workers[0].messages[0], { ...data, requestId: 1 });
  workers[0].progress(3);
  const final = report([{ matchId: 'a' }], data.skipped);
  workers[0].complete(final);
  assert.equal(messages[0].completed, 3);
  assert.equal(messages.at(-1).report, final);
  assert.equal(workers[0].messages.length, 1);
});

test('later scans reuse idle parsers and ignore stale progress, completion and errors', () => {
  const { scanner, workers, messages } = fixture(2, 8);
  scanner.postMessage(scan());
  workers[0].complete(report());
  scanner.postMessage(scan());
  assert.equal(workers.length, 1);
  const oldId = workers[0].messages[0].requestId;
  const newId = workers[0].messages[1].requestId;
  const before = messages.length;
  workers[0].progress(4, '', oldId);
  workers[0].complete(report([{ matchId: 'old' }]), oldId);
  workers[0].emit({ type: 'error', requestId: oldId, message: 'old failure' });
  assert.equal(messages.length, before);
  assert.equal(workers[0].terminated, false);
  workers[0].complete(report([{ matchId: 'new' }]), newId);
  assert.equal(messages.at(-1).report.matches[0].matchId, 'new');
});

test('a replacement scan retires active workers and ignores their late messages', () => {
  const { scanner, workers, messages } = fixture(2, 8);
  scanner.postMessage(scan());
  scanner.postMessage(scan());
  assert.equal(workers.length, 2);
  assert.equal(workers[0].terminated, true);
  workers[0].complete(report());
  workers[0].error('late error');
  assert.deepEqual(messages, []);
  workers[1].complete(report());
  assert.equal(messages.at(-1).type, 'complete');
});

test('cancellation terminates every worker and ignores all later messages', () => {
  const { scanner, workers, messages, errors } = fixture();
  scanner.postMessage(scan());
  scanner.terminate();
  assert.ok(workers.every(worker => worker.terminated));
  for (const worker of workers) {
    worker.emit({ type: 'ready' });
    worker.complete(report());
    worker.error('late failure');
  }
  scanner.postMessage(scan());
  assert.equal(workers.length, 4);
  assert.deepEqual(messages, []);
  assert.deepEqual(errors, []);
});

test('native worker failures terminate the entire pool before emitting one error', () => {
  const { scanner, workers, messages, errors } = fixture();
  scanner.postMessage(scan());
  workers[2].error('Worker crashed');
  assert.ok(workers.every(worker => worker.terminated));
  assert.deepEqual(errors, ['Worker crashed']);
  workers[0].emit({ type: 'error', message: 'Late failure' });
  assert.deepEqual(messages, []);
});

test('parser and incomplete-report failures terminate the pool and forward a useful message', () => {
  for (const message of [{ type: 'error', message: 'Runtime failed' },
    { type: 'error', requestId: 1, message: 'Scan failed' },
    { type: 'complete', requestId: 1, report: { matches: [] } }]) {
    const { scanner, workers, messages } = fixture();
    scanner.postMessage(scan());
    workers[1].emit(message);
    assert.ok(workers.every(worker => worker.terminated));
    assert.equal(messages.at(-1).type, 'error');
    assert.match(messages.at(-1).message, /failed|incomplete/i);
  }
});

test('report-builder errors stop every worker and discard unfinished combined reports', () => {
  const { scanner, workers, messages } = fixture(3, 8);
  scanner.postMessage(scan());
  for (const worker of workers) worker.complete(report());
  workers[0].emit({ type: 'error', requestId: workers[0].messages[1].requestId, message: 'Report failed' });
  assert.ok(workers.every(worker => worker.terminated));
  assert.equal(messages.at(-1).type, 'error');
  assert.equal(messages.filter(message => message.type === 'complete').length, 0);
});
