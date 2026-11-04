/* Anchor — product site interactions. No dependencies. */
(() => {
  'use strict';

  const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const $ = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

  /* ---------- Reveal on scroll ---------- */
  const reveals = $$('.reveal');
  if ('IntersectionObserver' in window && !reduceMotion) {
    const io = new IntersectionObserver((entries) => {
      for (const e of entries) {
        if (e.isIntersecting) {
          e.target.classList.add('is-in');
          io.unobserve(e.target);
        }
      }
    }, { rootMargin: '0px 0px -8% 0px', threshold: 0 });
    reveals.forEach((el) => io.observe(el));
  } else {
    reveals.forEach((el) => el.classList.add('is-in'));
  }

  /* ---------- Navigation: theme follows the section under it, mobile menu ---------- */
  const nav = $('#nav');
  const toggle = $('.nav__toggle', nav);
  const darkStages = $$('.hero, .stage--night, .stage--night-2, .oss, .notfound');

  function updateNavTheme() {
    const probe = nav.offsetHeight / 2;
    const onDark = darkStages.some((s) => {
      const r = s.getBoundingClientRect();
      return r.top <= probe && r.bottom >= probe;
    });
    nav.classList.toggle('is-light', !onDark);
  }

  toggle.addEventListener('click', () => {
    const open = nav.classList.toggle('is-open');
    toggle.setAttribute('aria-expanded', String(open));
  });
  $$('.nav__links a', nav).forEach((a) =>
    a.addEventListener('click', () => {
      nav.classList.remove('is-open');
      toggle.setAttribute('aria-expanded', 'false');
    })
  );
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && nav.classList.contains('is-open')) {
      nav.classList.remove('is-open');
      toggle.setAttribute('aria-expanded', 'false');
      toggle.focus();
    }
  });

  /* ---------- Hero screen settles flat as you scroll ---------- */
  const heroScreen = $('#hero-screen');

  function updateHero() {
    if (!heroScreen || reduceMotion) return;
    const r = heroScreen.getBoundingClientRect();
    const vh = window.innerHeight;
    // 0 when the screen's top enters the viewport's lower edge, 1 when it reaches ~20% from the top.
    const p = Math.min(1, Math.max(0, (vh - r.top) / (vh * 0.8)));
    const eased = 1 - Math.pow(1 - p, 3);
    heroScreen.style.setProperty('--hero-scale', (0.88 + 0.12 * eased).toFixed(4));
    heroScreen.style.setProperty('--hero-tilt', (10 * (1 - eased)).toFixed(3) + 'deg');
  }

  /* ---------- Statement: words light up as they cross the viewport ---------- */
  const statement = $('#statement');
  let words = [];
  if (statement) {
    // Split into words, keeping <mark>ed phrases as highlighted words.
    const parts = [];
    statement.childNodes.forEach((n) => {
      const hl = n.nodeType === 1 && n.tagName === 'MARK';
      (n.textContent || '').trim().split(/\s+/).filter(Boolean).forEach((w) => parts.push([w, hl]));
    });
    const full = statement.textContent.trim().replace(/\s+/g, ' ');
    statement.textContent = '';
    const sr = document.createElement('span');
    sr.className = 'visually-hidden';
    sr.textContent = full;
    statement.appendChild(sr);
    parts.forEach(([w, hl], i) => {
      const span = document.createElement('span');
      span.className = hl ? 'w hl' : 'w';
      span.textContent = w;
      span.setAttribute('aria-hidden', 'true');
      statement.appendChild(span);
      if (i < parts.length - 1) statement.appendChild(document.createTextNode(' '));
    });
    words = $$('.w', statement);
  }

  function updateStatement() {
    if (!words.length) return;
    if (reduceMotion) { words.forEach((w) => w.style.setProperty('--lit', 1)); return; }
    const r = statement.getBoundingClientRect();
    const vh = window.innerHeight;
    const start = vh * 0.85;
    const end = vh * 0.35;
    const p = Math.min(1, Math.max(0, (start - r.top) / (start - end + r.height * 0.6)));
    const litCount = p * words.length;
    words.forEach((w, i) => {
      const t = Math.min(1, Math.max(0, litCount - i));
      w.style.setProperty('--lit', (0.16 + 0.84 * t).toFixed(3));
    });
  }

  /* ---------- One rAF-throttled scroll loop ---------- */
  let ticking = false;
  function onScroll() {
    if (ticking) return;
    ticking = true;
    requestAnimationFrame(() => {
      updateNavTheme();
      updateHero();
      updateStatement();
      ticking = false;
    });
  }
  window.addEventListener('scroll', onScroll, { passive: true });
  window.addEventListener('resize', onScroll);
  onScroll();

  /* ---------- Language tile: the app's own translated search placeholder ---------- */
  const phrases = [
    ['en', 'Search your dock…'],
    ['de', 'Dock durchsuchen…'],
    ['es', 'Buscar en tu dock…'],
    ['fr', 'Rechercher dans votre dock…'],
    ['hi', 'अपने डॉक में खोजें…'],
    ['ja', 'ドックを検索…'],
    ['pt-BR', 'Buscar no seu dock…'],
    ['zh-Hans', '搜索你的程序坞…'],
  ];
  const searchText = $('#search-text');
  const langItems = $$('#lang-list li');
  if (searchText && langItems.length) {
    let idx = 0;
    const show = (i) => {
      langItems.forEach((li, j) => li.classList.toggle('is-on', j === i));
      searchText.lang = phrases[i][0];
      searchText.textContent = phrases[i][1];
    };
    show(0);
    if (!reduceMotion) {
      setInterval(() => {
        if (document.hidden) return;
        searchText.classList.add('is-out');
        setTimeout(() => {
          idx = (idx + 1) % phrases.length;
          show(idx);
          searchText.classList.remove('is-out');
        }, 350);
      }, 2200);
    }
  }

  /* ---------- Closer-look tabs (WAI-ARIA tabs pattern) ---------- */
  const tabs = $$('.closer__tab');
  function selectTab(tab, focus) {
    tabs.forEach((t) => {
      const on = t === tab;
      t.setAttribute('aria-selected', String(on));
      t.tabIndex = on ? 0 : -1;
      const panel = document.getElementById(t.getAttribute('aria-controls'));
      if (!panel) return;
      panel.hidden = !on;
      if (on) {
        panel.classList.remove('is-entering');
        void panel.offsetWidth; // restart the entrance animation
        panel.classList.add('is-entering');
      }
    });
    if (focus) tab.focus();
    tab.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: reduceMotion ? 'auto' : 'smooth' });
  }
  tabs.forEach((tab, i) => {
    tab.addEventListener('click', () => selectTab(tab, false));
    tab.addEventListener('keydown', (e) => {
      let next = null;
      if (e.key === 'ArrowRight') next = tabs[(i + 1) % tabs.length];
      if (e.key === 'ArrowLeft') next = tabs[(i - 1 + tabs.length) % tabs.length];
      if (e.key === 'Home') next = tabs[0];
      if (e.key === 'End') next = tabs[tabs.length - 1];
      if (next) { e.preventDefault(); selectTab(next, true); }
    });
  });
  // Deep link from "Tour every page" etc. keeps working; nothing else to wire.

  /* ---------- Magnification playground ---------- */
  const dock = $('#demo-dock');
  const magToggle = $('#mag-toggle');
  if (dock) {
    const cells = $$('.demo-dock__cell', dock);
    const MAX = 1.55;
    const RANGE = 150; // px either side of the cursor that feels the swell

    const reset = () => cells.forEach((c) => c.style.removeProperty('--s'));

    dock.addEventListener('pointermove', (e) => {
      if (!magToggle.checked || e.pointerType === 'touch') return;
      for (const c of cells) {
        const r = c.getBoundingClientRect();
        const d = Math.abs(e.clientX - (r.left + r.width / 2));
        const k = Math.max(0, 1 - d / RANGE);
        // Cosine falloff reads smoother than linear.
        const s = 1 + (MAX - 1) * (0.5 - 0.5 * Math.cos(Math.PI * k));
        c.style.setProperty('--s', s.toFixed(3));
      }
    });
    dock.addEventListener('pointerleave', reset);
    magToggle.addEventListener('change', () => {
      dock.classList.toggle('is-flat', !magToggle.checked);
      reset();
    });

    // Clicking an icon toggles its "running" dot, the way launching an app would.
    cells.forEach((c) => {
      if ($('use', c).getAttribute('href') === '#app-gear') return;
      c.addEventListener('click', () => c.classList.toggle('is-running'));
    });
  }

  /* ---------- Edge-snap diagram cycles through positions ---------- */
  const edges = $('#edges');
  const edgesLabel = $('#edges-label');
  if (edges) {
    const seq = [
      ['bottom', 'Snapped: bottom'],
      ['float', 'Floating'],
      ['right', 'Snapped: right'],
      ['top', 'Snapped: top'],
      ['left', 'Snapped: left'],
    ];
    let k = 0;
    let timer = null;
    const step = () => {
      k = (k + 1) % seq.length;
      edges.dataset.edge = seq[k][0];
      edgesLabel.textContent = seq[k][1];
    };
    if (!reduceMotion && 'IntersectionObserver' in window) {
      new IntersectionObserver(([entry]) => {
        if (entry.isIntersecting && !timer) timer = setInterval(step, 1900);
        if (!entry.isIntersecting && timer) { clearInterval(timer); timer = null; }
      }, { threshold: 0.3 }).observe(edges);
    }
  }

  /* ---------- Settings carousel ---------- */
  const carousel = $('#carousel');
  const prev = $('#car-prev');
  const next = $('#car-next');
  if (carousel && prev && next) {
    const slideWidth = () => {
      const s = $('.slide', carousel);
      return s ? s.getBoundingClientRect().width + 20 : carousel.clientWidth * 0.8;
    };
    const sync = () => {
      const max = carousel.scrollWidth - carousel.clientWidth - 2;
      prev.disabled = carousel.scrollLeft <= 2;
      next.disabled = carousel.scrollLeft >= max;
    };
    prev.addEventListener('click', () => carousel.scrollBy({ left: -slideWidth(), behavior: reduceMotion ? 'auto' : 'smooth' }));
    next.addEventListener('click', () => carousel.scrollBy({ left: slideWidth(), behavior: reduceMotion ? 'auto' : 'smooth' }));
    carousel.addEventListener('scroll', sync, { passive: true });
    window.addEventListener('resize', sync);
    sync();
  }
})();
