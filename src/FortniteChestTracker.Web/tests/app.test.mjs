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
  focus() { this.focused = true; }
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
  const tabs = ['squad', 'players', 'rewards', 'skipped'].map(name => {
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

function reward(playerId, options = {}) {
  return { playerName: playerId, playerId, isOwner: false, isTeammate: true,
    category: 'Loading screen', tier: 'Major', prizeTemplateId: 'example:loading',
    replayTimeSeconds: 45, rewardActorGuid: 10, prizeIndex: 0, ...options };
}

function match(options = {}) {
  return { matchId: 'match-a', matchDate: '2026-11-10T12:00:00', mode: 'BR', playlist: 'Playlist_DefaultSquad',
    filePath: 'Demos/example.replay', ownerId: 'owner-a', ownerName: 'Owner A',
    coverage: 'Chests opened; replay may miss distant openings',
    rewardCoverage: 'Recorded rewards only; distant animations may be missing.',
    players: [{ name: 'Owner A', playerId: 'owner-a', isOwner: true, chests: 7, coverage: 'Complete chest data' },
      { name: 'Teammate', playerId: 'teammate', isOwner: false, chests: 3, coverage: 'Complete chest data' }],
    rewards: [], ...options };
}

test('rewards keep separate players with the same prize and show the recorded recipient and time', () => {
  const app = loadApp();
  app.set('matches', [match({ rewards: [
    reward('teammate', { rewardActorGuid: 10, replayTimeSeconds: 85.99 }),
    reward('another-player', { isTeammate: false, rewardActorGuid: 11, replayTimeSeconds: 45 })
  ] })]);
  app.run('renderRewards(matches);');
  const rows = app.elements.get('rewards-body').children;
  assert.equal(rows.length, 2);
  assert.deepEqual(rows.map(row => row.children[0].textContent), ['another-player', 'teammate']);
  assert.deepEqual(rows.map(row => row.children[1].textContent), ['Other player', 'Teammate']);
  assert.deepEqual(rows.map(row => row.children[2].textContent), ['Loading screen', 'Loading screen']);
  assert.deepEqual(rows.map(row => row.children[5].textContent), ['00:45', '01:25']);
  assert.equal(app.elements.get('rewards-count').textContent, '(2)');
  assert.equal(app.elements.get('rewards-empty').hidden, true);
});

test('the replay-owner filter includes other recipients from that owners matches', () => {
  const app = loadApp();
  app.set('report', { matches: [
    match({ rewards: [reward('other-player', { isTeammate: false })] }),
    match({ matchId: 'match-b', ownerId: 'owner-b', ownerName: 'Owner B', rewards: [reward('owner-b', { isOwner: true })] })
  ], skipped: [], filesScanned: 2 });
  app.run("state.report = report; ui['owner-filter'].value = 'owner-a'; renderFilteredReport();");
  assert.equal(app.elements.get('rewards-body').children.length, 1);
  assert.equal(app.elements.get('rewards-body').children[0].children[0].textContent, 'other-player');
  assert.equal(app.elements.get('rewards-count').textContent, '(1)');
  app.run("ui['owner-filter'].value = ''; renderFilteredReport();");
  assert.equal(app.elements.get('rewards-body').children.length, 2);
  assert.equal(app.elements.get('rewards-count').textContent, '(2)');
});

test('unidentified recipients and unknown prize categories remain visible without identity guesses', () => {
  const app = loadApp();
  app.set('matches', [match({ rewards: [reward(null, { playerName: 'Unidentified player', isTeammate: false,
    category: 'Treasure (unrecognized reward)', tier: 'Unknown', replayTimeSeconds: null })] })]);
  app.run('renderRewards(matches);');
  const cells = app.elements.get('rewards-body').children[0].children;
  assert.equal(cells[0].textContent, 'Unidentified player');
  assert.equal(cells[1].textContent, 'Unidentified');
  assert.equal(cells[2].textContent, 'Treasure (unrecognized reward)');
  assert.equal(cells[3].textContent, 'Unknown');
  assert.equal(cells[5].textContent, 'Time unavailable');
  assert.equal(app.run('playerRewardCount(matches, null)'), 0);
});

test('empty reward evidence is displayed as none recorded and older report shapes still render', () => {
  const app = loadApp();
  app.set('report', { matches: [match({ rewards: undefined })], skipped: [], filesScanned: 1 });
  app.run('state.report = report; renderFilteredReport();');
  assert.equal(app.elements.get('rewards-body').children.length, 0);
  assert.equal(app.elements.get('rewards-empty').hidden, false);
  assert.equal(app.elements.get('matches-body').children[0].children[6].textContent, 'None recorded');
  assert.equal(app.elements.get('squad-body').children[0].children[3].textContent, 'None recorded');
  assert.equal(app.elements.get('players-body').children[0].children[4].textContent, 'None recorded');
  const html = readFileSync(new URL('../wwwroot/index.html', import.meta.url), 'utf8');
  assert.match(html, /None recorded<\/strong> does not mean no reward was received/);
  assert.match(html, /Gizmo reward detection is currently unavailable/);
});

test('recorded reward counts leave squad chest totals unchanged and exclude other recipients from squad counts', () => {
  const app = loadApp();
  app.set('report', { matches: [match({ rewards: [
    reward('OWNER-A', { isOwner: true }), reward('teammate'), reward('another-player', { isTeammate: false })
  ] })], skipped: [], filesScanned: 1 });
  app.run('state.report = report; renderFilteredReport();');
  assert.equal(app.run('matchCounts(state.visibleMatches[0]).squad'), 10);
  assert.equal(app.elements.get('squad-body').children[0].children[3].textContent, '1');
  assert.equal(app.elements.get('squad-body').children[1].children[3].textContent, '1');
  assert.equal(app.elements.get('rewards-body').children.length, 3);
});

test('reward coverage exceptions stay visible even when a match has no reward records and follow the owner filter', () => {
  const app = loadApp();
  const baseCoverage = 'Recorded rewards only; replay may miss distant reward animations. Gizmo reward detection unavailable.';
  app.set('report', { matches: [
    match({ rewards: [], rewardCoverage: baseCoverage + ' Some reward updates could not be read.' }),
    match({ matchId: 'match-b', ownerId: 'owner-b', ownerName: 'Owner B', rewardCoverage: baseCoverage,
      rewards: [reward('owner-b', { isOwner: true })] })
  ], skipped: [], filesScanned: 2 });
  app.run("state.report = report; ui['owner-filter'].value = 'owner-a'; renderFilteredReport();");
  const notes = app.elements.get('rewards-coverage');
  assert.equal(notes.hidden, false);
  assert.equal(notes.children.length, 1);
  assert.match(notes.children[0].textContent, /Some reward updates could not be read/);
  assert.match(app.elements.get('selected-match-reward-summary').textContent, /Some reward updates could not be read/);
  assert.match(app.elements.get('matches-body').children[0].children[6].title, /Some reward updates could not be read/);
  app.run("ui['owner-filter'].value = 'owner-b'; renderFilteredReport();");
  assert.equal(notes.hidden, true);
  assert.equal(notes.children.length, 0);
  assert.equal(app.elements.get('selected-match-reward-summary').textContent, baseCoverage);
  app.run("ui['owner-filter'].value = 'missing-owner'; renderFilteredReport();");
  assert.equal(app.elements.get('selected-match-reward-summary').textContent, '');
});

test('reward CSV rows preserve the chest schema and cannot double-count chest openings', () => {
  const app = loadApp();
  app.set('matches', [match({ rewards: [reward('teammate', { playerName: '=HYPERLINK("example")' }),
    reward('other', { playerName: 'Other, "player"', isTeammate: false, category: 'Gold outfit', tier: 'Supreme' })] })]);
  const rows = app.run('csvRows(matches)');
  assert.equal(rows.length, 5);
  assert.equal(rows[0].length, 19);
  assert.deepEqual(Array.from(rows[0].slice(14)), ['Row type', 'Reward category', 'Reward tier', 'Reward replay time', 'Prize template ID']);
  assert.equal(rows[1][9], 7);
  assert.equal(rows[1][12], 10);
  assert.equal(rows[1][14], 'Chest');
  assert.equal(rows[3][14], 'Reward');
  assert.deepEqual(Array.from(rows[3].slice(9, 13)), ['', '', '', '']);
  assert.equal(rows[3][15], 'Loading screen');
  assert.equal(rows[3][17], '00:45');
  assert.equal(rows[4][8], 'Other player');
  const csv = app.run("csvRows(matches).map(line => line.map(csvEscape).join(',')).join('\\r\\n')");
  assert.ok(csv.includes('"\'=HYPERLINK(""example"")"'));
  assert.ok(csv.includes('"Other, ""player"""'));
});

test('Rewards participates in accessible tab activation and keyboard navigation', () => {
  const app = loadApp();
  app.elements.get('rewards-tab').click();
  assert.equal(app.elements.get('rewards-tab').getAttribute('aria-selected'), 'true');
  assert.equal(app.elements.get('rewards-tab').tabIndex, 0);
  assert.equal(app.elements.get('rewards-panel').hidden, false);
  assert.equal(app.elements.get('squad-panel').hidden, true);
  assert.equal(app.elements.get('players-tab').tabIndex, -1);
  let prevented = false;
  app.elements.get('players-tab').listeners.get('keydown')({ key: 'ArrowRight', preventDefault() { prevented = true; } });
  assert.equal(prevented, true);
  assert.equal(app.elements.get('rewards-tab').focused, true);
  app.elements.get('rewards-tab').listeners.get('keydown')({ key: 'End', preventDefault() {} });
  assert.equal(app.elements.get('skipped-panel').hidden, false);
  const html = readFileSync(new URL('../wwwroot/index.html', import.meta.url), 'utf8');
  assert.match(html, /id="rewards-tab" aria-controls="rewards-panel" aria-selected="false" tabindex="-1"/);
  assert.match(html, /role="tabpanel" id="rewards-panel" aria-labelledby="rewards-tab" hidden/);
});
