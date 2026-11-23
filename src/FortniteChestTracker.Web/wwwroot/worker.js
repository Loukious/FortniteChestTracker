import { dotnet } from './_framework/dotnet.js';
import { createReplayCache, PARSER_CACHE_VERSION } from './replay-cache.js';
import { createMatchHistory } from './match-history.js';

const { readCachedResult, writeCachedResult } = createReplayCache({
    parserVersion: `${PARSER_CACHE_VERSION}:${Intl.DateTimeFormat().resolvedOptions().timeZone}`
});

let parser;
const history = createMatchHistory({ parserVersion: PARSER_CACHE_VERSION });
const ready = (async () => {
    const runtime = await dotnet.create();
    const config = runtime.getConfig();
    const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
    parser = exports.FortniteChestTracker.Web.Program;
    self.postMessage({ type: 'ready' });
})();

ready.catch(error => self.postMessage({ type: 'error', message: String(error.message ?? error) }));

self.addEventListener('message', async event => {
    if (!['scan', 'build-report'].includes(event.data?.type)) return;
    const requestId = event.data.requestId;
    let checkpoint;
    try {
        await ready;
        if (event.data.type === 'build-report') {
            const report = JSON.parse(parser.BuildReport(JSON.stringify(event.data.matches ?? []),
                JSON.stringify(event.data.skipped ?? []), event.data.totalFiles));
            self.postMessage({ type: 'complete', report, requestId });
            return;
        }
        const { files, filePaths = [], since, totalFiles } = event.data;
        const includeOtherModes = event.data.includeOtherModes === true;
        const skipped = [...(event.data.skipped ?? [])];
        const matches = [...(event.data.matches ?? [])];
        let completed = event.data.completed ?? skipped.length;
        let parsedFiles = 0;
        checkpoint = { matches, skipped, fileIndex: 0, completed };
        for (const [index, file] of files.entries()) {
            checkpoint.fileIndex = index;
            checkpoint.completed = completed;
            const fileName = filePaths[index] ?? (file.webkitRelativePath || file.name);
            self.postMessage({ type: 'progress', completed, total: totalFiles, fileName, requestId });
            try {
                const buffer = await file.arrayBuffer();
                const digest = await crypto.subtle.digest('SHA-256', buffer);
                const contentHash = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
                let result = await readCachedResult(contentHash, since, includeOtherModes);
                if (result) {
                    if (result.match) {
                        result.match.filePath = fileName;
                        result.match.fileName = fileName.split(/[\\/]/).pop();
                    }
                    if (result.skipped) result.skipped.fileName = fileName;
                } else {
                    parsedFiles++;
                    result = JSON.parse(parser.ScanReplay(new Uint8Array(buffer), fileName, since, contentHash, includeOtherModes));
                    if (result.skipped && /out.?of.?memory/i.test(result.skipped.reason ?? '')) {
                        self.postMessage({ type: 'parser-crash', message: result.skipped.reason, requestId, ...checkpoint });
                        return;
                    }
                    await writeCachedResult(contentHash, since, result, includeOtherModes);
                }
                if (result.match) {
                    await history.saveMatch(result.match);
                    matches.push(result.match);
                }
                if (result.skipped) skipped.push(result.skipped);
            } catch (error) {
                if (isParserCrash(error)) {
                    self.postMessage({ type: 'parser-crash', message: String(error.message ?? error), requestId, ...checkpoint });
                    return;
                }
                skipped.push({ fileName, reason: `Could not read replay: ${error.message ?? error}` });
            }
            completed++;
            self.postMessage({ type: 'progress', completed, total: totalFiles, fileName, requestId });
            if (parsedFiles >= 4 && index + 1 < files.length) {
                self.postMessage({ type: 'parser-recycle', requestId, matches, skipped, fileIndex: index + 1, completed });
                return;
            }
        }
        checkpoint.fileIndex = files.length;
        checkpoint.completed = completed;
        const report = JSON.parse(parser.BuildReport(JSON.stringify(matches), JSON.stringify(skipped), totalFiles));
        self.postMessage({ type: 'complete', report, requestId });
    } catch (error) {
        if (checkpoint && isParserCrash(error)) {
            self.postMessage({ type: 'parser-crash', message: String(error.message ?? error), requestId, ...checkpoint });
            return;
        }
        self.postMessage({ type: 'error', message: String(error.message ?? error), requestId });
    }
});

function isParserCrash(error) {
    return error?.name === 'RuntimeError' || /memory access out of bounds|out of memory|unreachable/i.test(String(error?.message ?? error));
}
