# Browser chest tracker

The chest tracker runs the replay parser in browser workers using .NET WebAssembly.
It produces a report for the replay owner and teammates in standard public Battle
Royale and Zero Build matches, with owner filtering and CSV download. Release
builds compile the parser ahead of time to improve replay parsing speed.

## Choose your replays

Select **Choose replay folder** on your first visit. Fortnite's usual Windows replay
folder is:

```text
%LOCALAPPDATA%\FortniteGame\Saved\Demos
```

Use **Copy folder path**, then paste the path into the folder picker's address bar.
Chrome and Edge can remember the chosen folder for later visits. **Use saved replay
folder** refreshes its file list after the browser grants read access; subsequent
folder selections also start there. Firefox and other browsers use the standard
folder picker or **Choose files instead**.

Only `.replay` files are listed. Files last modified before the selected start date
are skipped before their contents are read. Browsers provide the last-modified
timestamp, so this prefilter does not use the Windows file creation date. Selected
newer files are also checked against the recording timestamp and actual match date
inside the replay. The default start date is November 1, 2026, inclusive.

Select the files to scan, then choose **Scan selected replays**. The parser loads
when scanning starts. File contents are processed locally and are never uploaded.
The app uses one to four workers according to the browser's reported CPU cores and
device memory. Each replay stays in one worker. Cancel stops all workers
and preserves the previous completed report.

Completed per-file results are cached locally in IndexedDB. The cache contains
report metadata such as player names, IDs, chest counts, and skip reasons, without
storing replay bytes. Eligible files are read and hashed with SHA256 before cache
lookup, even on repeat scans, so changed contents cannot reuse an old result. Cache
keys also include the parser version, selected start date, and browser time zone.
Live recordings and read failures are not cached. Scanning still works if browser
storage is unavailable.

Counts represent chests players actually opened. **+** means additional openings
may be missing from the recording, especially those by distant teammates.
**Unknown** means unavailable data and is excluded from totals. Unsupported modes,
older recordings, duplicate matches, and unreadable files appear under **Skipped
files**. The browser app uses the managed replay decompressor and requires no
Windows game DLL or local server component.

## Measured scan example

One local Chrome test, with eight reported logical CPU cores and 8 GB reported
device memory, selected 69 replay files from one folder. The date prefilter skipped
34 files without reading them; 35 candidates produced 26 eligible matches, with
eight Creative recordings and one live recording excluded.

| Scan | Time |
| --- | ---: |
| Previous AOT parser with one worker | 90.52 seconds |
| Optimized parser with four workers and an empty result cache | 16.01 seconds |
| Repeat with cached results, after all parser workers restarted | 1.09 seconds |

Timings include candidate-file reads and SHA256 hashing. The repeat used the same
selected File objects; test-input setup time was excluded. Match and skip details
were equivalent across runs. Other replay folders, hardware, and browsers can give
different timings.

## Build and preview

Install the .NET 10 SDK and the WebAssembly workload, then publish from the repository
root. The first Release build can take several minutes because it compiles the
parser to WebAssembly.

```powershell
dotnet workload install wasm-tools
dotnet publish src/FortniteChestTracker.Web -c Release -o artifacts/FortniteChestTracker.Web
python -m http.server 8765 --bind 127.0.0.1 --directory artifacts/FortniteChestTracker.Web/wwwroot
```

Open `http://127.0.0.1:8765/`. Python 3 is needed only for this preview server. Serve
the published `wwwroot` directory over HTTP or HTTPS so the worker and WebAssembly
runtime can load. Relative asset URLs also support a GitHub Pages repository path.
When a change alters parsing, eligibility, ownership, or chest counts, bump
`PARSER_CACHE_VERSION` in `wwwroot/replay-cache.js` to invalidate earlier results.

## Deploy to GitHub Pages

In the destination repository's **Settings → Pages**, set **Source** to **GitHub
Actions**. The prepared
[Chest Tracker Pages workflow](../../.github/workflows/chest-tracker-pages.yml)
publishes after relevant changes reach `main`, or when started through **Actions →
Deploy Chest Tracker to GitHub Pages → Run workflow**.

The workflow installs .NET 10 and `wasm-tools`, publishes the Release browser
project, and uploads **only** `artifacts/FortniteChestTracker.Web/wwwroot`. Keep its
`_framework` directory, which contains the generated runtime and parser, together
with `index.html`, `app.js`, `app.css`, `scanner-pool.js`, `worker.js`, and
`replay-cache.js`. The workflow creates
`.nojekyll` in that output to preserve underscore directories when serving the
static files. Replay recordings, reports, desktop editor artifacts, and proprietary
Windows DLLs are not included in the site.

The deployment job publishes that static artifact and exposes its Pages URL in the
workflow's `github-pages` environment. Preparing these files does not push commits
or start a deployment.
