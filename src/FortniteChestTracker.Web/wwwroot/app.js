import { ReplayScanner } from './scanner-pool.js';

const byId = (id) => document.getElementById(id);
const ui = Object.fromEntries([
  'choose-folder', 'choose-files', 'use-saved-folder', 'copy-path', 'default-folder-path', 'folder-memory-hint', 'folder-input', 'files-input', 'since', 'folder-summary',
  'files-panel', 'selection-summary', 'select-all', 'files-body', 'scan-selection-label', 'scan',
  'status', 'cancel', 'progress', 'report', 'report-summary', 'owner-filter', 'export',
  'owner-total', 'teammate-total', 'squad-total', 'match-total', 'owners-summary', 'files-summary',
  'matches-body', 'matches-empty', 'selected-match-summary', 'squad-body', 'players-body',
  'skipped-body', 'skipped-count', 'skipped-empty'
].map((id) => [id, byId(id)]));

const state = {
  files: [], eligible: [], dateSkipped: [], selected: new Set(), worker: null, readerReady: false,
  busy: false, choosing: false, report: null, reportSince: null, scanSince: null, scanStartedAt: null, visibleMatches: [], selectedMatch: null,
  savedFolder: null, savedFolderVersion: 0
};
const relativePaths = new WeakMap();
const number = new Intl.NumberFormat();
const dateTime = new Intl.DateTimeFormat(undefined, { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' });
const caseKey = (value) => String(value ?? '').toLocaleLowerCase('en-US');
const filePath = (file) => relativePaths.get(file) || file.webkitRelativePath || file.name;
const fileKey = (file) => `${filePath(file)}\u0000${file.size}\u0000${file.lastModified}`;
const completeCoverage = (coverage) => String(coverage ?? '') === 'Complete chest data';
const unknownTeam = (match) => String(match.coverage ?? '').includes('team membership unavailable');
const knownCount = (value) => typeof value === 'number' && Number.isFinite(value) ? value : null;

function dateThreshold() {
  const value = ui.since.value;
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match || !ui.since.validity.valid) return null;
  const threshold = new Date(0);
  threshold.setFullYear(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  threshold.setHours(0, 0, 0, 0);
  if (!Number.isFinite(threshold.getTime()) || threshold.getFullYear() !== Number(match[1]) ||
      threshold.getMonth() !== Number(match[2]) - 1 || threshold.getDate() !== Number(match[3])) return null;
  return threshold.getTime();
}

function setStatus(message) { ui.status.textContent = message; }

function openFolderStore() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open('fortnite-chest-tracker', 1);
    request.onupgradeneeded = () => {
      if (!request.result.objectStoreNames.contains('folders')) request.result.createObjectStore('folders');
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
    request.onblocked = () => reject(new Error('Folder storage is unavailable.'));
  });
}

async function folderStorage(action, handle) {
  const database = await openFolderStore();
  try {
    return await new Promise((resolve, reject) => {
      const transaction = database.transaction('folders', action === 'save' ? 'readwrite' : 'readonly');
      const store = transaction.objectStore('folders');
      const request = action === 'save' ? store.put(handle, 'replay-folder') : store.get('replay-folder');
      transaction.oncomplete = () => resolve(request.result);
      transaction.onabort = () => reject(transaction.error || new Error('Folder storage is unavailable.'));
      transaction.onerror = () => reject(transaction.error || new Error('Folder storage is unavailable.'));
    });
  } finally {
    database.close();
  }
}

async function restoreSavedFolder() {
  if (typeof window.showDirectoryPicker !== 'function') {
    ui['folder-memory-hint'].textContent = 'Choose the Demos folder using the path above. Choose files instead if your browser does not support folder selection.';
    return;
  }
  const version = state.savedFolderVersion;
  try {
    if (!('indexedDB' in window)) return;
    const handle = await folderStorage('read');
    if (version !== state.savedFolderVersion || handle?.kind !== 'directory' || typeof handle.entries !== 'function') return;
    state.savedFolder = handle;
    ui['use-saved-folder'].hidden = false;
    ui['use-saved-folder'].title = `Open ${handle.name}`;
    ui['folder-memory-hint'].textContent = `Saved folder: ${handle.name}. Use it above to reload its replay files, or choose another folder.`;
  } catch {
    // Folder selection still works when this browser cannot store a handle.
  }
}

async function rememberFolder(handle) {
  state.savedFolder = handle;
  state.savedFolderVersion++;
  ui['use-saved-folder'].hidden = false;
  ui['use-saved-folder'].title = `Open ${handle.name}`;
  try {
    if (!('indexedDB' in window)) throw new Error('Storage unavailable.');
    await folderStorage('save', handle);
    ui['folder-memory-hint'].textContent = `Saved folder: ${handle.name}. Future folder selections will start there; use the saved folder button to reload its files.`;
  } catch {
    ui['folder-memory-hint'].textContent = 'This folder can be reused while this page is open. Choose it again on your next visit.';
  }
}

async function collectFolderFiles(handle) {
  const files = [];
  let ignored = 0;
  async function visit(directory, prefix) {
    for await (const [name, entry] of directory.entries()) {
      const path = `${prefix}/${name}`;
      if (entry.kind === 'directory') {
        await visit(entry, path);
      } else if (/\.replay$/i.test(name)) {
        const file = await entry.getFile();
        relativePaths.set(file, path);
        files.push(file);
      } else {
        ignored++;
      }
    }
  }
  await visit(handle, handle.name);
  return { files, ignored };
}

async function loadFolderHandle(handle) {
  setStatus(`Listing replay files in ${handle.name}…`);
  const { files, ignored } = await collectFolderFiles(handle);
  acceptFiles(files, true, handle.name, ignored);
  await rememberFolder(handle);
}

async function chooseFolder() {
  if (state.busy || state.choosing) return;
  if (typeof window.showDirectoryPicker !== 'function') {
    ui['folder-input'].click();
    return;
  }
  const previousStatus = ui.status.textContent;
  state.choosing = true;
  refreshControls();
  try {
    const handle = await window.showDirectoryPicker({ id: 'fortnite-replays', mode: 'read', startIn: state.savedFolder || 'documents' });
    await loadFolderHandle(handle);
  } catch (error) {
    if (error.name === 'AbortError') {
      setStatus(previousStatus);
    } else if (['SecurityError', 'NotAllowedError', 'NotSupportedError', 'TypeError'].includes(error.name)) {
      state.choosing = false;
      refreshControls();
      ui['folder-input'].click();
      setStatus('Choose the Demos folder using the path above. The standard folder picker is available here.');
    } else {
      setStatus(`Could not open that folder: ${error.message || error}. Choose the replay folder again.`);
    }
  } finally {
    state.choosing = false;
    refreshControls();
  }
}

async function useSavedFolder() {
  if (!state.savedFolder || state.busy || state.choosing) return;
  const previousStatus = ui.status.textContent;
  state.choosing = true;
  refreshControls();
  try {
    const options = { mode: 'read' };
    const permission = await state.savedFolder.queryPermission(options);
    if (permission !== 'granted' && await state.savedFolder.requestPermission(options) !== 'granted') {
      setStatus('Folder access was not granted. Choose the replay folder to try again.');
      return;
    }
    await loadFolderHandle(state.savedFolder);
  } catch (error) {
    setStatus(error.name === 'AbortError' ? previousStatus : 'The saved folder could not be opened. Choose the replay folder again.');
  } finally {
    state.choosing = false;
    refreshControls();
  }
}

async function copyFolderPath() {
  const path = ui['default-folder-path'].textContent;
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(path);
    } else {
      const text = document.createElement('textarea');
      text.value = path;
      text.setAttribute('readonly', '');
      text.style.cssText = 'position:fixed;left:-9999px;top:0';
      document.body.append(text);
      text.select();
      const copied = document.execCommand('copy');
      text.remove();
      if (!copied) throw new Error('Copy unavailable.');
    }
    setStatus('Folder path copied. Paste it into the folder picker address bar.');
  } catch {
    const range = document.createRange();
    range.selectNodeContents(ui['default-folder-path']);
    const selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
    setStatus('Copy the highlighted folder path and paste it into the folder picker address bar.');
  }
}
function cell(value, className) {
  const element = document.createElement('td');
  element.textContent = value ?? '';
  if (className) element.className = className;
  return element;
}
function formatDate(value) {
  const date = new Date(value);
  return Number.isFinite(date.getTime()) ? dateTime.format(date) : 'Date unavailable';
}
function formatSize(bytes) {
  return bytes >= 1024 * 1024 ? `${number.format(Math.round(bytes / 1024 / 1024 * 10) / 10)} MB`
    : `${number.format(Math.ceil(bytes / 1024))} KB`;
}
function formatCount(value, coverage) {
  const count = knownCount(value);
  return count === null ? 'Unknown' : number.format(count) + (completeCoverage(coverage) ? '' : '+');
}
function sumPlayers(players) {
  const values = players.map((player) => knownCount(player.chests));
  return values.some((value) => value === null) ? null : values.reduce((total, value) => total + value, 0);
}
function matchCounts(match) {
  const players = match.players ?? [];
  return {
    owner: knownCount(players.find((player) => player.isOwner)?.chests),
    teammates: unknownTeam(match) ? null : sumPlayers(players.filter((player) => !player.isOwner)),
    squad: unknownTeam(match) ? null : sumPlayers(players)
  };
}
function summaryCount(matches, predicate, withTeamCoverage = false) {
  const players = matches.flatMap((match) => match.players ?? []).filter(predicate);
  const teamUnavailable = withTeamCoverage && matches.some((match) => matchCounts(match).teammates === null);
  if ((players.length === 0 && teamUnavailable) || (players.length > 0 && players.every((player) => knownCount(player.chests) === null))) return 'Unknown';
  const total = players.reduce((sum, player) => sum + (knownCount(player.chests) ?? 0), 0);
  const partial = teamUnavailable || players.some((player) => knownCount(player.chests) === null || !completeCoverage(player.coverage));
  return number.format(total) + (partial ? '+' : '');
}

function acceptFiles(files, folderSelected, folderName = '', ignoredCount = 0) {
  if (state.busy) return;
  const chosen = Array.from(files);
  state.files = chosen.filter((file) => /\.replay$/i.test(file.name)).sort((a, b) => filePath(a).localeCompare(filePath(b)));
  state.selected.clear();
  const folder = folderName || (folderSelected && chosen.length ? filePath(chosen[0]).split('/')[0] : '');
  const ignored = ignoredCount + chosen.length - state.files.length;
  ui['folder-summary'].textContent = `${folder ? `${folder} · ` : ''}${number.format(state.files.length)} replay files${ignored ? ` · ${number.format(ignored)} other files ignored` : ''}`;
  ui['files-panel'].hidden = false;
  filterFiles(true);
  setStatus(state.files.length ? 'Review the selected replay files, then scan. Nothing is uploaded.' : 'No .replay files found in this selection. Choose your Fortnite replay folder or select replay files instead.');
  ui['folder-input'].value = '';
  ui['files-input'].value = '';
}

function filterFiles(selectNewEligible = false) {
  const threshold = dateThreshold();
  state.eligible = [];
  state.dateSkipped = [];
  const fragment = document.createDocumentFragment();
  for (const file of state.files) {
    // Only a known older timestamp rules a file out; otherwise let the replay date decide.
    const hasFileDate = Number.isFinite(file.lastModified);
    const eligible = threshold !== null && (!hasFileDate || file.lastModified >= threshold);
    if (eligible) {
      state.eligible.push(file);
      if (selectNewEligible) state.selected.add(fileKey(file));
    } else {
      state.selected.delete(fileKey(file));
      state.dateSkipped.push({ fileName: filePath(file), reason: threshold === null ? 'Choose a valid start date; contents not read.' : `Last modified before ${ui.since.value}; contents not read.` });
    }
    const row = document.createElement('tr');
    if (!eligible) row.className = 'file-skipped';
    const checkboxCell = cell('');
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.checked = eligible && state.selected.has(fileKey(file));
    checkbox.disabled = !eligible || state.busy || state.choosing;
    checkbox.dataset.key = fileKey(file);
    checkbox.setAttribute('aria-label', `Include ${filePath(file)}`);
    checkboxCell.append(checkbox);
    row.append(checkboxCell, cell(filePath(file)), cell(hasFileDate ? formatDate(file.lastModified) : 'Date unavailable'), cell(formatSize(file.size)),
      cell(eligible ? hasFileDate ? 'Ready to scan' : 'Check replay date during scan' : threshold === null ? 'Choose a valid start date' : `Skipped · before ${ui.since.value}`, `file-status${eligible ? '' : ' skipped'}`));
    fragment.append(row);
  }
  ui['files-body'].replaceChildren(fragment);
  ui['selection-summary'].textContent = `${number.format(state.eligible.length)} eligible · ${number.format(state.dateSkipped.length)} skipped by file date`;
  updateSelection();
}

function updateSelection() {
  const selectedCount = state.eligible.reduce((count, file) => count + Number(state.selected.has(fileKey(file))), 0);
  ui['scan-selection-label'].textContent = `${number.format(selectedCount)} of ${number.format(state.eligible.length)} eligible replay files selected`;
  ui['select-all'].textContent = selectedCount === state.eligible.length && selectedCount > 0 ? 'Clear selection' : 'Select all eligible';
  ui.scan.disabled = state.busy || state.choosing || selectedCount === 0 || dateThreshold() === null;
  ui['select-all'].disabled = state.busy || state.choosing || state.eligible.length === 0;
}

function setBusy(busy) {
  state.busy = busy;
  refreshControls();
}

function refreshControls() {
  const locked = state.busy || state.choosing;
  for (const id of ['choose-folder', 'choose-files', 'use-saved-folder', 'since']) ui[id].disabled = locked;
  ui.cancel.hidden = !state.busy;
  ui.export.disabled = locked || !state.report;
  const eligibleKeys = new Set(state.eligible.map(fileKey));
  for (const checkbox of ui['files-body'].querySelectorAll('input[type=checkbox]')) {
    checkbox.disabled = locked || !eligibleKeys.has(checkbox.dataset.key);
  }
  updateSelection();
}

function destroyWorker() {
  state.worker?.terminate();
  state.worker = null;
  state.readerReady = false;
}

function createWorker() {
  const worker = new ReplayScanner();
  state.worker = worker;
  worker.addEventListener('message', (event) => {
    if (state.worker !== worker) return;
    const message = event.data ?? {};
    if (message.type === 'ready') {
      state.readerReady = true;
      if (state.busy) setStatus('Reading selected replays…');
    } else if (message.type === 'progress' && state.busy) {
      ui.progress.hidden = false;
      ui.progress.max = Math.max(1, Number(message.total) || 1);
      ui.progress.value = Math.min(ui.progress.max, Math.max(0, Number(message.completed) || 0));
      setStatus(`Reading ${number.format(Math.min(ui.progress.max, ui.progress.value + 1))} of ${number.format(ui.progress.max)}: ${message.fileName ?? ''}`);
    } else if (message.type === 'complete' && state.busy) {
      if (!Array.isArray(message.report?.matches) || !Array.isArray(message.report?.skipped)) {
        failScan('The replay reader returned an incomplete report. Try scanning again.');
        return;
      }
      state.report = message.report;
      state.reportSince = state.scanSince;
      ui.progress.hidden = true;
      setBusy(false);
      populateReport();
      const seconds = Math.round((Date.now() - state.scanStartedAt) / 100) / 10;
      setStatus(`Scan complete in ${number.format(seconds)}s: ${number.format(state.report.matches.length)} matches included, ${number.format(state.report.skipped.length)} files skipped. Select a match for its squad breakdown.`);
    } else if (message.type === 'error') {
      failScan(message.message || 'The replay reader could not complete the scan.');
    }
  });
  worker.addEventListener('error', (event) => {
    if (state.worker !== worker) return;
    event.preventDefault();
    failScan(event.message || 'The replay reader could not start. Reload the page and try again.');
  });
  return worker;
}

function failScan(message) {
  destroyWorker();
  setBusy(false);
  ui.progress.hidden = true;
  setStatus(`Scan failed: ${message}${state.report ? ' The previous completed report is still available.' : ''}`);
}

function scan() {
  if (state.busy || state.choosing || dateThreshold() === null) return;
  const files = state.eligible.filter((file) => state.selected.has(fileKey(file)));
  if (files.length === 0) return;
  state.scanSince = ui.since.value;
  state.scanStartedAt = Date.now();
  const skipped = [...state.dateSkipped, ...state.eligible.filter((file) => !state.selected.has(fileKey(file))).map((file) => ({ fileName: filePath(file), reason: 'Not selected for this scan; contents not read.' }))];
  setBusy(true);
  ui.progress.hidden = false;
  ui.progress.removeAttribute('value');
  setStatus(state.readerReady ? 'Reading selected replays…' : 'Loading the replay reader… Your files will be processed in this browser.');
  try {
    const worker = state.worker || createWorker();
    worker.postMessage({ type: 'scan', files, filePaths: files.map(filePath), since: state.scanSince, skipped, totalFiles: state.files.length });
  } catch (error) {
    failScan(error.message || String(error));
  }
}

function populateReport() {
  const previousOwner = ui['owner-filter'].value;
  const owners = new Map();
  for (const match of state.report.matches) {
    const key = caseKey(match.ownerId);
    if (!owners.has(key)) owners.set(key, { id: key, name: match.ownerName || match.ownerId || 'Unnamed owner' });
  }
  const allOption = document.createElement('option');
  allOption.value = '';
  allOption.textContent = 'All replay owners';
  const options = [allOption];
  for (const owner of Array.from(owners.values()).sort((a, b) => a.name.localeCompare(b.name))) {
    const option = document.createElement('option');
    option.value = owner.id;
    option.textContent = owner.name;
    options.push(option);
  }
  ui['owner-filter'].replaceChildren(...options);
  ui['owner-filter'].value = owners.has(previousOwner) ? previousOwner : owners.size === 1 ? owners.keys().next().value : '';
  ui['report-summary'].textContent = `Last completed scan · matches since ${state.reportSince}`;
  ui['files-summary'].textContent = `${number.format(state.report.filesScanned)} files considered · ${number.format(state.report.skipped.length)} skipped`;
  ui['skipped-count'].textContent = `(${number.format(state.report.skipped.length)})`;
  const skippedRows = document.createDocumentFragment();
  for (const skip of state.report.skipped) {
    const row = document.createElement('tr');
    row.append(cell(skip.fileName), cell(skip.reason));
    skippedRows.append(row);
  }
  ui['skipped-body'].replaceChildren(skippedRows);
  ui['skipped-empty'].hidden = state.report.skipped.length !== 0;
  ui.report.hidden = false;
  renderFilteredReport();
  if (!state.report.matches.length) selectTab('skipped-tab');
}

function renderFilteredReport() {
  const ownerId = ui['owner-filter'].value;
  const matches = state.report.matches.filter((match) => !ownerId || caseKey(match.ownerId) === ownerId)
    .sort((a, b) => (Date.parse(b.matchDate) || 0) - (Date.parse(a.matchDate) || 0));
  state.visibleMatches = matches;
  if (!matches.includes(state.selectedMatch)) state.selectedMatch = matches[0] ?? null;
  ui['owner-total'].textContent = summaryCount(matches, (player) => player.isOwner);
  ui['teammate-total'].textContent = summaryCount(matches, (player) => !player.isOwner, true);
  ui['squad-total'].textContent = summaryCount(matches, () => true, true);
  ui['match-total'].textContent = number.format(matches.length);
  ui['owners-summary'].textContent = [...new Set(matches.map((match) => match.ownerName))].join(', ') || 'No included replay owners';
  const rows = document.createDocumentFragment();
  matches.forEach((match, index) => {
    const counts = matchCounts(match);
    const row = document.createElement('tr');
    row.dataset.index = String(index);
    row.classList.toggle('selected', match === state.selectedMatch);
    row.setAttribute('aria-selected', String(match === state.selectedMatch));
    const dateCell = cell('');
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'match-pick';
    button.textContent = formatDate(match.matchDate);
    button.setAttribute('aria-label', `Show squad for ${match.ownerName}, ${formatDate(match.matchDate)}`);
    dateCell.append(button);
    row.append(dateCell, cell(String(match.mode ?? '').replace('Battle Royale', 'BR')), cell(match.ownerName),
      cell(formatCount(counts.owner, match.coverage), 'number'), cell(formatCount(counts.teammates, match.coverage), 'number'), cell(formatCount(counts.squad, match.coverage), 'number'),
      cell(match.coverage), cell(String(match.filePath ?? '').split(/[\\/]/).pop()));
    rows.append(row);
  });
  ui['matches-body'].replaceChildren(rows);
  ui['matches-empty'].hidden = matches.length !== 0;
  renderSquad();
  renderPlayerTotals(matches);
}

function renderSquad() {
  const match = state.selectedMatch;
  if (!match) {
    ui['selected-match-summary'].textContent = 'No eligible match is selected.';
    ui['squad-body'].replaceChildren();
    return;
  }
  ui['selected-match-summary'].textContent = `${formatDate(match.matchDate)} · ${match.playlist} · ${match.coverage}`;
  const rows = document.createDocumentFragment();
  for (const player of [...(match.players ?? [])].sort((a, b) => Number(b.isOwner) - Number(a.isOwner))) {
    const row = document.createElement('tr');
    row.append(cell(player.name), cell(player.isOwner ? 'Replay owner' : 'Teammate'), cell(formatCount(player.chests, player.coverage), 'number'), cell(player.coverage), cell(player.playerId));
    rows.append(row);
  }
  ui['squad-body'].replaceChildren(rows);
}

function renderPlayerTotals(matches) {
  const groups = new Map();
  for (const match of matches) for (const player of match.players ?? []) {
    const key = caseKey(player.playerId);
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(player);
  }
  const sorted = Array.from(groups.values()).sort((a, b) => Number(b.some((player) => player.isOwner)) - Number(a.some((player) => player.isOwner)) || String(a[0].name).localeCompare(String(b[0].name)));
  const rows = document.createDocumentFragment();
  for (const players of sorted) {
    const owners = players.filter((player) => player.isOwner).length;
    const hasData = players.filter((player) => knownCount(player.chests) !== null).length;
    const partial = players.some((player) => knownCount(player.chests) === null || !completeCoverage(player.coverage));
    const count = hasData === 0 ? 'Unknown' : number.format(players.reduce((sum, player) => sum + (knownCount(player.chests) ?? 0), 0)) + (partial ? '+' : '');
    const coverage = partial ? `${number.format(hasData)} of ${number.format(players.length)} matches have chest opening counts; some openings may be missing` : 'Complete chest data';
    const row = document.createElement('tr');
    row.append(cell(players[0].name), cell(owners === players.length ? 'Replay owner' : owners ? 'Owner / teammate' : 'Teammate'), cell(number.format(players.length), 'number'), cell(count, 'number'), cell(coverage), cell(players[0].playerId));
    rows.append(row);
  }
  ui['players-body'].replaceChildren(rows);
}

function selectTab(id) {
  for (const tab of document.querySelectorAll('[role=tab]')) {
    const selected = tab.id === id;
    tab.setAttribute('aria-selected', String(selected));
    tab.tabIndex = selected ? 0 : -1;
    byId(tab.getAttribute('aria-controls')).hidden = !selected;
  }
}

function csvEscape(value) {
  let text = String(value ?? '');
  if (/^[\t\r\n]|^\s*[=+\-@]/.test(text)) text = `'${text}`;
  return `"${text.replaceAll('"', '""')}"`;
}
function exportCsv() {
  if (!state.report || state.busy) return;
  const lines = [['Match date', 'Mode', 'Playlist', 'Match ID', 'Replay file', 'Replay owner', 'Player', 'Player ID', 'Role', 'Chests opened', 'Owner chests opened', 'Teammate chests opened', 'Squad chests opened', 'Coverage']];
  for (const match of state.visibleMatches) {
    const counts = matchCounts(match);
    for (const player of match.players ?? []) lines.push([
      match.matchDate, match.mode, match.playlist, match.matchId, String(match.filePath ?? '').split(/[\\/]/).pop(), match.ownerName,
      player.name, player.playerId, player.isOwner ? 'Replay owner' : 'Teammate', knownCount(player.chests) ?? 'Unknown',
      counts.owner ?? 'Unknown', counts.teammates ?? 'Unknown', counts.squad ?? 'Unknown', `${player.coverage}; ${match.coverage}`
    ]);
  }
  const url = URL.createObjectURL(new Blob(['\uFEFF' + lines.map((line) => line.map(csvEscape).join(',')).join('\r\n')], { type: 'text/csv;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = `Fortnite-chests-since-${state.reportSince}.csv`;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

ui['choose-folder'].addEventListener('click', chooseFolder);
ui['use-saved-folder'].addEventListener('click', useSavedFolder);
ui['copy-path'].addEventListener('click', copyFolderPath);
ui['choose-files'].addEventListener('click', () => ui['files-input'].click());
ui['folder-input'].addEventListener('change', () => acceptFiles(ui['folder-input'].files, true));
ui['files-input'].addEventListener('change', () => acceptFiles(ui['files-input'].files, false));
ui.since.addEventListener('change', () => { if (!state.busy) filterFiles(true); });
ui['files-body'].addEventListener('change', (event) => {
  const checkbox = event.target;
  if (!(checkbox instanceof HTMLInputElement) || checkbox.type !== 'checkbox' || state.busy || state.choosing) return;
  if (checkbox.checked) state.selected.add(checkbox.dataset.key); else state.selected.delete(checkbox.dataset.key);
  updateSelection();
});
ui['select-all'].addEventListener('click', () => {
  if (state.busy || state.choosing) return;
  const clear = state.eligible.every((file) => state.selected.has(fileKey(file)));
  for (const file of state.eligible) { if (clear) state.selected.delete(fileKey(file)); else state.selected.add(fileKey(file)); }
  for (const checkbox of ui['files-body'].querySelectorAll('input[type=checkbox]:not(:disabled)')) checkbox.checked = !clear;
  updateSelection();
});
ui.scan.addEventListener('click', scan);
ui.cancel.addEventListener('click', () => {
  destroyWorker();
  setBusy(false);
  ui.progress.hidden = true;
  setStatus(state.report ? 'Scan cancelled. The previous completed report is still available.' : 'Scan cancelled. Your file selection is ready for another scan.');
});
ui['owner-filter'].addEventListener('change', () => { if (state.report) renderFilteredReport(); });
ui['matches-body'].addEventListener('click', (event) => {
  const row = event.target.closest('tr[data-index]');
  if (!row) return;
  state.selectedMatch = state.visibleMatches[Number(row.dataset.index)];
  for (const matchRow of ui['matches-body'].children) {
    const selected = matchRow === row;
    matchRow.classList.toggle('selected', selected);
    matchRow.setAttribute('aria-selected', String(selected));
  }
  renderSquad();
});
for (const tab of document.querySelectorAll('[role=tab]')) {
  tab.addEventListener('click', () => selectTab(tab.id));
  tab.addEventListener('keydown', (event) => {
    const tabs = Array.from(document.querySelectorAll('[role=tab]'));
    let next = tabs.indexOf(tab);
    if (event.key === 'ArrowRight') next = (next + 1) % tabs.length;
    else if (event.key === 'ArrowLeft') next = (next + tabs.length - 1) % tabs.length;
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = tabs.length - 1;
    else return;
    event.preventDefault();
    selectTab(tabs[next].id);
    tabs[next].focus();
  });
}
ui.export.addEventListener('click', exportCsv);
window.addEventListener('beforeunload', () => destroyWorker());
restoreSavedFolder();
