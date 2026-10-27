// Page wiring: the sample drive, the visitor's own files, and small conveniences.
import { Drive, Viewer, describe } from './demo/drive.js';
import { forget, loadSamples, onModelProgress, prepare, ready } from './demo/engine.js';
import { SUGGESTIONS } from './demo/shared.js';

const bar = document.querySelector('.bar');
const onScroll = () => bar.classList.toggle('is-scrolled', window.scrollY > 8);
onScroll();
window.addEventListener('scroll', onScroll, { passive: true });

const menu = document.querySelector('.nav__menu');
menu.addEventListener('click', (event) => { if (event.target.closest('a')) menu.open = false; });
document.addEventListener('keydown', (event) => { if (event.key === 'Escape') menu.open = false; });

const viewer = new Viewer(document.querySelector('.viewer'));

// Model downloads are reported per file; the page shows them as one figure.
const downloads = new Map();
const megabytes = (bytes) => `${Math.round(bytes / 1e6)} MB`;
const downloadText = () => {
  let loaded = 0;
  let total = 0;
  for (const d of downloads.values()) { loaded += d.loaded; total += d.total; }
  return total ? `${megabytes(loaded)} of ${megabytes(total)}` : '';
};

// ---- Sample drive ----------------------------------------------------------------------------------

const sampleRoot = document.getElementById('sample-drive');
const sample = await loadSamples('samples/index.json');
const counts = describe(sample.files);
document.querySelector('[data-count]').textContent = String(sample.files.length);
const sampleDrive = new Drive(sampleRoot, {
  library: sample,
  viewer,
  suggestions: SUGGESTIONS,
  idle: [{ text: `${sample.files.length} files` }, `: ${counts}. Hover to rummage through them, or just ask.`],
});

// A search can be shared as a link: /?q=elephants+crossing+the+road
const shared = new URLSearchParams(location.search).get('q');
if (shared) {
  sampleRoot.querySelector('input').value = shared;
  sampleDrive.run(shared);
}
sampleRoot.querySelector('.seek').addEventListener('submit', () => {
  const q = sampleRoot.querySelector('input').value.trim();
  history.replaceState(null, '', q ? `?q=${encodeURIComponent(q).replace(/%20/g, '+')}` : location.pathname);
});

onModelProgress((event) => {
  downloads.set(event.part, event);
  if (event.part === 'text' && !ready.text && sampleRoot.classList.contains('is-thinking')) {
    sampleDrive.setReadout([{ text: 'Fetching the search model' }, ` from this site, once: ${downloadText()}.`]);
  }
  if (!prepPanel.hidden) prepNow.textContent = `Loading the model · ${downloadText()}`;
});

// ---- Your own files --------------------------------------------------------------------------------

const yours = document.querySelector('.yours');
const slot = yours.querySelector('.slot');
const input = document.getElementById('own-files');
const prepPanel = yours.querySelector('.prep');
const prepNow = yours.querySelector('.prep__now');
const prepCount = yours.querySelector('.prep__count');
const prepFill = yours.querySelector('.bar-fill');
const ownRoot = document.getElementById('own-drive');
const skippedList = yours.querySelector('.skipped');
const LIMIT = 60;
let own = null;
let ownDrive = null;

async function addFiles(list) {
  const incoming = [...list].slice(0, Math.max(0, LIMIT - (own?.files.length ?? 0)));
  if (!incoming.length) return;
  yours.dataset.state = 'preparing';
  slot.hidden = true;
  ownRoot.hidden = true;
  prepPanel.hidden = false;
  prepFill.style.width = '0%';
  prepCount.textContent = '';
  try {
    const { files, skipped } = await prepare(incoming, (step) => {
      if (step.phase === 'model') { prepNow.textContent = 'Loading the model'; return; }
      if (step.phase === 'file') {
        prepNow.textContent = `${step.name} · ${step.detail}`;
        prepCount.textContent = `${step.done} of ${step.total} files`;
        prepFill.style.width = `${(step.done / step.total) * 100}%`;
      }
    });
    own = { files: [...(own?.files ?? []), ...files] };
    showSkipped(skipped);
  } catch (error) {
    showSkipped([{ name: 'The model', why: `could not load in this browser: ${error.message}` }]);
  }
  prepPanel.hidden = true;
  if (!own?.files.length) { yours.dataset.state = 'empty'; slot.hidden = false; return; }
  yours.dataset.state = 'ready';
  ownRoot.hidden = false;
  const summary = describe(own.files);
  const idle = [{ text: `${own.files.length} of your files` }, `: ${summary}. Prepared in this tab. Search them.`];
  if (ownDrive) ownDrive.update({ library: own, idle });
  else ownDrive = new Drive(ownRoot, { library: own, viewer, suggestions: ['a dog', 'food', 'the beach', 'a city at night', 'money'], idle });
  ownRoot.querySelector('input').focus({ preventScroll: true });
}

function showSkipped(skipped) {
  skippedList.replaceChildren(...skipped.map(({ name, why }) => {
    const item = document.createElement('li');
    item.append(Object.assign(document.createElement('strong'), { textContent: name }), ` skipped: ${why}`);
    return item;
  }));
  skippedList.hidden = !skipped.length;
}

input.addEventListener('change', () => { addFiles(input.files); input.value = ''; });
for (const target of [slot, ownRoot]) {
  target.addEventListener('dragover', (event) => { event.preventDefault(); yours.classList.add('is-over'); });
  target.addEventListener('dragleave', () => yours.classList.remove('is-over'));
  target.addEventListener('drop', (event) => { event.preventDefault(); yours.classList.remove('is-over'); addFiles(event.dataTransfer.files); });
}
yours.querySelector('[data-forget]').addEventListener('click', () => {
  forget(own);
  own = null;
  ownDrive?.update({ library: { files: [] }, idle: [] });
  ownRoot.hidden = true;
  skippedList.hidden = true;
  slot.hidden = false;
  yours.dataset.state = 'empty';
});

// ---- Small things ----------------------------------------------------------------------------------

const tour = document.querySelector('.tour');
const tourVideo = tour.querySelector('video');
document.querySelectorAll('[data-open-tour]').forEach((trigger) => trigger.addEventListener('click', () => {
  tour.showModal();
  tourVideo.play().catch(() => {});
}));
tour.addEventListener('click', (event) => { if (event.target === tour) tour.close(); });
tour.addEventListener('close', () => tourVideo.pause());

document.querySelectorAll('[data-copy]').forEach((button) => button.addEventListener('click', async () => {
  const text = button.parentElement.querySelector('[data-cmd]').textContent.trim();
  try { await navigator.clipboard.writeText(text); button.textContent = 'Copied'; } catch { button.textContent = 'Select it'; }
  window.setTimeout(() => { button.textContent = 'Copy'; }, 1800);
}));
