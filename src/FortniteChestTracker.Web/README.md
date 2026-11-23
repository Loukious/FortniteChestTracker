# Browser chest tracker

The chest tracker runs the replay parser in browser workers using .NET WebAssembly.
It produces a report for the replay owner and teammates in standard public Battle
Royale and Zero Build matches, with recorded treasure rewards, owner filtering,
and CSV download. Release
builds compile the parser ahead of time to improve replay parsing speed.

## Saved match history

**Include saved match history** is enabled by default. Completed results are stored
in IndexedDB using match ID plus the replay owner's player ID, retaining squad
counts and rewards without replay bytes. Copies and rescans update one result;
an unreadable or shorter recording does not replace a better stored result.
History loads without selecting replay files and survives replay rotation and
parser updates. Date, mode, and owner filters hide results without deleting them.

On the first visit after this update, supported results already in the browser's
replay cache are imported into history. Matches never scanned or cached cannot
be recovered after their files disappear. View preferences use local storage.

**Export history backup** downloads a JSON backup. **Import history backup** merges
it by the same IDs, so importing twice does not add counts twice. History is local
to this browser; use a backup when moving browsers or clearing site data.
**Clear saved history** requires confirmation and prevents old parse-cache entries
from automatically restoring the cleared total. If browser storage is blocked,
results remain available for the visit and can still be exported.

The match table displays at most the latest 500 matches; totals and CSV exports
include the full filtered history.

## Choose your replays

Select **Choose replay folder** on your first visit. Fortnite's usual Windows replay
folder is:

```text
%LOCALAPPDATA%\FortniteGame\Saved\Demos
```

Use **Copy folder path**, then paste the path into the folder picker's address bar.
**Choose replay folder** uses the standard browser picker for Demos inside AppData.
The browser may remember its last location; the site requires a new selection on
later visits. Firefox and other browsers can also use **Choose files instead**.

The optional **Choose and remember folder** button uses Chrome/Edge's persistent
folder access. Chrome and Edge can block it for protected AppData folders with a
system-files message; use the primary folder button for Demos. To remember a
folder, select a copy of your replays outside AppData. **Use saved replay folder**
refreshes that folder after read access is granted.

Only `.replay` files are listed. Files last modified before the selected start date
are skipped before their contents are read. Browsers provide the last-modified
timestamp, so this prefilter does not use the Windows file creation date. Selected
newer files are also checked against the recording timestamp and actual match date
inside the replay. The default start date is November 1, 2026, inclusive.

Check **Include other Battle Royale modes** before scanning to include Respawn
Rumble, Reload, OG, ranked, and other Battle Royale playlists. It is off by default.
Recorded chest counts and treasure rewards from those matches use the same report
and CSV export. The completed report states which mode setting was used.

Select the files to scan, then choose **Scan selected replays**. The parser loads
when scanning starts. File contents are processed locally and are never uploaded.
The app uses one to four workers according to the browser's reported CPU cores and
device memory. Each replay stays in one worker. Cancel stops all workers
and preserves the previous completed report.

Large scans recycle parser workers after small batches of uncached files to limit
WebAssembly memory use. A memory failure retries the interrupted file in a fresh
parser while retaining completed results. A file that fails twice is listed under
**Skipped files** so other recordings can finish. A failed scan explicitly labels
any retained report as the previous completed result.

Completed per-file results are cached locally in IndexedDB. The cache contains
report metadata such as player names, IDs, chest counts, recorded rewards, and skip reasons, without
storing replay bytes. Eligible files are read and hashed with SHA256 before cache
lookup, even on repeat scans, so changed contents cannot reuse an old result. Cache
keys also include the parser version, selected start date, mode setting, and browser time zone.
Live recordings and read failures are not cached. Scanning still works if browser
storage is unavailable.

**Opened** is one count combining normal openings and destroyed unopened chests
attributed to players in the replay. An explicit recorded reset allows a chest to
contribute again when it is reopened or destroyed. Repeated updates within a reset
cycle count once, including placed chests that reappear with another actor GUID.
Counts are the minimum confirmed by the recording; destruction without supported
player attribution may be missing. **+** means additional chests may be missing,
especially those by distant teammates.
**Unknown** means unavailable data and is excluded from totals. Unsupported modes,
older recordings, duplicate matches, and unreadable files appear under **Skipped
files**. The browser app uses the managed replay decompressor and requires no
Windows game DLL or local server component.

## Recorded treasure rewards

The **Rewards** tab shows successful treasure results and recorded treasure receipts with their player,
reward category, tier, match date, replay time, and source file. It includes the
replay owner, teammates, other recorded players, and unidentified recipients.
Nearby players outside the squad, including opponents, appear as **Other player**
when the replay captures their reward. This label describes squad membership;
the app does not infer the recipient's distance from the owner.
The replay-owner filter selects which recordings are included; it does not limit
the reward recipients to the owner. Each included replay contributes its own
reward records.

Major/Supreme prize categories can be detected when the recording contains the
treasure animation actor and its explicit successful result. This includes gold
outfits, Pirate Shop Access, gold car decals, pickaxes/back bling, Jam Tracks, and
loading screens. Unknown prize IDs remain visible as unrecognized treasures.
Categories describe the event reward; they do not identify the exact weekly
cosmetic or independently confirm a final locker transaction.

An explicitly replicated treasure-handler ability grant adds a **Treasure (unknown
type)** receipt when no unique successful result for the same recipient is nearby.
The **Record** column distinguishes receipts from successful prize results. A
receipt identifies who received a treasure, and can come from a shared teammate
reward; it does not identify the chest's opener. A generic handler does not reveal
the prize category, so unknown receipts may include Gizmos or unrecorded Major/Supreme
rewards. Possession, use, and the first appearance of an ability class are not counted
as awards. Repeated ability updates do not add another receipt.

A unique receipt and successful result for the same recipient within five replay
seconds are shown once, with the identified prize taking priority. Ambiguous nearby
records remain separate. Counts describe recorded observations, rather than a
complete locker transaction history. Coverage is incomplete: distant rewards may be
absent, and **None recorded** does not mean the player received no reward. Reward
decode issues appear in the coverage note without changing chest counts.

Match, squad, and player tables include recorded-reward counts. CSV exports retain
the original chest columns and append a row type, reward fields, and reward evidence. **Reward**
rows leave all chest-count columns empty, so summing chest columns remains safe.
**Chest** rows carry the same combined **Opened** count in the existing chest
columns. Updated parsing uses a new cache version, so a repeat scan refreshes
earlier results that missed reset cycles or duplicated reappearing placed chests.

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
