// Assembles the website into website/_site. Run `npm ci` in website/ first.
//
//   node website/build.mjs            copy public/, the demo's runtime and model, the tour video and favicon
//   node website/build.mjs --release  also point the download links at the newest published release
//
// The tour video and favicon are copied from where the repository already keeps them, so they are not duplicated.
// The model is fetched once into website/.cache and served from the site, so visitors never contact another origin.
import { cp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { MODEL, ensureModel } from './tools/models.mjs';

const site = dirname(fileURLToPath(import.meta.url));
const root = join(site, '..');
const out = join(site, '_site');
const modules = join(site, 'node_modules');
const repository = process.env.GITHUB_REPOSITORY ?? 'adilzubair/FindIt';

await rm(out, { recursive: true, force: true });
await cp(join(site, 'public'), out, { recursive: true });
await mkdir(join(out, 'media'), { recursive: true });
await cp(join(root, 'docs/media/findit-demo.mp4'), join(out, 'media/findit-demo.mp4'));
await cp(join(root, 'src/FindIt.App/Assets/FindIt.ico'), join(out, 'favicon.ico'));

// The in-browser search: Transformers.js with its WebAssembly runtime, and the text half of the model.
const vendor = {
  '@huggingface/transformers/dist/transformers.min.js': 'transformers.min.js',
  'onnxruntime-web/dist/ort-wasm-simd-threaded.asyncify.mjs': 'ort-wasm-simd-threaded.asyncify.mjs',
  'onnxruntime-web/dist/ort-wasm-simd-threaded.asyncify.wasm': 'ort-wasm-simd-threaded.asyncify.wasm',
};
for (const [from, to] of Object.entries(vendor)) await cp(join(modules, from), join(out, 'vendor', to));
const model = await ensureModel();
for (const file of MODEL.site) await cp(join(model, file), join(out, 'models', MODEL.id, file));

if (process.argv.includes('--release')) {
  const release = await latestRelease();
  const page = join(out, 'index.html');
  await writeFile(page, stamp(await readFile(page, 'utf8'), release));
  console.log(`Download links point at ${release.version}.`);
}
console.log(`Site assembled in ${out}`);

// Newest non-draft release (prereleases included) that has a package ZIP.
async function latestRelease() {
  const headers = { Accept: 'application/vnd.github+json', 'User-Agent': 'findit-website' };
  if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
  const response = await fetch(`https://api.github.com/repos/${repository}/releases?per_page=20`, { headers });
  if (!response.ok) throw new Error(`GitHub releases request failed: ${response.status} ${response.statusText}`);
  for (const release of await response.json()) {
    if (release.draft) continue;
    const zip = release.assets.find((asset) => /^FindIt-.+-win-x64\.zip$/.test(asset.name));
    if (!zip) continue;
    return {
      version: release.tag_name.replace(/^v/, ''),
      size: `${(zip.size / 1e9).toFixed(1)} GB`,
      date: new Date(release.published_at).toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' }),
    };
  }
  throw new Error('No published release with a FindIt ZIP was found.');
}

// The page is written against one release; swap its version everywhere, then the size and date.
function stamp(html, release) {
  const current = html.match(/<meta name="findit-version" content="([^"]+)">/)?.[1];
  if (!current) throw new Error('index.html has no findit-version meta tag.');
  return html
    .replaceAll(current, release.version)
    .replace(/(data-release-size>)[^<]*/g, `$1${release.size}`)
    .replace(/(data-release-date>)[^<]*/g, `$1${release.date}`);
}
