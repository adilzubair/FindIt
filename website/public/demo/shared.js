// Search logic shared by the page and by tools/index-samples.mjs. No browser or Node APIs here.

// Video sampling: one still per second, taken mid-second, shown as 192×108 in results.
export const FRAME = { offset: 0.5, step: 1, width: 192, height: 108, maxFrames: 90 };

export const SUGGESTIONS = [
  'dog running on the beach',
  'elephants crossing the road',
  'a cat on a desk',
  'what to buy for pizza night',
  'boats in a harbour',
  'AB-0091',
  'can I keep a pet',
  'plane in the sky',
  'a train at the station',
  'the moon',
];

// The image model was trained on captions, so a caption-like prompt ranks pictures better.
export const queryPrompt = (query) => `a photo of ${query}`;

// Cut-offs measured on the sample drive. Pictures and passages score on different scales.
export const MATCH = {
  picture: { floor: 0.17, spread: 0.07, top: 0.3 },
  passage: { floor: 0.7, spread: 0.05, top: 0.82 },
  moment: 0.03,
  limit: 12,
};

const STOP = new Set('a an and are at be by can do for from how i in is it my of on or the to was what when where which who with'.split(' '));

export function unpack({ s, q }) {
  const bytes = Uint8Array.from(atob(q), (c) => c.charCodeAt(0));
  return Float32Array.from(new Int8Array(bytes.buffer), (x) => x * s);
}

const dot = (a, b) => {
  let sum = 0;
  for (let i = 0; i < a.length; i += 1) sum += a[i] * b[i];
  return sum;
};

// Splits text into passages of up to ~220 characters along line and sentence breaks, keeping offsets.
export function chunkText(text, size = 220) {
  const pieces = [];
  const pattern = /[^\n.!?]+[.!?]*\s*/g;
  let match;
  while ((match = pattern.exec(text))) {
    if (match[0].trim()) pieces.push({ start: match.index, end: match.index + match[0].trimEnd().length });
  }
  const chunks = [];
  for (const piece of pieces) {
    const last = chunks.at(-1);
    if (last && piece.end - last.start <= size && !text.slice(last.end, piece.start).includes('\n\n')) last.end = piece.end;
    else chunks.push({ ...piece });
  }
  return chunks.map((c) => ({ ...c, text: text.slice(c.start, c.end) }));
}

export const words = (query) => (query.toLowerCase().match(/[\p{L}\p{N}][\p{L}\p{N}-]*/gu) ?? []).filter((w) => !STOP.has(w));

const escape = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

// Every occurrence of the query's words (as word prefixes) in text.
export function findWords(text, terms) {
  if (!terms.length || !text) return [];
  const found = [];
  const seen = new Set();
  for (const term of terms) {
    const pattern = new RegExp(`(?<![\\p{L}\\p{N}])${escape(term)}`, 'giu');
    let match;
    let any = false;
    while ((match = pattern.exec(text))) {
      any = true;
      let end = match.index + term.length;
      while (end < text.length && /[\p{L}\p{N}]/u.test(text[end])) end += 1;
      found.push([match.index, end]);
    }
    if (any) seen.add(term);
  }
  return seen.size === terms.length ? found.sort((a, b) => a[0] - b[0]) : [];
}

// Ranks a library for one query. `vectors` holds { picture, passage } query embeddings when available.
export function search(library, query, vectors = {}) {
  const terms = words(query);
  const hits = [];
  for (const file of library.files) {
    const hit = { file, reasons: [], strength: 0 };

    const nameMatches = findWords(file.name.replace(/[_.-]+/g, ' '), terms);
    if (nameMatches.length) { hit.reasons.push('name'); hit.strength = Math.max(hit.strength, 1.2); }

    if (file.text) {
      const places = findWords(file.text, terms);
      if (places.length) {
        hit.reasons.push('words');
        hit.places = places;
        hit.strength = Math.max(hit.strength, 1.5 + Math.min(places.length, 5) * 0.05);
      }
    }

    if (vectors.picture && file.vector) {
      hit.picture = dot(file.vector, vectors.picture);
    }
    if (vectors.picture && file.frames?.length) {
      const scores = file.frames.map((frame) => dot(frame.vector, vectors.picture));
      let best = 0;
      scores.forEach((s, i) => { if (s > scores[best]) best = i; });
      // The moment is the run of neighbouring stills that score almost as well as the best one.
      const near = Math.max(MATCH.picture.floor, scores[best] - MATCH.moment);
      let first = best;
      let last = best;
      while (first > 0 && scores[first - 1] >= near) first -= 1;
      while (last < scores.length - 1 && scores[last + 1] >= near) last += 1;
      hit.picture = scores[best];
      hit.moment = { frame: best, start: file.frames[first].t - FRAME.step / 2, end: file.frames[last].t + FRAME.step / 2, at: file.frames[best].t };
    }
    if (vectors.passage && file.chunks?.length) {
      let best = null;
      for (const chunk of file.chunks) {
        const s = dot(chunk.vector, vectors.passage);
        if (!best || s > best.score) best = { chunk, score: s };
      }
      hit.passage = best.score;
      hit.chunk = best.chunk;
    }
    hits.push(hit);
  }

  // Meaning matches must clear an absolute floor and stay close to the best match of their kind.
  // When the exact words were found somewhere, only confident meaning matches are worth showing beside them.
  const exact = hits.some((h) => h.reasons.length);
  const bestOf = (key) => Math.max(-1, ...hits.map((h) => h[key] ?? -1));
  for (const [key, rule] of [['picture', MATCH.picture], ['passage', MATCH.passage]]) {
    const floor = exact ? rule.floor + (rule.top - rule.floor) * 0.3 : rule.floor;
    const cut = Math.max(floor, bestOf(key) - rule.spread);
    for (const hit of hits) {
      if (hit[key] === undefined || hit[key] < cut) continue;
      hit.reasons.push(key === 'picture' ? (hit.moment ? 'moment' : 'looks') : 'meaning');
      hit.strength = Math.max(hit.strength, (hit[key] - rule.floor) / (rule.top - rule.floor));
    }
  }

  return hits
    .filter((h) => h.reasons.length)
    .sort((a, b) => b.strength - a.strength)
    .slice(0, MATCH.limit);
}

export const clock = (seconds) => `${Math.floor(seconds / 60)}:${String(Math.floor(seconds % 60)).padStart(2, '0')}`;
