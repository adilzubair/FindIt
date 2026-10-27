// Page wiring: the sample drive, the visitor's own files, and the tour video.
import { Drive, Viewer, describe } from './demo/drive.js';
import { forget, loadSamples, onModelProgress, prepare, ready } from './demo/engine.js';

const bar = document.querySelector('.bar');
const onScroll = () => bar.classList.toggle('is-scrolled', window.scrollY > 8);
onScroll();
window.addEventListener('scroll', onScroll, { passive: true });

const viewer = new Viewer(document.querySelector('.viewer'));

// Model downloads are reported per file; the page shows them as one figure.
const downloads = new Map();
const downloadText = () => {
  let loaded = 0;
  let total = 0;
  for (const d of downloads.values()) { loaded += d.loaded; total += d.total; }
  return total ? `${Math.round(loaded / 1e6)} of ${Math.round(total / 1e6)} MB` : '';
};

// ---- Sample drive ----------------------------------------------------------------------------------

const sampleRoot = document.getElementById('sample-drive');
const sample = await loadSamples('samples/index.json');
const sampleDrive = new Drive(sampleRoot, {
  library: sample,
  viewer,
  suggestions: ['dog running on the beach', 'elephants crossing the road', 'what to buy for pizza night', 'can I keep a pet', 'AB-0091'],
  idle: `${sample.files.length} files on this drive. None of them named for what's inside.`,
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

// ---- Your own files --------------------------------------------------------------------------------

const yours = document.querySelector('.yours');
const slot = yours.querySelector('.slot');
const input = document.getElementById('own-files');
const prepPanel = yours.querySelector('.prep');
const prepNow = yours.querySelector('.prep__now');
const prepCount = yours.querySelector('.prep__count');
const prepFill = yours.querySelector('.track__fill');
const ownRoot = document.getElementById('own-drive');
const skippedList = yours.querySelector('.skipped');
const LIMIT = 60;
let own = null;
let ownDrive = null;

onModelProgress((event) => {
  downloads.set(event.part, event);
  if (event.part === 'text' && !ready.text && sampleRoot.classList.contains('is-thinking')) {
    sampleDrive.setReadout([`Fetching the search model from this site: ${downloadText()}. This happens once.`]);
  }
  if (!prepPanel.hidden) prepNow.textContent = `Fetching the model · ${downloadText()}`;
});

async function addFiles(list) {
  const incoming = [...list].slice(0, Math.max(0, LIMIT - (own?.files.length ?? 0)));
  if (!incoming.length) return;
  yours.dataset.state = 'preparing';
  slot.hidden = true;
  ownRoot.hidden = true;
  prepPanel.hidden = false;
  prepFill.style.width = '0%';
  prepNow.textContent = 'Loading the model';
  prepCount.textContent = '';
  try {
    const { files, skipped } = await prepare(incoming, (step) => {
      if (step.phase !== 'file') return;
      prepNow.textContent = `${step.name} · ${step.detail}`;
      prepCount.textContent = `${step.done} of ${step.total}`;
      prepFill.style.width = `${(step.done / step.total) * 100}%`;
    });
    own = { files: [...(own?.files ?? []), ...files] };
    showSkipped(skipped);
  } catch (error) {
    showSkipped([{ name: 'The model', why: `could not load in this browser (${error.message})` }]);
  }
  prepPanel.hidden = true;
  if (!own?.files.length) { yours.dataset.state = 'empty'; slot.hidden = false; return; }
  yours.dataset.state = 'ready';
  ownRoot.hidden = false;
  const idle = `${own.files.length} of your files are ready: ${describe(own.files)}. Search them.`;
  if (ownDrive) ownDrive.update({ library: own, idle });
  else ownDrive = new Drive(ownRoot, { library: own, viewer, suggestions: ['a dog', 'food', 'the beach', 'a city at night'], idle });
  ownRoot.querySelector('input').focus({ preventScroll: true });
}

function showSkipped(skipped) {
  skippedList.replaceChildren(...skipped.map(({ name, why }) => {
    const item = document.createElement('li');
    item.append(Object.assign(document.createElement('strong'), { textContent: name }), ` was skipped: ${why}.`);
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
  ownDrive?.update({ library: { files: [] }, idle: '' });
  ownRoot.hidden = true;
  skippedList.hidden = true;
  slot.hidden = false;
  yours.dataset.state = 'empty';
});

// ---- Tour video ------------------------------------------------------------------------------------

const tour = document.querySelector('.tour');
const tourVideo = tour.querySelector('video');
document.querySelectorAll('[data-open-tour]').forEach((trigger) => trigger.addEventListener('click', () => {
  tour.showModal();
  tourVideo.play().catch(() => {});
}));
tour.addEventListener('click', (event) => { if (event.target === tour) tour.close(); });
tour.addEventListener('close', () => tourVideo.pause());
