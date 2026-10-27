// A searchable drive: a wall of anonymous files that rearranges itself around what you ask for.
import { clock, search, words } from './shared.js';
import { embedQuery, ready } from './engine.js';

const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
const el = (tag, className, text) => Object.assign(document.createElement(tag), className ? { className } : {}, text !== undefined ? { textContent: text } : {});

const REASON = {
  looks: 'Looks similar',
  moment: 'Moment',
  words: 'Exact words',
  meaning: 'Same meaning',
  name: 'In the name',
};

export const describe = (files) => {
  const count = (kind) => files.filter((f) => f.kind === kind).length;
  const parts = [['photo', 'photo'], ['video', 'video'], ['document', 'document']]
    .map(([kind, word]) => [count(kind), word]).filter(([n]) => n)
    .map(([n, word]) => `${n} ${word}${n === 1 ? '' : 's'}`);
  return parts.length > 1 ? `${parts.slice(0, -1).join(', ')} and ${parts.at(-1)}` : parts[0] ?? 'no files';
};

// Renders the visible part of a document with its matched words or passage marked.
export function paperText(target, text, { places = [], chunk = null } = {}, around = 260) {
  target.replaceChildren();
  let ranges = places;
  if (!ranges.length && chunk) ranges = [[chunk.start, chunk.end]];
  const focus = ranges[0]?.[0] ?? 0;
  // Start at the line holding the first match; long previews keep a little context above it.
  const from = Math.max(0, text.lastIndexOf('\n', Math.max(0, focus - (around > 400 ? 120 : 1))) + 1);
  const to = Math.min(text.length, from + around);
  let cursor = from;
  if (from > 0) target.append('…');
  for (const [start, end] of ranges) {
    if (end <= cursor || start >= to) continue;
    target.append(text.slice(cursor, Math.max(cursor, start)));
    target.append(el('mark', chunk && !places.length ? 'is-passage' : '', text.slice(Math.max(cursor, start), Math.min(end, to))));
    cursor = Math.min(end, to);
  }
  target.append(text.slice(cursor, to));
  if (to < text.length) target.append('…');
}

export class Drive {
  constructor(root, { library, label, viewer, suggestions = [], idle }) {
    this.root = root;
    this.library = library;
    this.label = label;
    this.viewer = viewer;
    this.idle = idle;
    this.form = root.querySelector('.seek');
    this.input = root.querySelector('.seek input');
    this.readout = root.querySelector('.readout');
    this.meter = root.querySelector('.meter');
    this.wall = root.querySelector('.wall');
    this.hints = root.querySelector('.hints');
    this.tiles = new Map();
    this.query = '';

    this.renderHints(suggestions);
    this.renderTiles();
    this.reset();

    this.form.addEventListener('submit', (event) => { event.preventDefault(); this.run(this.input.value); });
    let typing;
    this.input.addEventListener('input', () => {
      window.clearTimeout(typing);
      if (!this.input.value.trim()) { this.reset(); return; }
      // Search as you type once the model is here; before that, wait for Enter so nothing downloads by accident.
      if (ready.text || this.library.queries?.[this.input.value.trim()]) typing = window.setTimeout(() => this.run(this.input.value), 260);
    });
    this.wall.addEventListener('pointermove', (event) => {
      const box = this.wall.getBoundingClientRect();
      this.wall.style.setProperty('--x', `${event.clientX - box.left}px`);
      this.wall.style.setProperty('--y', `${event.clientY - box.top}px`);
    });
  }

  // Swaps in a new set of files, for example after the visitor adds more.
  update({ library, idle }) {
    this.library = library;
    this.idle = idle;
    this.input.value = '';
    this.renderTiles();
    this.reset();
  }

  renderHints(suggestions) {
    if (!this.hints) return;
    this.hints.replaceChildren(el('span', 'hints__label', 'Try'));
    for (const text of suggestions) {
      const button = el('button', 'hint', text);
      button.type = 'button';
      button.addEventListener('click', () => { this.input.value = text; this.run(text); });
      this.hints.append(button);
    }
  }

  renderTiles() {
    this.wall.replaceChildren();
    this.tiles.clear();
    this.library.files.forEach((file, index) => {
      const tile = el('li', `tile tile--${file.kind}`);
      tile.style.setProperty('--n', index);
      const open = el('button', 'tile__open');
      open.type = 'button';
      open.setAttribute('aria-label', `Open ${file.name}`);
      const media = el('span', 'tile__media');
      if (file.kind === 'photo') {
        const img = el('img');
        Object.assign(img, { src: file.src, alt: '', loading: 'lazy', decoding: 'async' });
        media.append(img);
      } else if (file.kind === 'video') {
        const frame = el('span', 'tile__frame');
        this.showFrame(frame, file, 0);
        media.append(frame, el('span', 'tile__badge', clock(file.duration)), el('span', 'tile__play'));
      } else {
        const paper = el('span', 'paper');
        paper.append(el('span', 'paper__ext', file.extension.toUpperCase()), el('span', 'paper__text'));
        paperText(paper.lastChild, file.text, {}, 180);
        media.append(paper);
      }
      const why = el('span', 'tile__why');
      open.append(media, el('span', 'tile__name', file.name), why);
      open.addEventListener('click', () => this.viewer.open(file, this.hits?.get(file) ?? null, this.query));
      tile.append(open);
      this.wall.append(tile);
      this.tiles.set(file, { tile, why, media });
    });
  }

  showFrame(target, file, index) {
    if (file.strip) {
      target.style.backgroundImage = `url("${file.strip}")`;
      target.style.backgroundSize = `${file.frames.length * 100}% 100%`;
      target.style.backgroundPosition = `${file.frames.length > 1 ? (index / (file.frames.length - 1)) * 100 : 0}% 0`;
    } else {
      target.style.backgroundImage = `url("${file.frames[index].thumb}")`;
      target.style.backgroundSize = 'cover';
    }
  }

  reset() {
    this.query = '';
    this.hits = null;
    this.root.classList.remove('has-results', 'has-none');
    this.root.classList.add('is-idle');
    this.flip(() => {
      for (const [file, { tile, why, media }] of this.tiles) {
        tile.classList.remove('is-hit', 'is-miss', 'is-lead');
        tile.style.order = '';
        why.textContent = '';
        if (file.kind === 'video') this.showFrame(media.firstChild, file, 0);
        if (file.kind === 'document') paperText(media.querySelector('.paper__text'), file.text, {}, 180);
      }
    });
    this.setReadout(this.idle);
    this.hints?.querySelectorAll('.hint').forEach((b) => b.setAttribute('aria-pressed', 'false'));
  }

  setReadout(parts) {
    this.readout.replaceChildren(...parts.map((part) => (typeof part === 'string' ? part : el(part.tag ?? 'strong', part.className ?? '', part.text))));
  }

  async run(raw) {
    const query = raw.trim();
    if (!query) { this.reset(); return; }
    this.query = query;
    this.hints?.querySelectorAll('.hint').forEach((b) => b.setAttribute('aria-pressed', String(b.textContent === query)));

    let vectors = this.library.queries?.[query];
    let understood = null;
    if (!vectors) {
      if (!ready.text) this.setReadout([{ text: 'Fetching the search model' }, ' from this site, once. About 43 MB, then it stays in your browser.']);
      this.root.classList.add('is-thinking');
      const started = performance.now();
      try {
        vectors = await embedQuery(query);
      } catch (error) {
        this.root.classList.remove('is-thinking');
        this.setReadout([{ text: 'The search model could not load.' }, ` Exact words still work. (${error.message})`]);
        vectors = {};
      }
      understood = performance.now() - started;
      this.root.classList.remove('is-thinking');
      if (query !== this.input.value.trim() && this.input.value.trim()) return;
    }

    const started = performance.now();
    const results = search(this.library, query, vectors);
    const elapsed = performance.now() - started;
    this.show(results, { elapsed, understood });
  }

  show(results, { elapsed, understood }) {
    this.hits = new Map(results.map((hit) => [hit.file, hit]));
    this.root.classList.remove('is-idle');
    this.root.classList.toggle('has-results', results.length > 0);
    this.root.classList.toggle('has-none', results.length === 0);

    this.flip(() => {
      const rest = results.length;
      this.library.files.forEach((file, index) => {
        const { tile, why, media } = this.tiles.get(file);
        const hit = this.hits.get(file);
        tile.classList.toggle('is-hit', Boolean(hit));
        tile.classList.toggle('is-miss', !hit);
        tile.classList.toggle('is-lead', hit === results[0] && results.length > 2);
        tile.style.order = hit ? results.indexOf(hit) : rest + index;
        why.replaceChildren();
        if (!hit) return;
        why.append(el('span', '', hit.reasons.map((r) => (r === 'moment' ? `${REASON.moment} ${clock(hit.moment.start)}–${clock(hit.moment.end)}` : REASON[r])).join(' · ')));
        if (file.kind === 'video' && hit.moment) this.showFrame(media.firstChild, file, hit.moment.frame);
        if (file.kind === 'document') paperText(media.querySelector('.paper__text'), file.text, { places: hit.places ?? [], chunk: hit.chunk && hit.reasons.includes('meaning') ? hit.chunk : null }, tile.classList.contains('is-lead') ? 480 : 180);
      });
    });

    const total = this.library.files.length;
    const timing = `${elapsed < 1 ? '<1' : Math.round(elapsed)} ms`;
    if (!results.length) {
      this.setReadout([{ text: 'Nothing close enough.' }, ` Searched ${total} files in ${timing}. Try describing it differently.`]);
      return;
    }
    const lead = [{ text: `${results.length} match${results.length === 1 ? '' : 'es'}` }, ` for “${this.query}”. Searched ${total} files in ${timing}, on this device`];
    if (understood !== null) lead.push(`, after ${Math.round(understood)} ms understanding your words`);
    lead.push('.');
    this.setReadout(lead);
  }

  // Moves tiles to their new places smoothly: measure, change, then animate from the old positions.
  flip(change) {
    if (reduceMotion || !this.wall.isConnected) { change(); return; }
    const before = new Map([...this.tiles.values()].map(({ tile }) => [tile, tile.getBoundingClientRect()]));
    change();
    const visible = (r) => r.bottom > -200 && r.top < innerHeight + 200;
    for (const [tile, first] of before) {
      const last = tile.getBoundingClientRect();
      if (!visible(first) && !visible(last)) continue;
      const dx = first.left - last.left;
      const dy = first.top - last.top;
      const sx = first.width / last.width;
      const sy = first.height / last.height;
      if (Math.abs(dx) < 1 && Math.abs(dy) < 1 && Math.abs(sx - 1) < 0.01) continue;
      tile.animate(
        [{ transform: `translate(${dx}px, ${dy}px) scale(${sx}, ${sy})` }, { transform: 'none' }],
        { duration: 620, easing: 'cubic-bezier(.2,.75,.2,1)', delay: tile.classList.contains('is-hit') ? Number(tile.style.order) * 28 : 0, fill: 'backwards' },
      );
    }
  }
}

// One viewer, shared by every drive on the page.
export class Viewer {
  constructor(dialog) {
    this.dialog = dialog;
    this.media = dialog.querySelector('.viewer__media');
    this.title = dialog.querySelector('.viewer__name');
    this.meta = dialog.querySelector('.viewer__meta');
    this.why = dialog.querySelector('.viewer__why');
    this.original = dialog.querySelector('.viewer__original');
    dialog.addEventListener('click', (event) => { if (event.target === dialog) dialog.close(); });
    dialog.addEventListener('close', () => this.media.querySelector('video')?.pause());
  }

  open(file, hit, query) {
    this.media.replaceChildren();
    this.why.replaceChildren();
    this.title.textContent = file.name;
    const kind = file.kind === 'photo' ? 'Photo' : file.kind === 'video' ? `Video · ${clock(file.duration)}` : `${file.extension.toUpperCase()} document`;
    this.meta.textContent = `${kind} · ${file.folder}`;
    this.original.href = file.src;
    this.original.hidden = file.kind !== 'document';

    if (file.kind === 'photo') {
      const img = el('img');
      Object.assign(img, { src: file.src, alt: file.name });
      this.media.append(img);
    } else if (file.kind === 'video') {
      const video = el('video');
      Object.assign(video, { src: file.src, controls: true, playsInline: true, muted: true, preload: 'auto' });
      const strip = el('div', 'viewer__timeline');
      if (hit?.moment) {
        const span = el('span', 'viewer__moment');
        span.style.left = `${(hit.moment.start / file.duration) * 100}%`;
        span.style.width = `${((hit.moment.end - hit.moment.start) / file.duration) * 100}%`;
        strip.append(span);
        video.addEventListener('loadedmetadata', () => { video.currentTime = hit.moment.start; }, { once: true });
        video.addEventListener('timeupdate', () => { if (video.currentTime >= hit.moment.end && !video.dataset.watched) { video.dataset.watched = '1'; video.pause(); } });
      }
      const head = el('span', 'viewer__head');
      strip.append(head);
      video.addEventListener('timeupdate', () => { head.style.left = `${(video.currentTime / file.duration) * 100}%`; });
      this.media.append(video, strip);
      if (hit?.moment) video.play().catch(() => {});
    } else {
      const paper = el('div', 'paper paper--full');
      const body = el('div', 'paper__text');
      paperText(body, file.text, { places: hit?.places ?? [], chunk: hit?.reasons.includes('meaning') ? hit.chunk : null }, 1400);
      paper.append(el('span', 'paper__ext', file.extension.toUpperCase()), body);
      this.media.append(paper);
    }

    const reasons = hit?.reasons ?? [];
    if (!reasons.length) this.why.append(el('li', '', 'Not a match for the current search. Open it any time from the drive.'));
    for (const reason of reasons) {
      const item = el('li');
      item.append(el('strong', '', reason === 'moment' ? `Moment ${clock(hit.moment.start)}–${clock(hit.moment.end)}` : REASON[reason]));
      const say = {
        looks: 'Found by what is in the picture. The filename says nothing about it.',
        moment: `Found in the stills taken once a second. The video opens at ${clock(hit.moment?.start ?? 0)}.`,
        words: `“${words(query).join(' ')}” appears ${hit.places?.length} time${hit.places?.length === 1 ? '' : 's'}, highlighted.`,
        meaning: 'This passage means what you asked, even where the words differ.',
        name: 'The filename contains your words.',
      }[reason];
      item.append(el('span', '', say));
      this.why.append(item);
    }
    this.dialog.showModal();
  }
}

