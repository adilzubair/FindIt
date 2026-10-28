# Demo video

How `docs/media/findit-demo.mp4` and its README preview are made. Everything on screen is the real app, recorded live on a generated demo drive, so the video can be re-recorded whenever the interface changes.

| Step | Folder | What it does |
|---|---|---|
| 1. Build the drive | `library/` | Creates *Portable SSD*: 59 photos and 8 videos cut from Mixkit clips, 6 spoken voice memos, 15 fictional documents including a scanned contract |
| 2. Prepare and check | `tools/DemoDrive` | Prepares the drive with the real worker, then runs every storyboard search and fails if the expected file is not first |
| 3. Record | `recorder/` | Opens the app, plays each take from `storyboard.json` and records only its window at 60 fps, logging every action with its time and position |
| 4. Edit | `edit/` | A [Remotion](https://www.remotion.dev/) project that cuts the takes, frames them with a camera driven by the logs, draws the pointer, and adds captions, music and sound |

None of the media is committed. Every download is pinned by URL and SHA-256 (`library/lock.json`, `edit/assets.json`), and generated files live outside the repository by default (`%USERPROFILE%\FindItDemo`).

## Requirements

Windows 11 with an NVIDIA GPU (the recorder encodes with NVENC), the repository's native runtime (`scripts/Fetch-Dependencies.ps1` / `Build.ps1`), [uv](https://docs.astral.sh/uv/), Node.js 20 or later, and Google Chrome or Microsoft Edge for printing the documents.

## Steps

From the repository root, after building FindIt (`dotnet build FindIt.slnx -c Release`):

```powershell
$drive = "$env:USERPROFILE\FindItDemo\Portable SSD"

# 1. The demo drive (about 300 MB; downloads about 2 GB into the cache once)
uv run demo-video/library/build.py --out $drive

# 2. Prepare it and check the storyboard's searches
dotnet build demo-video/tools/DemoDrive -c Release
$env:FINDIT_WORKER_PATH = "$PWD\src\FindIt.Worker\bin\Release\net10.0\FindIt.Worker.exe"
demo-video/tools/DemoDrive/bin/Release/net10.0/DemoDrive.exe prepare $drive
demo-video/tools/DemoDrive/bin/Release/net10.0/DemoDrive.exe check $drive demo-video/storyboard.json
demo-video/tools/DemoDrive/bin/Release/net10.0/DemoDrive.exe clear-recent $drive

# 3. Record the takes. Leave the PC alone while it runs: a FindIt window opens for each take.
dotnet build demo-video/recorder -c Release
demo-video/recorder/bin/Release/net10.0-windows/Recorder.exe `
  --app src/FindIt.App/bin/Release/net10.0-windows/FindIt.App.exe --drive $drive `
  --storyboard demo-video/storyboard.json --out "$env:USERPROFILE\FindItDemo\takes" --input real

# 4. Edit and render
cd demo-video/edit
npm ci
node prepare.mjs
npx remotion studio          # preview and adjust
npx remotion render Demo out/findit-demo.mp4 --crf=21

# 5. Publish: docs/media/findit-demo.mp4, the README's WebP preview and the credits
node export.mjs
cd ../..
uv run demo-video/library/credits.py
```

`--input uia` records without touching the mouse or keyboard, through UI Automation alone; it is handy while working on the storyboard, but it shows no hover states or caret. `--input real` moves the pointer and types, so the takes look like a person using the app.

Run the recorder with the display at a scale of 150% or more: the takes are recorded at the window's physical size, and the edit zooms in up to about 2×.

## Changing the video

- **Searches and takes:** `storyboard.json`. Run `DemoDrive check` after changing a search; a take only works if its result is first.
- **Cut, camera, captions and sound:** `edit/src/Demo.tsx`. Shots refer to events in the take logs (`"click:open+300"`, `"mark:results"`) and to logged elements (`"MomentsList"`, `"tile0|tile11"`), so a re-recorded take with different timing still lines up.
- **Drive contents:** `library/sources.json`, `library/memos.json`, `library/documents.json` and the HTML in `library/documents/`. `library/credits.py` lists every Mixkit clip and sound in `docs/media/CREDITS.md`; rerun it after changing them.

## Licences

The clips, music and sound effects are from [Mixkit](https://mixkit.co/license/) and may be used in this video but not redistributed on their own, which is why they are downloaded rather than committed. The voices are [Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M) (Apache-2.0). Remotion is free for individuals and small teams; larger companies need a [company licence](https://www.remotion.dev/license) to render it.
