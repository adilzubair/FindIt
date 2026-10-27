// Progressive enhancement only: every section reads fine without this file.
(() => {
  const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  // Masthead rule once the page scrolls.
  const masthead = document.querySelector('.masthead');
  const onScroll = () => masthead.classList.toggle('is-scrolled', window.scrollY > 8);
  onScroll();
  window.addEventListener('scroll', onScroll, { passive: true });

  // Mobile menu closes after choosing a link or pressing Escape.
  const menu = document.querySelector('.nav__menu');
  menu.addEventListener('click', (event) => { if (event.target.closest('a')) menu.open = false; });
  document.addEventListener('keydown', (event) => { if (event.key === 'Escape') menu.open = false; });

  // Hero: the search box types a query and the matching index card comes to the front.
  const catalogue = document.querySelector('.catalogue');
  const queryText = catalogue.querySelector('[data-query]');
  const buttons = [...catalogue.querySelectorAll('[data-show]')];
  const cards = new Map([...catalogue.querySelectorAll('[data-card]')].map((card) => [card.dataset.card, card]));
  let current = 0;
  let typing = 0;
  let timer = 0;
  let autoplay = !reduceMotion;
  let paused = false;
  let visible = true;

  const type = (text, done) => {
    window.clearInterval(typing);
    if (reduceMotion) { queryText.textContent = text; done(); return; }
    let length = 0;
    queryText.textContent = '';
    typing = window.setInterval(() => {
      length += 1;
      queryText.textContent = text.slice(0, length);
      if (length >= text.length) { window.clearInterval(typing); done(); }
    }, 38);
  };

  const show = (index) => {
    const previous = cards.get(buttons[current].dataset.show);
    const next = cards.get(buttons[index].dataset.show);
    current = index;
    buttons.forEach((button, i) => button.setAttribute('aria-pressed', String(i === index)));
    type(buttons[index].textContent.trim(), () => {
      if (previous !== next) {
        previous.classList.remove('is-active');
        previous.classList.add('is-leaving');
        window.setTimeout(() => previous.classList.remove('is-leaving'), 450);
      }
      next.classList.add('is-active');
      schedule();
    });
  };

  const schedule = () => {
    window.clearTimeout(timer);
    if (autoplay && !paused && visible && !document.hidden) {
      timer = window.setTimeout(() => show((current + 1) % buttons.length), 5200);
    }
  };

  buttons.forEach((button, index) => button.addEventListener('click', () => {
    autoplay = false;
    window.clearTimeout(timer);
    show(index);
  }));
  catalogue.addEventListener('pointerenter', () => { paused = true; window.clearTimeout(timer); });
  catalogue.addEventListener('pointerleave', () => { paused = false; schedule(); });
  catalogue.addEventListener('focusin', () => { paused = true; window.clearTimeout(timer); });
  catalogue.addEventListener('focusout', () => { paused = false; schedule(); });
  document.addEventListener('visibilitychange', schedule);
  new IntersectionObserver(([entry]) => { visible = entry.isIntersecting; schedule(); }).observe(catalogue);

  // 01: each filename resolves into what the file actually holds.
  const rows = [...document.querySelector('.names').children];
  if (reduceMotion) {
    rows.forEach((row) => row.classList.add('is-known'));
  } else {
    // Rows that arrive together resolve one after another rather than all at once.
    let queue = Promise.resolve();
    const reveal = new IntersectionObserver((entries) => entries.forEach((entry) => {
      if (!entry.isIntersecting) return;
      reveal.unobserve(entry.target);
      queue = queue.then(() => new Promise((resolve) => window.setTimeout(() => {
        entry.target.classList.add('is-known');
        resolve();
      }, 260)));
    }), { rootMargin: '0px 0px -18% 0px', threshold: 1 });
    rows.forEach((row) => reveal.observe(row));
  }

  // The 38-second tour plays in a dialog.
  const dialog = document.getElementById('tour-dialog');
  const video = dialog.querySelector('video');
  document.querySelectorAll('[data-open-tour]').forEach((trigger) => trigger.addEventListener('click', (event) => {
    if (typeof dialog.showModal !== 'function') return;
    event.preventDefault();
    dialog.showModal();
    video.play().catch(() => {});
  }));
  dialog.addEventListener('click', (event) => { if (event.target === dialog) dialog.close(); });
  dialog.addEventListener('close', () => video.pause());

  // Copy the checksum command.
  document.querySelectorAll('[data-copy]').forEach((button) => button.addEventListener('click', async () => {
    const text = button.parentElement.querySelector('[data-cmd]').textContent.trim();
    try {
      await navigator.clipboard.writeText(text);
      button.textContent = 'Copied';
    } catch {
      button.textContent = 'Select it';
    }
    window.setTimeout(() => { button.textContent = 'Copy'; }, 1800);
  }));

  schedule();
})();
