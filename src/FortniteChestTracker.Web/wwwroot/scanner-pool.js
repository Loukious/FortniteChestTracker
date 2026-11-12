// Keep a replay in one worker. Each parser has its own WebAssembly heap.
export class ReplayScanner extends EventTarget {
  constructor({ Worker = globalThis.Worker,
    hardwareConcurrency = globalThis.navigator?.hardwareConcurrency,
    deviceMemory = globalThis.navigator?.deviceMemory } = {}) {
    super();
    this.Worker = Worker;
    const cores = Number.isFinite(hardwareConcurrency) && hardwareConcurrency > 0
      ? Math.floor(hardwareConcurrency) : 2;
    const memoryLimit = Number.isFinite(deviceMemory) && deviceMemory > 0
      ? deviceMemory <= 2 ? 1 : deviceMemory < 8 ? 2 : 4 : 2;
    this.limit = Math.max(1, Math.min(4, cores - 1, memoryLimit));
    this.workers = [];
    this.active = null;
    this.nextRequestId = 0;
    this.readyEmitted = false;
    this.terminated = false;
  }

  postMessage(data) {
    if (this.terminated || data?.type !== 'scan') return;
    if (!Array.isArray(data.files)) throw new TypeError('Replay files must be an array.');
    // A replacement scan must not race another scan in the same parser.
    if (this.active) this.retireWorkers();
    const count = Math.max(1, Math.min(this.limit, data.files.length));
    const requestId = ++this.nextRequestId;
    const initialSkipped = [...(data.skipped ?? [])];
    const groups = partition(data.files, data.filePaths ?? [], count);
    this.active = {
      requestId, groups, initialSkipped,
      totalFiles: Number.isFinite(data.totalFiles) && data.totalFiles >= 0
        ? data.totalFiles : data.files.length + initialSkipped.length,
      completed: groups.map(() => 0), reports: new Map(), stage: 'scan', fileName: ''
    };
    try {
      this.resizeWorkers(count);
      if (count === 1) {
        // Preserve the original worker's scan/report protocol, including its skips.
        this.workers[0].worker.postMessage({ ...data, requestId });
      } else {
        groups.forEach((group, index) => this.workers[index].worker.postMessage({
          ...data, requestId, files: group.files, filePaths: group.paths,
          skipped: [], totalFiles: group.files.length
        }));
      }
      this.notifyReady();
    } catch (error) {
      this.fail(error, 'error');
    }
  }

  terminate() {
    if (this.terminated) return;
    this.terminated = true;
    this.active = null;
    this.retireWorkers();
  }

  retireWorkers() {
    const retired = this.workers;
    this.workers = [];
    for (const record of retired) record.worker.terminate();
  }

  resizeWorkers(count) {
    while (this.workers.length > count) this.workers.pop().worker.terminate();
    while (this.workers.length < count) {
      const worker = new this.Worker('./worker.js', { type: 'module' });
      const record = { worker, ready: false, index: this.workers.length };
      this.workers.push(record);
      worker.addEventListener('message', event => this.receive(record, event.data));
      worker.addEventListener('error', event => {
        if (!this.isCurrentWorker(record)) return;
        event.preventDefault?.();
        this.fail(event.error ?? new Error(event.message || 'The replay reader could not start.'), 'error');
      });
    }
  }

  isCurrentWorker(record) {
    return !this.terminated && this.workers[record.index] === record;
  }

  notifyReady() {
    if (!this.terminated && !this.readyEmitted && this.workers.length && this.workers.every(record => record.ready)) {
      this.readyEmitted = true;
      this.emitMessage({ type: 'ready' });
    }
  }

  receive(record, message) {
    if (!this.isCurrentWorker(record) || !message) return;
    if (message.type === 'ready') {
      record.ready = true;
      this.notifyReady();
      return;
    }
    const scan = this.active;
    // Runtime startup errors have no request ID; scan errors are correlated.
    if (message.type === 'error' && message.requestId == null) {
      this.fail(new Error(message.message || 'The replay reader could not start.'), 'message');
      return;
    }
    if (!scan || message.requestId !== (scan.stage === 'report' ? scan.reportRequestId : scan.requestId)) return;
    if (message.type === 'error') {
      this.fail(new Error(message.message || 'The replay reader could not complete the scan.'), 'message');
      return;
    }
    if (message.type === 'progress') {
      if (scan.stage !== 'scan' || scan.reports.has(record.index)) return;
      if (this.workers.length === 1) {
        this.emitMessage(message);
      } else {
        const completed = Number(message.completed);
        if (Number.isFinite(completed)) {
          scan.completed[record.index] = Math.max(scan.completed[record.index],
            Math.min(scan.groups[record.index].files.length, Math.max(0, completed)));
        }
        scan.fileName = message.fileName ?? '';
        this.emitProgress(scan);
      }
      return;
    }
    if (message.type !== 'complete') return;
    if (scan.stage === 'report' && record.index !== 0 || scan.stage === 'scan' && scan.reports.has(record.index)) return;
    if (!Array.isArray(message.report?.matches) || !Array.isArray(message.report?.skipped)) {
      this.fail(new Error('The replay reader returned an incomplete report.'), 'message');
      return;
    }
    if (this.workers.length === 1 || scan.stage === 'report') {
      this.active = null;
      this.emitMessage(message);
      return;
    }
    scan.reports.set(record.index, message.report);
    scan.completed[record.index] = scan.groups[record.index].files.length;
    this.emitProgress(scan);
    if (this.active !== scan || this.terminated) return;
    if (scan.reports.size === this.workers.length) {
      scan.stage = 'report';
      scan.reportRequestId = ++this.nextRequestId;
      const reports = scan.groups.map((_, index) => scan.reports.get(index));
      try {
        // Run the shared C# builder once across all workers so duplicate matches
        // retain the same original/longest-recording preference as a serial scan.
        this.workers[0].worker.postMessage({
          type: 'build-report', requestId: scan.reportRequestId,
          matches: reports.flatMap(report => report.matches),
          skipped: [...scan.initialSkipped, ...reports.flatMap(report => report.skipped)],
          totalFiles: scan.totalFiles
        });
      } catch (error) {
        this.fail(error, 'error');
      }
    }
  }

  emitProgress(scan) {
    this.emitMessage({ type: 'progress', requestId: scan.requestId,
      completed: scan.initialSkipped.length + scan.completed.reduce((sum, count) => sum + count, 0),
      total: scan.totalFiles, fileName: scan.fileName });
  }

  emitMessage(data) {
    this.dispatchEvent(new MessageEvent('message', { data }));
  }

  fail(error, type) {
    if (this.terminated) return;
    const message = String(error?.message ?? error);
    this.terminate();
    if (type === 'message') {
      this.emitMessage({ type: 'error', message });
    } else {
      const event = new Event('error', { cancelable: true });
      Object.defineProperties(event, { message: { value: message }, error: { value: error } });
      this.dispatchEvent(event);
    }
  }
}

function partition(files, paths, count) {
  const groups = Array.from({ length: count }, () => ({ files: [], paths: [], size: 0 }));
  const ordered = files.map((file, index) => ({ file, index,
    size: Number.isFinite(file.size) && file.size > 0 ? file.size : 0,
    path: paths[index] ?? (file.webkitRelativePath || file.name)
  })).sort((left, right) => right.size - left.size || left.index - right.index);
  for (const item of ordered) {
    const group = groups.reduce((best, current) =>
      current.size < best.size || current.size === best.size && current.files.length < best.files.length ? current : best);
    group.files.push(item.file);
    group.paths.push(item.path);
    group.size += item.size;
  }
  return groups;
}
