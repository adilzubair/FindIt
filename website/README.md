# FindIt website

The public site at [findit.muhamedadil.com](https://findit.muhamedadil.com/). It is plain HTML, CSS and JavaScript modules, with no framework and no server.

The design is deliberately quiet: white space, system fonts (Segoe UI Variable on Windows, SF Pro on Apple devices), the app's blue, and one line of text per section. The page opens on a working search over a sample drive of 37 files with meaningless names. Only the matches appear, each saying why it matched. The search runs in the visitor's browser, and the page loads nothing from other origins. It searches the sample drive only: visitors are never asked for their own files.

## How the demo works

| Piece | What it does |
|---|---|
| `public/demo/shared.js` | Search logic shared with the indexing tool: chunking, exact-word matching, scoring and match cut-offs |
| `public/demo/worker.js` | Runs the text half of [MobileCLIP-S0](https://github.com/apple/ml-mobileclip) with [Transformers.js](https://github.com/huggingface/transformers.js) in a Web Worker, on WebAssembly |
| `public/demo/engine.js` | Loads the prepared sample drive and turns typed searches into vectors |
| `public/demo/drive.js` | The search box, result cards and file viewer |
| `public/samples/` | The sample drive and its prepared `index.json` |

The sample drive is prepared ahead of time with both halves of the model: photos and one still per second of video through the image encoder, document passages through the text encoder. In the browser, only the query needs embedding, so the page ships just the 43 MB text encoder and the 27 MB WebAssembly runtime, fetched on the first typed search and then cached. Suggested searches are embedded ahead of time too, so they answer with no download at all. Exact-word matching runs alongside.

This is a sketch of FindIt, not the app, and the page says so.

## Layout

| Path | Contents |
|---|---|
| `public/` | The site as published, apart from the generated parts below |
| `build.mjs` | Assembles `_site/`: copies `public/`, the runtime from `node_modules`, the text model, the tour video and the favicon |
| `tools/models.mjs` | The demo model, pinned to one Hugging Face revision, and its one-time download into `.cache/` |
| `tools/index-samples.mjs` | Prepares the sample drive into `public/samples/index.json` and a still strip per video, reading PDFs with [PDF.js](https://mozilla.github.io/pdf.js/) |

## Preview

```powershell
cd website
npm ci
node build.mjs
python -m http.server 4317 -d _site
```

Then open <http://localhost:4317>. A search can be linked directly, for example `/?q=elephants+crossing+the+road`.

## Changing the sample drive

Add or replace files under `public/samples/photos`, `videos` or `documents`, then run the indexer. It needs ffmpeg:

```powershell
$env:FFMPEG = 'path\to\ffmpeg.exe'
node website/tools/index-samples.mjs
```

If you add or change a suggested search in `shared.js`, run it again so the suggestion is embedded. The match cut-offs in `MATCH` were measured on this sample drive.

## Deploying

`.github/workflows/website.yml` deploys to GitHub Pages on every push to `main` that touches the site, and whenever a release is published or edited. The deploy runs `node website/build.mjs --release`, which asks the GitHub API for the newest non-draft release with a `FindIt-*-win-x64.zip` asset and rewrites the version, size and date in `index.html`. The model download is cached between runs.

One-time setup:

1. **Repository settings → Pages → Source:** GitHub Actions.
2. **Custom domain:** `findit.muhamedadil.com`, then enable **Enforce HTTPS** once the certificate is issued. `public/CNAME` records the domain, but Pages deployed from Actions reads it from the settings.
3. **DNS:** a `CNAME` record for `findit` pointing to `adilzubair.github.io`.

GitHub Pages is free for public repositories. Its published soft limit is 100 GB of bandwidth a month; a visitor who types their own search downloads about 70 MB once, and one who only clicks suggestions downloads under 10 MB. If the demo ever gets that popular, serve `models/` and `vendor/` from a bucket with free egress and point `worker.js` at it.

## Principles

- Nothing is loaded from other origins. Code and the model are served from this site, text uses system fonts, and there are no analytics or cookies. The footer says so, and it should stay true.
- Keep it short. Each section has a heading and at most one line beneath it; detail lives in the repository docs.
- Claims match [validation status](../docs/validation.md).

## Credits

- Sample drive: travel photos from [COCO 2017](https://cocodataset.org/), each under [CC BY 2.0](https://creativecommons.org/licenses/by/2.0/) by its Flickr author and chosen to show no people. The three videos are slow pushes over further photos from the same set. The documents are fictional. Every image is listed in [samples/CREDITS.md](public/samples/CREDITS.md).
- Model: [MobileCLIP-S0](https://huggingface.co/Xenova/mobileclip_s0) by Apple, under the Apple sample code licence served next to it as `models/mobileclip_s0/LICENSE`.
- Runtime: [Transformers.js](https://github.com/huggingface/transformers.js) and [ONNX Runtime Web](https://github.com/microsoft/onnxruntime) (Apache-2.0 and MIT). The indexer also uses [PDF.js](https://github.com/mozilla/pdf.js) (Apache-2.0).
- The tour video and the Drive-page screenshot show the real FindIt app on its own demo library; see [demo media credits](../docs/media/CREDITS.md).
