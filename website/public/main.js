// Page wiring: the sample drive and the tour video.
import { Drive, Viewer } from './demo/drive.js';
import { loadSamples, onModelProgress, ready } from './demo/engine.js';

const bar = document.querySelector('.bar');
const onScroll = () => bar.classList.toggle('is-scrolled', window.scrollY > 8);
onScroll();
window.addEventListener('scroll', onScroll, { passive: true });

const viewer = new Viewer(document.querySelector('.viewer'));

// ---- Sample drive ----------------------------------------------------------------------------------

const sampleRoot = document.getElementById('sample-drive');
const sample = await loadSamples('samples/index.json');
const sampleDrive = new Drive(sampleRoot, {
  library: sample,
  viewer,
  suggestions: ['a tram in the street', 'boats in a harbour at night', 'coffee and cake', 'where are we staying', 'QX-4471'],
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

// The first typed search fetches the text model; say how far along it is.
onModelProgress(({ loaded, total }) => {
  if (!ready.text && sampleRoot.classList.contains('is-thinking')) {
    sampleDrive.setReadout([`Fetching the search model from this site: ${Math.round(loaded / 1e6)} of ${Math.round(total / 1e6)} MB. This happens once.`]);
  }
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
