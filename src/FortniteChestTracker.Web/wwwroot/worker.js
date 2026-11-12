import { dotnet } from './_framework/dotnet.js';
import { createReplayCache, PARSER_CACHE_VERSION } from './replay-cache.js';

const { readCachedResult, writeCachedResult } = createReplayCache({
    parserVersion: `${PARSER_CACHE_VERSION}:${Intl.DateTimeFormat().resolvedOptions().timeZone}`
});

let parser;
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
    try {
        await ready;
        if (event.data.type === 'build-report') {
            const report = JSON.parse(parser.BuildReport(JSON.stringify(event.data.matches ?? []),
                JSON.stringify(event.data.skipped ?? []), event.data.totalFiles));
            self.postMessage({ type: 'complete', report, requestId });
            return;
        }
        const { files, filePaths = [], since, totalFiles } = event.data;
        const skipped = [...(event.data.skipped ?? [])];
        const matches = [];
        let completed = skipped.length;
        for (const [index, file] of files.entries()) {
            const fileName = filePaths[index] ?? (file.webkitRelativePath || file.name);
            self.postMessage({ type: 'progress', completed, total: totalFiles, fileName, requestId });
            try {
                const buffer = await file.arrayBuffer();
                const digest = await crypto.subtle.digest('SHA-256', buffer);
                const contentHash = Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
                let result = await readCachedResult(contentHash, since);
                if (result) {
                    if (result.match) {
                        result.match.filePath = fileName;
                        result.match.fileName = fileName.split(/[\\/]/).pop();
                    }
                    if (result.skipped) result.skipped.fileName = fileName;
                } else {
                    result = JSON.parse(parser.ScanReplay(new Uint8Array(buffer), fileName, since, contentHash));
                    await writeCachedResult(contentHash, since, result);
                }
                if (result.match) matches.push(result.match);
                if (result.skipped) skipped.push(result.skipped);
            } catch (error) {
                skipped.push({ fileName, reason: `Could not read replay: ${error.message ?? error}` });
            }
            completed++;
            self.postMessage({ type: 'progress', completed, total: totalFiles, fileName, requestId });
        }
        const report = JSON.parse(parser.BuildReport(JSON.stringify(matches), JSON.stringify(skipped), totalFiles));
        self.postMessage({ type: 'complete', report, requestId });
    } catch (error) {
        self.postMessage({ type: 'error', message: String(error.message ?? error), requestId });
    }
});
