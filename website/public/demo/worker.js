// Runs the text model off the main thread. Everything it loads comes from this site; nothing is sent anywhere.
import { AutoTokenizer, CLIPTextModelWithProjection, env } from '../vendor/transformers.min.js';

const MODEL = 'mobileclip_s0';
env.allowRemoteModels = false;
env.allowLocalModels = true;
// A path rather than a full URL: Transformers.js 4.3 skips its existence check for local files given as URLs.
env.localModelPath = new URL('../models/', import.meta.url).pathname;
env.backends.onnx.wasm.wasmPaths = new URL('../vendor/', import.meta.url).href;
env.backends.onnx.wasm.numThreads = 1;

const progress = (event) => {
  if (event.status === 'progress' && event.file?.endsWith('.onnx')) {
    postMessage({ type: 'progress', loaded: event.loaded, total: event.total });
  }
};

let text;
const loadText = () => (text ??= Promise.all([
  AutoTokenizer.from_pretrained(MODEL),
  CLIPTextModelWithProjection.from_pretrained(MODEL, { dtype: 'q8', device: 'wasm', progress_callback: progress }),
]));

const handlers = {
  async texts({ texts }) {
    const [tokenizer, model] = await loadText();
    const { text_embeds } = await model(tokenizer(texts, { padding: 'max_length', truncation: true }));
    return text_embeds.normalize().tolist().map((v) => Float32Array.from(v));
  },
};

self.addEventListener('message', async ({ data }) => {
  try {
    postMessage({ id: data.id, result: await handlers[data.type](data) });
  } catch (error) {
    postMessage({ id: data.id, error: String(error?.message ?? error) });
  }
});
