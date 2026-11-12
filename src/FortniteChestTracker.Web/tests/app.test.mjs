import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const source = readFileSync(new URL('../wwwroot/app.js', import.meta.url), 'utf8').replace(/^import .*scanner-pool\.js';\r?\n/m, '');

class Element {
  constructor(tag = 'div') {
    this.tagName = tag;
    this.children = [];
    this.dataset = {};
    this.attributes = new Map();
    this.listeners = new Map();
    this.value = '';
    this.textContent = '';
    this.validity = { valid: true };
    this.classes = new Set();
    this.classList = { toggle: (name, enabled) => enabled ? this.classes.add(name) : this.classes.delete(name) };
  }
  append(...children) {
    for (const child of children) this.children.push(...(child.tagName === 'fragment' ? child.children : [child]));
  }
  replaceChildren(...children) { this.children = []; this.append(...children); }
  setAttribute(name, value) { this.attributes.set(name, String(value)); }
  getAttribute(name) { return this.attributes.get(name); }
  removeAttribute(name) { this.attributes.delete(name); }
  addEventListener(name, listener) { this.listeners.set(name, listener); }
  click() {
    if (this.disabled) return;
    this.clickCount = (this.clickCount ?? 0) + 1;
    return this.listeners.get('click')?.();
  }
  querySelectorAll(selector) {
    const descendants = this.children.flatMap(child => [child, ...child.querySelectorAll(selector)]);
    return descendants.filter(child => child.tagName === 'input' && child.type === 'checkbox' &&
      (!selector.includes(':not(:disabled)') || !child.disabled));
  }
}

function loadApp(windowOptions = {}) {
  const elements = new Map();
  const getElement = (id) => {
    if (!elements.has(id)) elements.set(id, new Element());
    return elements.get(id);
  };
  const tabs = ['squad', 'players', 'skipped'].map(name => {
    const tab = getElement(`${name}-tab`);
    tab.id = `${name}-tab`;
    tab.setAttribute('aria-controls', `${name}-panel`);
    return tab;
  });
  getElement('since').value = '2026-11-01';
  const workers = [];
  class Worker {
    constructor() { this.listeners = new Map(); workers.push(this); }
    addEventListener(name, listener) { this.listeners.set(name, listener); }
    postMessage(message) { this.message = message; }
    terminate() { this.terminated = true; }
    emit(message) { this.listeners.get('message')({ data: message }); }
  }
  const document = {
    getElementById: getElement,
    querySelectorAll: () => tabs,
    createElement: (tag) => new Element(tag),
    createDocumentFragment: () => new Element('fragment')
  };
  const context = vm.createContext({ document, window: { addEventListener() {}, ...windowOptions }, ReplayScanner: Worker,
    HTMLInputElement: Element, Date, Intl, console, Set, Map, WeakMap });
  vm.runInContext(source, context);
  return { elements, workers, run: code => vm.runInContext(code, context),
    set: (name, value) => { context[name] = value; } };
}

function replay(name, lastModified) {
  return { name, lastModified, size: 1024, arrayBuffer() { throw new Error('File contents must not be read by the UI.'); } };
}

test('the default folder button uses the standard picker even when Chrome supports remembering', () => {
  let nativeCalls = 0;
  const app = loadApp({ showDirectoryPicker() { nativeCalls++; throw new Error('Protected AppData path'); } });
  app.set('chosenFiles', [replay('existing.replay', Date.now())]);
  app.run('acceptFiles(chosenFiles, false);');
  app.elements.get('choose-folder').click();
  assert.equal(app.elements.get('folder-input').clickCount, 1);
  assert.equal(nativeCalls, 0);
  assert.equal(app.run('state.files[0]'), app.run('chosenFiles[0]'));
  assert.equal(app.run('state.choosing'), false);
});

test('blocked native remembering leaves the standard folder and file buttons available', async () => {
  const app = loadApp({ async showDirectoryPicker() { throw Object.assign(new Error('Folder contains system files'), { name: 'NotAllowedError' }); } });
  app.set('chosenFiles', [replay('existing.replay', Date.now())]);
  app.run('acceptFiles(chosenFiles, false);');
  await app.elements.get('choose-remember-folder').click();
  assert.equal(app.elements.get('choose-folder').disabled, false);
  assert.equal(app.elements.get('choose-files').disabled, false);
  assert.equal(app.run('state.files[0]'), app.run('chosenFiles[0]'));
  assert.match(app.elements.get('status').textContent, /Use Choose replay folder for Demos/);
  app.elements.get('choose-folder').click();
  assert.equal(app.elements.get('folder-input').clickCount, 1);
  app.elements.get('choose-files').click();
  assert.equal(app.elements.get('files-input').clickCount, 1);
});

test('cancelling the optional remember picker preserves the previous selection and status', async () => {
  const app = loadApp({ async showDirectoryPicker() { throw Object.assign(new Error('Cancelled'), { name: 'AbortError' }); } });
  app.set('chosenFiles', [replay('existing.replay', Date.now())]);
  app.run('acceptFiles(chosenFiles, false);');
  const previousStatus = app.elements.get('status').textContent;
  await app.elements.get('choose-remember-folder').click();
  assert.equal(app.elements.get('status').textContent, previousStatus);
  assert.equal(app.run('state.files[0]'), app.run('chosenFiles[0]'));
  assert.equal(app.run('state.choosing'), false);
});

test('the optional remember picker preserves nested replay paths and reads only metadata', async () => {
  const file = replay('recent.REPLAY', Date.now());
  const handle = { name: 'ReplayCopies', kind: 'directory', async *entries() {
    yield ['notes.txt', { kind: 'file', getFile() { throw new Error('Non-replay metadata must not be read'); } }];
    yield ['nested', { kind: 'directory', async *entries() { yield [file.name, { kind: 'file', async getFile() { return file; } }]; } }];
  } };
  let options;
  const app = loadApp({ async showDirectoryPicker(value) { options = value; return handle; } });
  await app.elements.get('choose-remember-folder').click();
  assert.equal(options.mode, 'read');
  assert.equal(app.run('state.savedFolder'), handle);
  assert.equal(app.elements.get('use-saved-folder').hidden, false);
  app.run('scan();');
  assert.deepEqual(Array.from(app.workers[0].message.filePaths), ['ReplayCopies/nested/recent.REPLAY']);
  assert.equal(app.workers[0].message.files[0], file);
});

test('browsers without native remembering keep standard folder and file selection', () => {
  const app = loadApp();
  assert.equal(app.elements.get('choose-remember-folder').hidden, true);
  app.elements.get('choose-folder').click();
  app.elements.get('choose-files').click();
  assert.equal(app.elements.get('folder-input').clickCount, 1);
  assert.equal(app.elements.get('files-input').clickCount, 1);
});

test('the inclusive cutoff keeps midnight and rejects older files before worker parsing', () => {
  const app = loadApp();
  const midnight = new Date(2026, 10, 1).getTime();
  app.set('chosenFiles', [replay('old.replay', midnight - 1), replay('today.replay', midnight), replay('other.txt', midnight)]);
  app.run('acceptFiles(chosenFiles, false); scan();');
  assert.equal(app.workers.length, 1);
  assert.deepEqual(Array.from(app.workers[0].message.files, file => file.name), ['today.replay']);
  assert.equal(app.workers[0].message.totalFiles, 2);
  assert.equal(app.workers[0].message.skipped.length, 1);
  assert.match(app.workers[0].message.skipped[0].reason, /Last modified before 2026-11-01; contents not read/);
  assert.equal(app.elements.get('since').disabled, true);
});

test('unknown file timestamps defer to the replay date instead of claiming the file is old', () => {
  const app = loadApp();
  app.set('chosenFiles', [replay('unknown.replay', NaN)]);
  app.run('acceptFiles(chosenFiles, false); scan();');
  assert.equal(app.workers[0].message.files.length, 1);
  assert.equal(app.workers[0].message.skipped.length, 0);
  const row = app.elements.get('files-body').children[0];
  assert.equal(row.children[2].textContent, 'Date unavailable');
  assert.equal(row.children[4].textContent, 'Check replay date during scan');
});

test('an invalid calendar date never allows a scan', () => {
  const app = loadApp();
  app.elements.get('since').value = '2026-02-29';
  app.set('chosenFiles', [replay('new.replay', Date.now())]);
  app.run('acceptFiles(chosenFiles, false); scan();');
  assert.equal(app.workers.length, 0);
  assert.equal(app.elements.get('scan').disabled, true);
});

test('deselected eligible files are reported without being sent to the reader', () => {
  const app = loadApp();
  app.set('chosenFiles', [replay('one.replay', Date.now()), replay('two.replay', Date.now())]);
  app.run('acceptFiles(chosenFiles, false); state.selected.delete(fileKey(chosenFiles[1])); scan();');
  assert.deepEqual(Array.from(app.workers[0].message.files, file => file.name), ['one.replay']);
  assert.equal(app.workers[0].message.skipped[0].fileName, 'two.replay');
  assert.match(app.workers[0].message.skipped[0].reason, /Not selected/);
});

test('cancellation terminates parsing and ignores its late result while retaining the previous report', () => {
  const app = loadApp();
  const previousReport = { matches: [], skipped: [], filesScanned: 2 };
  app.set('previousReport', previousReport);
  app.set('chosenFiles', [replay('new.replay', Date.now())]);
  app.run("state.report = previousReport; state.reportSince = '2026-10-01'; acceptFiles(chosenFiles, false); scan();");
  app.elements.get('cancel').listeners.get('click')();
  app.workers[0].emit({ type: 'complete', report: { matches: [], skipped: [], filesScanned: 999 } });
  assert.equal(app.workers[0].terminated, true);
  assert.equal(app.run('state.report'), previousReport);
  assert.equal(app.run('state.reportSince'), '2026-10-01');
  assert.equal(app.elements.get('since').disabled, false);
  assert.equal(app.elements.get('export').disabled, false);
});

test('a completed report records the cutoff from its scan', () => {
  const app = loadApp();
  app.set('chosenFiles', [replay('new.replay', Date.now())]);
  app.run('acceptFiles(chosenFiles, false); scan();');
  app.elements.get('since').value = '2026-11-02';
  app.workers[0].emit({ type: 'complete', report: { matches: [], skipped: [], filesScanned: 1 } });
  assert.equal(app.run('state.reportSince'), '2026-11-01');
  assert.match(app.elements.get('report-summary').textContent, /2026-11-01/);
  assert.equal(app.elements.get('report').hidden, false);
});

test('missing chest counts remain unknown and partial squad totals are marked as subtotals', () => {
  const app = loadApp();
  app.set('matches', [{ coverage: 'Chests opened; replay may miss distant openings; team membership unavailable',
    players: [{ isOwner: true, chests: 4, coverage: 'Chests opened; replay may miss distant openings' }] }]);
  assert.equal(app.run('matchCounts(matches[0]).teammates'), null);
  assert.equal(app.run('summaryCount(matches, player => !player.isOwner, true)'), 'Unknown');
  assert.equal(app.run('summaryCount(matches, () => true, true)'), '4+');
  assert.equal(app.run("formatCount(null, 'Complete chest data')"), 'Unknown');
});

test('CSV preserves quoted player names and neutralizes spreadsheet formulas', () => {
  const app = loadApp();
  app.set('playerName', 'Player "One",\r\nTwo');
  assert.equal(app.run('csvEscape(playerName)'), '"Player ""One"",\r\nTwo"');
  for (const name of ['=1+1', '  =1+1', '+1', '-1', '@SUM(A1)', '\tPlayer', '\nPlayer']) {
    app.set('playerName', name);
    assert.ok(app.run('csvEscape(playerName)').startsWith('"\''));
  }
});
