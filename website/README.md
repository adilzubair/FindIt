# FindIt website

The public site at [findit.muhamedadil.com](https://findit.muhamedadil.com/). It is plain HTML, CSS and one small progressive-enhancement script, with no framework and no dependencies.

The design borrows from a library card catalogue: the drive is the archive and FindIt is its index. Results appear as index cards that say why they matched, the search modes are drawers in a cabinet, and the drive layout is drawn as a labelled manifest.

## Layout

| Path | Contents |
|---|---|
| `public/` | The site as published: `index.html`, `404.html`, `styles.css`, `script.js`, `fonts/`, `media/` |
| `build.mjs` | Assembles `public/` into `_site/` and adds files the repository already keeps elsewhere |
| `_site/` | Build output (ignored by git) |

`build.mjs` copies `docs/media/findit-demo.mp4` and `src/FindIt.App/Assets/FindIt.ico` into the site so they are not duplicated in the repository.

## Preview

```powershell
node website/build.mjs
python -m http.server 4317 -d website/_site
```

Then open <http://localhost:4317>. Any static server works; asset paths are relative.

## Deploying

`.github/workflows/website.yml` deploys to GitHub Pages on every push to `main` that touches the site, and whenever a release is published or edited. The deploy runs `node website/build.mjs --release`, which asks the GitHub API for the newest non-draft release with a `FindIt-*-win-x64.zip` asset and rewrites the version, size and date in `index.html`. The page is written against the release in its `findit-version` meta tag, so it also reads correctly without that step.

One-time setup, already described here so it isn't lost:

1. **Repository settings → Pages → Source:** GitHub Actions.
2. **Custom domain:** `findit.muhamedadil.com`, then enable **Enforce HTTPS** once the certificate is issued. `public/CNAME` records the domain, but Pages deployed from Actions reads it from the settings.
3. **DNS:** a `CNAME` record for `findit` pointing to `adilzubair.github.io`.

## Principles

- Nothing is loaded from other origins: fonts are self-hosted, and there are no analytics or cookies. The footer says so, and it should stay true.
- Every section reads correctly without JavaScript. The script only cycles the hero cards, reveals the filename list, opens the tour video and copies the checksum command, and it respects `prefers-reduced-motion`.
- Light and dark themes follow `prefers-color-scheme`.
- Claims match [validation status](../docs/validation.md). Update the Status section when a release gate is passed.

## Media

The stills in `media/` are frames from the demo video, which shows the real app on a demo library. See [demo media credits](../docs/media/CREDITS.md). `og.jpg` is the social preview image.

## Fonts

Self-hosted Latin subsets from [Fontsource](https://fontsource.org/), all under the SIL Open Font License (texts in `public/fonts/`):

- [Newsreader](https://github.com/productiontype/Newsreader) for headings, variable weight and optical size
- [IBM Plex Sans](https://github.com/IBM/plex) for text
- [IBM Plex Mono](https://github.com/IBM/plex) for labels and paths
