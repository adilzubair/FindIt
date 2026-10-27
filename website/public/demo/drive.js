// A searchable drive: one search box, and only the files that match it.
import { clock, search, words } from './shared.js';
import { embedQuery, ready } from './engine.js';

const el = (tag, className, text) => Object.assign(document.createElement(tag), className ? { className } : {}, text !== undefined ? { textContent: text } : {});

const REASON = {
  looks: 'Looks similar',
  moment: 'Moment',
  words: 'Exact words',
  meaning: 'Same meaning',
  name: 'In the name',
};

const reasonText = (hit, reason) => (reason === 'moment' ? `${clock(hit.moment.start)}–${clock(hit.moment.end)}` : REASON[reason]);

// Renders the visible part of a document, starting at its first match, with the match marked.
export function paperText(target, text, { places = [], chunk = null } = {}, around = 260) {
  target.replaceChildren();
  let ranges = places;
  if (!ranges.length && chunk) ranges = [[chunk.start, chunk.end]];
  const focus = ranges[0]?.[0] ?? 0;
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

// A video still, cut from the strip of stills the sample index prepared for each video.
function frameStyle(target, file, index) {
  target.style.backgroundImage = `url("${file.strip}")`;
  target.style.backgroundSize = `${file.frames.length * 100}% 100%`;
  target.style.backgroundPosition = `${file.frames.length > 1 ? (index / (file.frames.length - 1)) * 100 : 0}% 0`;
}

export class Drive {
  constructor(root, { library, viewer, suggestions = [], idle }) {
    this.root = root;
    this.viewer = viewer;
    this.form = root.querySelector('.seek');
    this.input = root.querySelector('.seek input');
    this.readout = root.querySelector('.readout');
    this.results = root.querySelector('.results');
    this.peek = root.querySelector('.peek');
    this.hints = root.querySelector('.hints');
    this.query = '';

    this.library = library;
    this.idle = idle;
    this.renderHints(suggestions);
    this.renderPeek();
    this.reset();

    this.form.addEventListener('submit', (event) => { event.preventDefault(); this.run(this.input.value); });
    let typing;
    this.input.addEventListener('input', () => {
      window.clearTimeout(typing);
      if (!this.input.value.trim()) { this.reset(); return; }
      // Search as you type once the model is here; before that, wait for Enter so nothing downloads by accident.
      if (ready.text || this.library.queries?.[this.input.value.trim()]) typing = window.setTimeout(() => this.run(this.input.value), 260);
    });
  }

  renderHints(suggestions) {
    if (!this.hints) return;
    this.hints.replaceChildren();
    for (const text of suggestions) {
      const button = el('button', 'hint', text);
      button.type = 'button';
      button.addEventListener('click', () => { this.input.value = text; this.run(text); });
      this.hints.append(button);
    }
  }

  // Before a search: a quiet row of what is on the drive, under the names a computer sees.
  renderPeek() {
    if (!this.peek) return;
    this.peek.replaceChildren(...this.library.files.filter((f) => f.kind !== 'document').slice(0, 9).map((file) => {
      const item = el('li', 'peek__item');
      const media = el('span', 'peek__media');
      if (file.kind === 'photo') media.append(Object.assign(el('img'), { src: file.src, alt: '', loading: 'lazy', decoding: 'async' }));
      else frameStyle(media, file, 0);
      item.append(media, el('span', 'peek__name', file.name));
      return item;
    }));
  }

  reset() {
    this.query = '';
    this.root.classList.remove('has-results');
    this.results.replaceChildren();
    if (this.peek) this.peek.hidden = false;
    this.setReadout([this.idle]);
    this.hints?.querySelectorAll('.hint').forEach((b) => b.setAttribute('aria-pressed', 'false'));
  }

  setReadout(parts) {
    this.readout.replaceChildren(...parts.map((part) => (typeof part === 'string' ? part : el('strong', '', part.text))));
  }

  async run(raw) {
    const query = raw.trim();
    if (!query) { this.reset(); return; }
    this.query = query;
    this.hints?.querySelectorAll('.hint').forEach((b) => b.setAttribute('aria-pressed', String(b.textContent === query)));

    let vectors = this.library.queries?.[query];
    let understood = null;
    if (!vectors) {
      if (!ready.text) this.setReadout(['Fetching the search model from this site. This happens once.']);
      this.root.classList.add('is-thinking');
      const started = performance.now();
      try {
        vectors = await embedQuery(query);
      } catch (error) {
        console.warn(error);
        vectors = {};
      }
      understood = performance.now() - started;
      this.root.classList.remove('is-thinking');
      if (query !== this.input.value.trim() && this.input.value.trim()) return;
    }

    const started = performance.now();
    const results = search(this.library, query, vectors);
    this.show(results, performance.now() - started, understood);
  }

  show(results, elapsed, understood) {
    this.root.classList.toggle('has-results', results.length > 0);
    if (this.peek) this.peek.hidden = true;
    this.results.replaceChildren(...results.map((hit, index) => this.card(hit, index)));

    const total = this.library.files.length;
    const time = `${elapsed < 1 ? 'under 1' : Math.round(elapsed)} ms`;
    if (!results.length) {
      this.setReadout([{ text: 'Nothing close enough.' }, ' Try describing it another way.']);
      return;
    }
    const thought = understood === null ? '' : ` Understanding your words took ${Math.round(understood)} ms.`;
    this.setReadout([{ text: `${results.length} of ${total} files` }, ` · searched in ${time}, on this device.${thought}`]);
  }

  card(hit, index) {
    const { file } = hit;
    const item = el('li', `card card--${file.kind}`);
    item.style.setProperty('--i', index);
    const open = el('button', 'card__open');
    open.type = 'button';
    const media = el('span', 'card__media');
    if (file.kind === 'photo') {
      media.append(Object.assign(el('img'), { src: file.src, alt: '', decoding: 'async' }));
    } else if (file.kind === 'video') {
      const frame = el('span', 'card__frame');
      frameStyle(frame, file, hit.moment?.frame ?? 0);
      media.append(frame);
      if (hit.moment) media.append(el('span', 'card__time', `${clock(hit.moment.start)}–${clock(hit.moment.end)}`));
    } else {
      const paper = el('span', 'card__paper');
      paperText(paper, file.text, { places: hit.places ?? [], chunk: hit.reasons.includes('meaning') ? hit.chunk : null }, 220);
      media.append(paper);
    }
    const body = el('span', 'card__body');
    body.append(el('span', 'card__name', file.name), el('span', 'card__why', hit.reasons.map((r) => (r === 'moment' ? 'Matching moment' : REASON[r])).join(' · ')));
    open.append(media, body);
    open.setAttribute('aria-label', `${file.name}: ${hit.reasons.map((r) => reasonText(hit, r)).join(', ')}`);
    open.addEventListener('click', () => this.viewer.open(file, hit, this.query));
    item.append(open);
    return item;
  }
}

// The file viewer opened from a result.
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
      this.media.append(Object.assign(el('img'), { src: file.src, alt: file.name }));
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
      const paper = el('div', 'viewer__paper');
      paperText(paper, file.text, { places: hit?.places ?? [], chunk: hit?.reasons.includes('meaning') ? hit.chunk : null }, 1400);
      this.media.append(paper);
    }

    for (const reason of hit?.reasons ?? []) {
      const item = el('li');
      item.append(el('strong', '', reason === 'moment' ? `Moment ${reasonText(hit, reason)}` : REASON[reason]));
      const say = {
        looks: 'Found by what is in the picture. The filename says nothing about it.',
        moment: `Found in stills taken once a second. The video opens at ${clock(hit.moment?.start ?? 0)}.`,
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
