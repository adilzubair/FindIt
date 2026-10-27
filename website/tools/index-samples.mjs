// Prepares the sample drive the way FindIt prepares a real one, ahead of time:
// photo and video-frame embeddings, document text, and embeddings for the suggested searches.
// Writes public/samples/index.json and a frame strip per video. Run it after changing anything in public/samples.
//
//   node website/tools/index-samples.mjs          (needs ffmpeg on PATH, or FFMPEG=path\to\ffmpeg.exe)
import { execFile } from 'node:child_process';
import { mkdtemp, readdir, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, extname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { AutoProcessor, AutoTokenizer, CLIPTextModelWithProjection, CLIPVisionModelWithProjection, RawImage, env } from '@huggingface/transformers';
import { getDocument } from 'pdfjs-dist/legacy/build/pdf.mjs';
import { MODEL, ensureModel, modelRoot } from './models.mjs';
import { FRAME, SUGGESTIONS, chunkText, queryPrompt } from '../public/demo/shared.js';

const run = promisify(execFile);
const ffmpeg = process.env.FFMPEG ?? 'ffmpeg';
const site = join(dirname(fileURLToPath(import.meta.url)), '../public');
const samples = join(site, 'samples');

await ensureModel();
env.localModelPath = `${modelRoot}/`;
env.allowRemoteModels = false;
const tokenizer = await AutoTokenizer.from_pretrained(MODEL.id);
const textModel = await CLIPTextModelWithProjection.from_pretrained(MODEL.id, { dtype: MODEL.dtype.text });
const processor = await AutoProcessor.from_pretrained(MODEL.id);
const visionModel = await CLIPVisionModelWithProjection.from_pretrained(MODEL.id, { dtype: MODEL.dtype.vision });

const embedImage = async (image) => (await visionModel(await processor(image))).image_embeds.normalize().tolist()[0];
const embedTexts = async (texts) => (await textModel(tokenizer(texts, { padding: 'max_length', truncation: true }))).text_embeds.normalize().tolist();

// Vectors are stored as int8 with a per-vector scale: small enough to ship, precise enough to rank.
const pack = (vector) => {
  const scale = Math.max(...vector.map(Math.abs)) / 127;
  return { s: Number(scale.toPrecision(6)), q: Buffer.from(Int8Array.from(vector, (x) => Math.round(x / scale)).buffer).toString('base64') };
};

const files = [];

for (const name of (await readdir(join(samples, 'photos'))).sort()) {
  const path = join(samples, 'photos', name);
  const image = await RawImage.read(path);
  files.push({
    kind: 'photo',
    name: name.replace(/\.webp$/, '.jpg'),
    folder: 'Photos',
    src: `samples/photos/${name}`,
    width: image.width,
    height: image.height,
    vector: pack(await embedImage(image)),
  });
}

for (const name of (await readdir(join(samples, 'videos'))).filter((n) => n.endsWith('.mp4')).sort()) {
  const path = join(samples, 'videos', name);
  const duration = Number((await run(ffmpeg, ['-hide_banner', '-i', path], { encoding: 'utf8' }).catch((e) => e)).stderr.match(/Duration: (\d+):(\d+):([\d.]+)/).slice(1).reduce((t, x) => t * 60 + Number(x), 0));
  const work = await mkdtemp(join(tmpdir(), 'findit-frames-'));
  const frames = [];
  for (let t = FRAME.offset; t < duration; t += FRAME.step) {
    const still = join(work, `${frames.length}.png`);
    await run(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-ss', t.toFixed(2), '-i', path, '-frames:v', '1', still]);
    frames.push({ t: Number(t.toFixed(2)), vector: pack(await embedImage(await RawImage.read(still))) });
  }
  // One strip of small stills per video, used to show the matching moment.
  const strip = `${name.replace(/\.mp4$/, '')}.frames.webp`;
  await run(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-y', '-start_number', '0', '-i', join(work, '%d.png'),
    '-vf', `scale=${FRAME.width}:${FRAME.height},tile=${frames.length}x1`, '-frames:v', '1', '-c:v', 'libwebp', '-quality', '72', join(samples, 'videos', strip)]);
  await rm(work, { recursive: true, force: true });
  files.push({ kind: 'video', name, folder: 'Videos', src: `samples/videos/${name}`, duration, strip: `samples/videos/${strip}`, frames });
}

for (const name of (await readdir(join(samples, 'documents'))).sort()) {
  const path = join(samples, 'documents', name);
  const extension = extname(name).slice(1).toLowerCase();
  let text;
  if (extension === 'pdf') {
    const pdf = await getDocument({ data: new Uint8Array(await readFile(path)), useSystemFonts: true }).promise;
    const pages = [];
    for (let p = 1; p <= pdf.numPages; p += 1) {
      const content = await (await pdf.getPage(p)).getTextContent();
      pages.push(content.items.map((item) => item.str + (item.hasEOL ? '\n' : '')).join(''));
    }
    text = pages.join('\n\n');
  } else {
    text = await readFile(path, 'utf8');
  }
  text = text.replace(/\r\n/g, '\n').replace(/[ \t]+\n/g, '\n').trim();
  const chunks = chunkText(text);
  const vectors = await embedTexts(chunks.map((c) => c.text));
  files.push({
    kind: 'document', name, folder: 'Documents', src: `samples/documents/${name}`, extension, size: (await stat(path)).size, text,
    chunks: chunks.map((c, i) => ({ start: c.start, end: c.end, vector: pack(vectors[i]) })),
  });
}

// Suggested searches are embedded here too, so they answer instantly before the visitor downloads any model.
const pictures = await embedTexts(SUGGESTIONS.map(queryPrompt));
const passages = await embedTexts(SUGGESTIONS);
const queries = Object.fromEntries(SUGGESTIONS.map((q, i) => [q, { picture: pack(pictures[i]), passage: pack(passages[i]) }]));

await writeFile(join(samples, 'index.json'), `${JSON.stringify({ model: MODEL.id, revision: MODEL.revision, files, queries })}\n`);
console.log(`Indexed ${files.length} files and ${SUGGESTIONS.length} suggested searches.`);
