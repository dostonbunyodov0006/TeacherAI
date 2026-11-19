// Teaching board: renders the LLM's boardContent (safe mini-markdown), reveals it block by block like it is
// being written, and pulses highlight words at the moment Ustoz says them.

const TYPE_LABEL = { grammar: 'Grammatika', vocabulary: "Lug'at", code: 'Kod', quiz: 'Viktorina', report: 'Haftalik hisobot',
  image: 'Rasm' };

const esc = (s) => s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

/** Inline: `code`, **bold**, *italic* — applied to already-escaped text. */
function inline(s) {
  return s
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/(^|[^*])\*([^*\s][^*]*)\*/g, '$1<em>$2</em>');
}

/** Minimal, XSS-safe markdown → HTML blocks (everything is escaped before formatting). */
function renderMarkdown(md) {
  const lines = md.replace(/\r/g, '').split('\n');
  const blocks = [];
  for (let i = 0; i < lines.length;) {
    const line = lines[i];
    if (/^```/.test(line)) {                                       // fenced code
      const lang = esc(line.slice(3).trim());
      const body = [];
      for (i++; i < lines.length && !/^```/.test(lines[i]); i++) body.push(lines[i]);
      i++;
      blocks.push(`<pre data-lang="${lang}"><code>${esc(body.join('\n'))}</code></pre>`);
    } else if (/^\s*\|/.test(line)) {                              // table
      const rows = [];
      for (; i < lines.length && /^\s*\|/.test(lines[i]); i++) rows.push(lines[i]);
      const cells = (r) => r.trim().replace(/^\||\|$/g, '').split('|').map((c) => inline(esc(c.trim())));
      const body = rows.filter((r) => !/^\s*\|?\s*:?-{2,}/.test(r)).map(cells);
      const [head, ...rest] = body;
      blocks.push(`<table><thead><tr>${head.map((c) => `<th>${c}</th>`).join('')}</tr></thead><tbody>${
        rest.map((r) => `<tr>${r.map((c) => `<td>${c}</td>`).join('')}</tr>`).join('')}</tbody></table>`);
    } else if (/^#{1,3}\s/.test(line)) {                           // heading
      const level = line.match(/^#+/)[0].length + 2;               // ## → h4 (board is a sub-panel)
      blocks.push(`<h${level}>${inline(esc(line.replace(/^#+\s*/, '')))}</h${level}>`);
      i++;
    } else if (/^\s*([-*]|\d+\.)\s/.test(line)) {                  // list
      const ordered = /^\s*\d+\./.test(line);
      const items = [];
      for (; i < lines.length && /^\s*([-*]|\d+\.)\s/.test(lines[i]); i++)
        items.push(`<li>${inline(esc(lines[i].replace(/^\s*([-*]|\d+\.)\s/, '')))}</li>`);
      blocks.push(ordered ? `<ol>${items.join('')}</ol>` : `<ul>${items.join('')}</ul>`);
    } else if (!line.trim()) i++;
    else {                                                          // paragraph
      const para = [];
      for (; i < lines.length && lines[i].trim() && !/^(```|#{1,3}\s|\s*\||\s*([-*]|\d+\.)\s)/.test(lines[i]); i++) para.push(lines[i]);
      blocks.push(`<p>${inline(esc(para.join(' ')))}</p>`);
    }
  }
  return blocks;
}

/** Wrap occurrences of highlight words in <mark> (text nodes only, so markup stays intact). */
function highlight(root, words) {
  const list = words.map((w) => w.trim()).filter(Boolean).sort((a, b) => b.length - a.length);
  if (!list.length) return;
  const re = new RegExp(`(${list.map((w) => w.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')})`, 'gi');
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const nodes = [];
  while (walker.nextNode()) nodes.push(walker.currentNode);
  for (const n of nodes) {
    if (!re.test(n.nodeValue)) continue;
    re.lastIndex = 0;
    const frag = document.createDocumentFragment();
    let last = 0;
    n.nodeValue.replace(re, (m, _g, idx) => {
      frag.append(n.nodeValue.slice(last, idx));
      const mark = document.createElement('mark');
      mark.textContent = m;
      mark.dataset.word = m.toLowerCase();
      frag.append(mark);
      last = idx + m.length;
    });
    frag.append(n.nodeValue.slice(last));
    n.replaceWith(frag);
  }
}

export class Board {
  #panel; #title; #badge; #body; #words = [];

  constructor(panel) {
    this.#panel = panel;
    this.#title = panel.querySelector('.board-title');
    this.#badge = panel.querySelector('.board-badge');
    this.#body = panel.querySelector('.board-body');
    panel.querySelector('.board-close').onclick = () => this.hide();
  }

  get visible() { return !this.#panel.hidden; }
  hide() { this.#panel.hidden = true; this.#panel.closest('.stage')?.classList.remove('with-board'); }

  /** content = { type, title, markdownBody, highlightWords } */
  show(content, { animate = true } = {}) {
    this.#open(content.type, content.title);
    this.#words = (content.highlightWords || []).map((w) => w.toLowerCase());
    this.#body.innerHTML = renderMarkdown(content.markdownBody || '').join('');
    highlight(this.#body, content.highlightWords || []);
    this.#reveal(animate);
  }

  /** Called when a speech chunk starts: pulse highlighted words the voice is saying right now. */
  pulse(spokenText) {
    if (!this.visible || !this.#words.length || !spokenText) return;
    const said = spokenText.toLowerCase();
    for (const m of this.#body.querySelectorAll('mark')) {
      if (!said.includes(m.dataset.word)) continue;
      m.classList.remove('pulse'); void m.offsetWidth; m.classList.add('pulse');   // restart animation
    }
  }

  /** Weekly report: stat tiles, 7-day study bars, top mistakes. */
  showReport(r) {
    this.#open('report', `${r.from} — ${r.to}`);
    this.#words = [];
    const delta = r.studyMinutes - r.prevStudyMinutes;
    const tile = (label, value, sub = '') =>
      `<div class="tile"><div class="tile-v">${esc(String(value))}</div><div class="tile-l">${esc(label)}</div>${sub ? `<div class="tile-s">${esc(sub)}</div>` : ''}</div>`;
    const max = Math.max(1, ...r.days.map((d) => d.studyMinutes));
    const dayName = (iso) => ['Ya', 'Du', 'Se', 'Ch', 'Pa', 'Ju', 'Sh'][new Date(`${iso}T12:00:00`).getDay()];
    this.#body.innerHTML = `
      <div class="tiles">
        ${tile("O'qish", `${r.studyMinutes} daq`, `${delta >= 0 ? '+' : ''}${delta} o'tgan haftaga nisbatan`)}
        ${tile('Faol kunlar', `${r.activeDays}/7`)}
        ${tile('Uy vazifasi', `${r.homework.completionRate}%`, `${r.homework.completed} ✓ · ${r.homework.failed} ✗ · ${r.homework.pending} ⏳`)}
        ${tile('Viktorina', `${r.quizzes.correct}/${r.quizzes.total}`, r.quizzes.avgAnswerSeconds != null ? `o'rtacha ${r.quizzes.avgAnswerSeconds} s` : '')}
      </div>
      <div class="bars" role="img" aria-label="Kunlik o'qish daqiqalari">
        ${r.days.map((d) => `<div class="bar-col" title="${esc(d.date)}: ${d.studyMinutes} daq">
          <div class="bar" style="height:${Math.round((d.studyMinutes / max) * 100)}%"></div><span>${dayName(d.date)}</span></div>`).join('')}
      </div>
      <h4>Eng ko'p takrorlangan xatolar</h4>
      ${r.topMistakes.length ? `<ol class="mistakes">${r.topMistakes.map((m) =>
        `<li><s>${esc(m.userSaid)}</s> → <strong>${esc(m.correctForm)}</strong> <span class="cnt">×${m.occurrenceCount}</span></li>`).join('')}</ol>`
        : '<p>Bu hafta takroriy xato yo\'q.</p>'}`;
    this.#reveal(true);
  }

  /** Placeholder while the image API works (it runs in parallel with Ustoz's voice). */
  showImagesPending(prompt, count = 1) {
    this.#open('image', 'Rasm tayyorlanmoqda…');
    this.#words = [];
    this.#body.innerHTML = `<p class="img-prompt"></p><div class="gallery">${
      Array.from({ length: Math.min(4, count) }, () => '<div class="shot skeleton"></div>').join('')}</div>`;
    this.#body.querySelector('.img-prompt').textContent = prompt;
  }

  /** items = [{ url, label? }] — server images (signed URLs) or browser-rendered variations (blob URLs). */
  showImages(prompt, items) {
    this.#open('image', items.length > 1 ? `${items.length} ta variant` : 'Natija');
    this.#words = [];
    this.#body.innerHTML = '';
    const p = document.createElement('p'); p.className = 'img-prompt'; p.textContent = prompt || '';
    const grid = document.createElement('div'); grid.className = 'gallery';
    items.forEach((it, i) => {
      const fig = document.createElement('figure'); fig.className = 'shot write-in';
      fig.style.animationDelay = `${i * 120}ms`;
      const img = document.createElement('img'); img.src = it.url; img.alt = it.label || `Variant ${i + 1}`; img.loading = 'lazy';
      img.onclick = () => lightbox(it.url, img.alt);
      const cap = document.createElement('figcaption');
      cap.textContent = it.label || `Variant ${i + 1}`;
      const dl = document.createElement('a'); dl.href = it.url; dl.download = `ustoz-${i + 1}.jpg`; dl.textContent = '⬇'; dl.title = 'Yuklab olish';
      cap.append(' ', dl);
      fig.append(img, cap);
      grid.append(fig);
    });
    this.#body.append(p, grid);
  }

  #open(type, title) {
    this.#panel.hidden = false;
    this.#panel.closest('.stage')?.classList.add('with-board');
    this.#panel.dataset.type = type;
    this.#badge.textContent = TYPE_LABEL[type] ?? type;
    this.#title.textContent = title || '';
  }

  #reveal(animate) {
    const kids = [...this.#body.children];
    kids.forEach((el, i) => {
      el.classList.toggle('write-in', animate);
      el.style.animationDelay = animate ? `${i * 160}ms` : '';
    });
    this.#body.scrollTop = 0;
  }
}

/** Full-screen viewer; Esc / click closes. */
export function lightbox(url, alt = '') {
  const d = document.createElement('div');
  d.className = 'lightbox'; d.setAttribute('role', 'dialog'); d.setAttribute('aria-label', alt || 'Rasm');
  const img = document.createElement('img'); img.src = url; img.alt = alt;
  d.append(img);
  const close = () => { d.remove(); window.removeEventListener('keydown', onKey, true); };
  const onKey = (e) => { if (e.key === 'Escape') { e.stopPropagation(); close(); } };
  d.onclick = close;
  window.addEventListener('keydown', onKey, true);
  document.body.append(d);
}
