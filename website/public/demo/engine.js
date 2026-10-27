// The page's side of the demo: loads the prepared sample drive and turns typed searches into vectors.
import { queryPrompt, unpack } from './shared.js';

let worker;
let sequence = 0;
const pending = new Map();
const listeners = new Set();
export const ready = { text: false };

function call(type, payload = {}) {
  worker ??= createWorker();
  return new Promise((resolve, reject) => {
    sequence += 1;
    pending.set(sequence, { resolve, reject });
    worker.postMessage({ id: sequence, type, ...payload });
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
