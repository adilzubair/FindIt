// Runs the model off the main thread. Everything it loads comes from this site; nothing is sent anywhere.
import { AutoProcessor, AutoTokenizer, CLIPTextModelWithProjection, CLIPVisionModelWithProjection, RawImage, env } from '../vendor/transformers.min.js';

const MODEL = 'mobileclip_s0';
env.allowRemoteModels = false;
env.allowLocalModels = true;
// A path rather than a full URL: Transformers.js 4.3 skips its existence check for local files given as URLs.
env.localModelPath = new URL('../models/', import.meta.url).pathname;
env.backends.onnx.wasm.wasmPaths = new URL('../vendor/', import.meta.url).href;
env.backends.onnx.wasm.numThreads = 1;

const progress = (part) => (event) => {
  if (event.status === 'progress' && event.file?.endsWith('.onnx')) {
    postMessage({ type: 'progress', part, loaded: event.loaded, total: event.total });
  }
};

let text;
let vision;

const loadText = () => (text ??= (async () => {
  const [tokenizer, model] = await Promise.all([
    AutoTokenizer.from_pretrained(MODEL),
    CLIPTextModelWithProjection.from_pretrained(MODEL, { dtype: 'q8', device: 'wasm', progress_callback: progress('text') }),
  ]);
  return { tokenizer, model };
})());

const loadVision = () => (vision ??= (async () => {
  const processor = await AutoProcessor.from_pretrained(MODEL);
  let model;
  if (self.navigator.gpu) {
    try {
      model = await CLIPVisionModelWithProjection.from_pretrained(MODEL, { dtype: 'fp32', device: 'webgpu', progress_callback: progress('vision') });
    } catch { model = undefined; }
  }
  model ??= await CLIPVisionModelWithProjection.from_pretrained(MODEL, { dtype: 'fp32', device: 'wasm', progress_callback: progress('vision') });
  return { processor, model };
})());

const toRawImage = (bitmap) => {
  const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
  const context = canvas.getContext('2d');
  context.drawImage(bitmap, 0, 0);
  bitmap.close();
  return new RawImage(context.getImageData(0, 0, canvas.width, canvas.height).data, canvas.width, canvas.height, 4);
};

const handlers = {
  async load({ part }) {
    await (part === 'vision' ? loadVision() : loadText());
    return true;
  },
  async texts({ texts }) {
    const { tokenizer, model } = await loadText();
    const out = [];
    // Small batches keep memory flat for long documents.
    for (let i = 0; i < texts.length; i += 16) {
      const { text_embeds } = await model(tokenizer(texts.slice(i, i + 16), { padding: 'max_length', truncation: true }));
      out.push(...text_embeds.normalize().tolist().map((v) => Float32Array.from(v)));
    }
    return out;
  },
  async image({ bitmap }) {
    const { processor, model } = await loadVision();
    const { image_embeds } = await model(await processor(toRawImage(bitmap)));
    return Float32Array.from(image_embeds.normalize().tolist()[0]);
  },
};

self.addEventListener('message', async ({ data }) => {
  try {
    postMessage({ id: data.id, result: await handlers[data.type](data) });
  } catch (error) {
    postMessage({ id: data.id, error: String(error?.message ?? error) });
  }
});
