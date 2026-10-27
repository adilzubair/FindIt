// The page's side of the demo: loads the prepared sample drive, and prepares a visitor's own files in this tab.
import { FRAME, chunkText, queryPrompt, unpack } from './shared.js';

let worker;
let sequence = 0;
const pending = new Map();
const listeners = new Set();
export const ready = { text: false, vision: false };

function call(type, payload = {}, transfer = []) {
  worker ??= createWorker();
  return new Promise((resolve, reject) => {
    sequence += 1;
    pending.set(sequence, { resolve, reject });
    worker.postMessage({ id: sequence, type, ...payload }, transfer);
  });
}

function createWorker() {
  const created = new Worker(new URL('./worker.js', import.meta.url), { type: 'module' });
  created.addEventListener('message', ({ data }) => {
    if (data.type === 'progress') { listeners.forEach((listener) => listener(data)); return; }
    const request = pending.get(data.id);
    pending.delete(data.id);
    if (data.error) request.reject(new Error(data.error));
    else request.resolve(data.result);
  });
  return created;
}

export const onModelProgress = (listener) => { listeners.add(listener); return () => listeners.delete(listener); };

export async function loadModel(part) {
  await call('load', { part });
  ready[part] = true;
}

export async function embedQuery(query) {
  const [picture, passage] = await call('texts', { texts: [queryPrompt(query), query] });
  ready.text = true;
  return { picture, passage };
}

const unpackFile = (file) => ({
  ...file,
  vector: file.vector && unpack(file.vector),
  frames: file.frames?.map((frame) => ({ ...frame, vector: unpack(frame.vector) })),
  chunks: file.chunks?.map((chunk) => ({ ...chunk, text: file.text.slice(chunk.start, chunk.end), vector: unpack(chunk.vector) })),
});

export async function loadSamples(url) {
  const index = await (await fetch(url)).json();
  // Paths in the index are relative to the site root, which is where the page lives.
  const resolve = (path) => path && new URL(path, document.baseURI).href;
  return {
    files: index.files.map((file) => unpackFile({ ...file, src: resolve(file.src), strip: resolve(file.strip) })),
    queries: Object.fromEntries(Object.entries(index.queries).map(([q, v]) => [q, { picture: unpack(v.picture), passage: unpack(v.passage) }])),
  };
}

// ---- Preparing a visitor's own files -------------------------------------------------------------

const IMAGE = /\.(jpe?g|png|webp|gif|avif|bmp)$/i;
const VIDEO = /\.(mp4|m4v|webm|mov|ogv)$/i;
const TEXT = /\.(txt|md|markdown|csv|json|html?|xml|log|rtf)$/i;

export function kindOf(file) {
  if (IMAGE.test(file.name) || /^image\/(jpeg|png|webp|gif|avif|bmp)$/.test(file.type)) return 'photo';
  if (file.type.startsWith('video/') || VIDEO.test(file.name)) return 'video';
  if (file.type === 'application/pdf' || /\.pdf$/i.test(file.name)) return 'pdf';
  if (file.type.startsWith('text/') || TEXT.test(file.name)) return 'text';
  return null;
}

const extension = (name) => (name.match(/\.([^.]+)$/)?.[1] ?? '').toLowerCase();

async function readPdf(file, step) {
  const pdfjs = await import('../vendor/pdf.min.mjs');
  pdfjs.GlobalWorkerOptions.workerSrc = new URL('../vendor/pdf.worker.min.mjs', import.meta.url).href;
  const task = pdfjs.getDocument({ data: new Uint8Array(await file.arrayBuffer()), isEvalSupported: false });
  const pdf = await task.promise;
  const pages = [];
  for (let p = 1; p <= Math.min(pdf.numPages, 30); p += 1) {
    step(`reading page ${p} of ${pdf.numPages}`);
    const content = await (await pdf.getPage(p)).getTextContent();
    pages.push(content.items.map((item) => item.str + (item.hasEOL ? '\n' : '')).join(''));
  }
  await task.destroy();
  return pages.join('\n\n');
}

async function readText(file) {
  const raw = await file.text();
  if (/\.html?$/i.test(file.name)) return new DOMParser().parseFromString(raw, 'text/html').body.textContent ?? '';
  return raw;
}

async function prepareDocument(file, text, step) {
  const clean = text.replace(/\r\n/g, '\n').replace(/[ \t]+\n/g, '\n').replace(/\n{3,}/g, '\n\n').trim().slice(0, 40000);
  if (!clean) return null;
  const chunks = chunkText(clean);
  step(`understanding ${chunks.length} passage${chunks.length === 1 ? '' : 's'}`);
  const vectors = await call('texts', { texts: chunks.map((c) => c.text) });
  return { kind: 'document', extension: extension(file.name), text: clean, chunks: chunks.map((c, i) => ({ ...c, vector: vectors[i] })) };
}

const once = (target, event) => new Promise((resolve, reject) => {
  target.addEventListener(event, resolve, { once: true });
  target.addEventListener('error', () => reject(new Error('This video format cannot be decoded by your browser.')), { once: true });
});

async function prepareVideo(file, url, step) {
  const video = document.createElement('video');
  video.muted = true;
  video.playsInline = true;
  video.preload = 'auto';
  video.src = url;
  await once(video, 'loadeddata');
  const duration = video.duration;
  if (!Number.isFinite(duration) || !video.videoWidth) throw new Error('This video format cannot be decoded by your browser.');
  const stepSize = Math.max(FRAME.step, duration / FRAME.maxFrames);
  const width = Math.min(512, video.videoWidth);
  const height = Math.round((video.videoHeight / video.videoWidth) * width);
  const still = Object.assign(document.createElement('canvas'), { width, height });
  const thumb = Object.assign(document.createElement('canvas'), { width: FRAME.width, height: FRAME.height });
  const frames = [];
  for (let t = Math.min(FRAME.offset, duration / 2); t < duration; t += stepSize) {
    step(`sampling ${clockShort(t)} of ${clockShort(duration)}`);
    video.currentTime = t;
    await once(video, 'seeked');
    still.getContext('2d').drawImage(video, 0, 0, width, height);
    const scale = Math.max(FRAME.width / width, FRAME.height / height);
    thumb.getContext('2d').drawImage(still, (FRAME.width - width * scale) / 2, (FRAME.height - height * scale) / 2, width * scale, height * scale);
    const bitmap = await createImageBitmap(still);
    frames.push({ t: Number(t.toFixed(2)), vector: await call('image', { bitmap }, [bitmap]), thumb: thumb.toDataURL('image/jpeg', 0.72) });
  }
  video.removeAttribute('src');
  video.load();
  return { kind: 'video', duration, frames };
}

const clockShort = (s) => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`;

// Prepares files one at a time and reports each step. Unreadable files are listed rather than failing the batch.
export async function prepare(list, report) {
  const files = [];
  const skipped = [];
  const usable = list.filter((file) => {
    if (kindOf(file)) return true;
    skipped.push({ name: file.name, why: 'this page reads photos, videos, PDFs and text files' });
    return false;
  });
  const needsVision = usable.some((f) => ['photo', 'video'].includes(kindOf(f)));
  report({ phase: 'model', detail: 'loading the model' });
  await loadModel('text');
  if (needsVision) await loadModel('vision');

  for (const [i, file] of usable.entries()) {
    const step = (detail) => report({ phase: 'file', done: i, total: usable.length, name: file.name, detail });
    const url = URL.createObjectURL(file);
    try {
      let entry;
      const kind = kindOf(file);
      if (kind === 'photo') {
        step('looking at the picture');
        const bitmap = await createImageBitmap(file, { resizeWidth: 512, resizeQuality: 'medium' });
        const { width, height } = bitmap;
        entry = { kind: 'photo', width, height, vector: await call('image', { bitmap }, [bitmap]) };
      } else if (kind === 'video') {
        entry = await prepareVideo(file, url, step);
      } else {
        step('reading the text');
        entry = await prepareDocument(file, kind === 'pdf' ? await readPdf(file, step) : await readText(file), step);
        if (!entry) throw new Error('No text could be read from it (scanned pages need OCR, which the app has).');
      }
      files.push({ ...entry, name: file.name, folder: 'Your files', src: url, size: file.size });
    } catch (error) {
      URL.revokeObjectURL(url);
      skipped.push({ name: file.name, why: error.message });
    }
  }
  report({ phase: 'done', done: usable.length, total: usable.length });
  return { files, skipped };
}

export function forget(library) {
  for (const file of library?.files ?? []) if (file.src.startsWith('blob:')) URL.revokeObjectURL(file.src);
}
