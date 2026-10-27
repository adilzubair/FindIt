# FindIt website

The public site at [findit.muhamedadil.com](https://findit.muhamedadil.com/). It is plain HTML, CSS and JavaScript modules, with no framework and no server.

The page opens on a working search. A sample drive of 37 files with meaningless names sits dim on a dark wall; asking for something brings the matches forward, lit, each one saying why it matched. Visitors can then drop their own files and search those too. Everything runs in the visitor's browser: files are never uploaded, and the page loads nothing from other origins.

## How the demo works

| Piece | What it does |
|---|---|
| `public/demo/shared.js` | Search logic shared with the indexing tool: chunking, exact-word matching, scoring and match cut-offs |
| `public/demo/worker.js` | Runs [MobileCLIP-S0](https://github.com/apple/ml-mobileclip) with [Transformers.js](https://github.com/huggingface/transformers.js) in a Web Worker: WebGPU for images when available, otherwise WebAssembly |
| `public/demo/engine.js` | Loads the prepared sample drive and prepares visitors' files: photos, one still per second of video, PDF text through [PDF.js](https://mozilla.github.io/pdf.js/), and plain-text files |
| `public/demo/drive.js` | The wall of files, the search box and the file viewer |
| `public/samples/` | The sample drive and its prepared `index.json` |

Pictures and video stills are compared with the query through the image–text model. Documents are split into passages and compared with the query through the same model's text encoder, alongside exact-word matching. Suggested searches are embedded ahead of time, so they answer instantly. A typed search fetches the 43 MB text model on first use, and adding your own photos or videos also fetches the 46 MB image model. The WebAssembly runtime adds 27 MB. Browsers cache all of it.

This is a sketch of FindIt, not the app: the page says so and compares the two.

## Layout

| Path | Contents |
|---|---|
| `public/` | The site as published, apart from the generated parts below |
| `build.mjs` | Assembles `_site/`: copies `public/`, the demo runtime from `node_modules`, the model, the tour video and the favicon |
| `tools/models.mjs` | The demo model, pinned to one Hugging Face revision, and its one-time download into `.cache/` |
| `tools/index-samples.mjs` | Prepares the sample drive into `public/samples/index.json` and a still strip per video |

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

GitHub Pages is free for public repositories. Its published soft limit is 100 GB of bandwidth a month; a visitor who uses every part of the demo downloads about 120 MB once. If the demo ever gets that popular, serve `models/` and `vendor/` from a bucket with free egress and point `worker.js` at it.

## Principles

- Nothing is loaded from other origins. Fonts, code and the model are served from this site, and there are no analytics or cookies. The footer says so, and it should stay true.
- Visitors' files stay in the tab. They are read through object URLs and forgotten when the tab closes or the visitor chooses **Forget these files**.
- Claims match [validation status](../docs/validation.md). Update the Status section when a release gate is passed.

## Credits

- Sample photos: [COCO 2017](https://cocodataset.org/) validation images, each under [CC BY 2.0](https://creativecommons.org/licenses/by/2.0/) by its Flickr author; the full list is in [demo media credits](../docs/media/CREDITS.md). The three sample videos are slow pushes over some of the same photos. The documents are fictional.
- Model: [MobileCLIP-S0](https://huggingface.co/Xenova/mobileclip_s0) by Apple, under the Apple sample code licence served next to it as `models/mobileclip_s0/LICENSE`.
- Runtime: [Transformers.js](https://github.com/huggingface/transformers.js) and [ONNX Runtime Web](https://github.com/microsoft/onnxruntime) (Apache-2.0 and MIT), [PDF.js](https://github.com/mozilla/pdf.js) (Apache-2.0).
- Fonts: [Newsreader](https://github.com/productiontype/Newsreader), [IBM Plex Sans and Mono](https://github.com/IBM/plex), under the SIL Open Font License (texts in `public/fonts/`).
- The tour video and app screenshot show the real FindIt app; see [demo media credits](../docs/media/CREDITS.md).
