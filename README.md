# Fortnite Chest Tracker

[Open the chest tracker](https://loukious.github.io/FortniteChestTracker/)

Count chests opened by the replay owner and their squad in regular Fortnite Battle
Royale and Zero Build matches. Choose your replay folder, select a start date, and
scan the files you need. Replays are processed locally in your browser and are
never uploaded.

The app skips older files before reading their contents, uses up to four workers,
and caches completed results locally for fast repeat scans. Counts come from
completed chest searches attributed to players. A **+** means the recording may
miss additional openings, especially those by distant teammates.

Fortnite's usual Windows replay folder is
`%LOCALAPPDATA%\FortniteGame\Saved\Demos`. The page provides a copy button for
this path. Supported browsers can remember the folder after you choose it once.

## Development

Install the .NET 10 SDK and its WebAssembly tools, then publish:

```sh
dotnet workload install wasm-tools
dotnet publish src/FortniteChestTracker.Web -c Release -o artifacts/FortniteChestTracker.Web
python -m http.server 8765 --bind 127.0.0.1 --directory artifacts/FortniteChestTracker.Web/wwwroot
```

Open `http://127.0.0.1:8765/`. Release builds compile the parser ahead of time to
WebAssembly. Run the browser tests with Node.js:

```sh
node --test src/FortniteChestTracker.Web/tests/*.test.mjs
```

See the [browser guide](src/FortniteChestTracker.Web/README.md) for folder access,
date filtering, caching, report coverage, and measured performance.

## GitHub Pages

The [deployment workflow](.github/workflows/chest-tracker-pages.yml) builds and
deploys the static browser app after relevant changes reach `main`. It can also
be run manually from the Actions tab. Only the published `wwwroot` is deployed.

## Credits and license

Built on the MIT-licensed
[FortniteReplayDecompressor](https://github.com/Shiqan/FortniteReplayDecompressor)
parser by Shiqan. The managed decompressor is the C# Ooz port by
[SL-x-TnT](https://github.com/SL-x-TnT), based on
[powzix/ooz](https://github.com/powzix/ooz).

The combined browser application is distributed under GNU GPLv3 or later because
it includes the GPL-licensed Kraken decompressor. See [COPYING](COPYING) and
[third-party notices](THIRD_PARTY_NOTICES.md). The reader's upstream MIT copyright
notice is retained in [LICENSE](LICENSE).

This repository contains the browser tracker and required parser source. It
includes no replay recordings, private reports, session notes, or game DLLs.
