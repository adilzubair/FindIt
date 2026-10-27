// The model behind the demo, pinned to one revision so the prepared sample index always matches it.
// Files are fetched once into website/.cache/models and served from the site itself, never from Hugging Face.
import { createWriteStream } from 'node:fs';
import { mkdir, rename, stat } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';

export const MODEL = {
  id: 'mobileclip_s0',
  repository: 'Xenova/mobileclip_s0',
  revision: '757d59c9c6870a76a4b0306f05f5061bca15c39f',
  // The text encoder is quantized; the image encoder stays fp32, as the model's own config recommends.
  files: [
    'config.json',
    'preprocessor_config.json',
    'tokenizer.json',
    'tokenizer_config.json',
    'LICENSE',
    'onnx/text_model_quantized.onnx',
    'onnx/vision_model.onnx',
  ],
  dtype: { text: 'q8', vision: 'fp32' },
  // The page only embeds typed searches, so it ships the text half. The image half is used by tools/index-samples.mjs.
  site: ['config.json', 'tokenizer.json', 'tokenizer_config.json', 'LICENSE', 'onnx/text_model_quantized.onnx'],
};

export const modelRoot = join(dirname(fileURLToPath(import.meta.url)), '../.cache/models');

export async function ensureModel() {
  for (const file of MODEL.files) {
    const target = join(modelRoot, MODEL.id, file);
    if (await stat(target).then((s) => s.size > 0, () => false)) continue;
    const url = `https://huggingface.co/${MODEL.repository}/resolve/${MODEL.revision}/${file}`;
    const response = await fetch(url);
    if (!response.ok) throw new Error(`Downloading ${url} failed: ${response.status}`);
    await mkdir(dirname(target), { recursive: true });
    await pipeline(Readable.fromWeb(response.body), createWriteStream(`${target}.part`));
    await rename(`${target}.part`, target);
    console.log(`Fetched ${MODEL.id}/${file}`);
  }
  return join(modelRoot, MODEL.id);
}
