// Left sidebar (Gemini-style): New chat · Weekly report · Notebooks (+ New notebook) · All chats (flat, newest
// activity first, ⋮ menu: rename / delete) · user + settings at the bottom. Collapsible; drawer on mobile.
const MONTHS = ['yanvar', 'fevral', 'mart', 'aprel', 'may', 'iyun', 'iyul', 'avgust', 'sentabr', 'oktabr', 'noyabr', 'dekabr'];
const WEEKDAYS = ['Yakshanba', 'Dushanba', 'Seshanba', 'Chorshanba', 'Payshanba', 'Juma', 'Shanba'];

/** "2026-11-19" → local Date (no UTC shift). */
const parseDay = (iso) => { const [y, m, d] = iso.split('-').map(Number); return new Date(y, m - 1, d); };
export const localISO = (d = new Date()) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
/** "19-noyabr" (+ year when not the current one) */
export function shortDate(iso, todayIso) {
  const d = parseDay(iso), t = parseDay(todayIso);
  return `${d.getDate()}-${MONTHS[d.getMonth()]}${d.getFullYear() !== t.getFullYear() ? ` ${d.getFullYear()}` : ''}`;
}
/** "Seshanba, 19-noyabr 2026" */
export const longDate = (iso) => {
  const d = parseDay(iso);
  return `${WEEKDAYS[d.getDay()]}, ${d.getDate()}-${MONTHS[d.getMonth()]} ${d.getFullYear()}`;
};
function dayLabel(iso, todayIso) {
  const diff = Math.round((parseDay(todayIso) - parseDay(iso)) / 86_400_000);
  if (diff <= 0) return 'Bugun';
  if (diff === 1) return 'Kecha';
  if (diff < 7) return `${WEEKDAYS[parseDay(iso).getDay()]}, ${shortDate(iso, todayIso)}`;
  return shortDate(iso, todayIso);
}

const store = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { localStorage.setItem(k, v); } catch { /* private mode */ } },
};

export class Sidebar {
  #app; #list; #more; #nbList; #opts;
  #items = []; #today = null; #next = null; #activeId = null; #activeNb = null;

  /**
   * @param {{ app: HTMLElement, fetchPage: (before?: string) => Promise<any>, onSelect: (id: string) => void,
   *           onRename: (id: string, title: string) => Promise<void>, onDelete: (id: string) => Promise<void>,
   *           fetchNotebooks: () => Promise<any[]>, onOpenNotebook: (id: string) => void }} o
   */
  constructor(o) {
    this.#opts = o;
    this.#app = o.app;
    this.#list = document.getElementById('conversation-list');
    this.#more = document.getElementById('load-more');
    this.#nbList = document.getElementById('notebook-list');
    if (store.get('ustoz.sb') === 'collapsed') o.app.classList.add('sb-collapsed');
    if (store.get('ustoz.tr') === 'hidden') o.app.classList.add('tr-hidden');
    document.getElementById('sidebar-toggle').onclick = () => this.toggleCollapsed();
    document.getElementById('sidebar-open').onclick = () => o.app.classList.add('sb-open');
    document.getElementById('scrim').onclick = () => o.app.classList.remove('sb-open');
    document.getElementById('transcript-toggle').onclick = () => this.toggleTranscript();
    this.#more.onclick = () => this.loadMore();
    window.addEventListener('keydown', (e) => {
      if (!(e.ctrlKey || e.metaKey)) return;
      if (e.key === 'b') { e.preventDefault(); this.toggleCollapsed(); }
      if (e.key === '.') { e.preventDefault(); this.toggleTranscript(); }
    });
    document.addEventListener('click', (e) => { if (!e.target.closest('.conv-menu, .conv-more')) this.#closeMenus(); });
  }

  get today() { return this.#today; }
  toggleCollapsed() { store.set('ustoz.sb', this.#app.classList.toggle('sb-collapsed') ? 'collapsed' : 'open'); }
  toggleTranscript() { store.set('ustoz.tr', this.#app.classList.toggle('tr-hidden') ? 'hidden' : 'shown'); }
  closeMobile() { this.#app.classList.remove('sb-open'); }

  async refresh() {
    const page = await this.#opts.fetchPage();
    this.#items = [];
    this.#merge(page);
    this.#render();
  }
  async loadMore() {
    if (!this.#next) return;
    this.#merge(await this.#opts.fetchPage(this.#next));
    this.#render();
  }
  setActive(id) {
    this.#activeId = id;
    this.#activeNb = null;
    this.#markActive();
  }
  setActiveNotebook(id) {
    this.#activeNb = id;
    this.#markActive();
  }

  async refreshNotebooks() {
    const list = await this.#opts.fetchNotebooks().catch(() => []);
    this.#nbList.replaceChildren(...list.map((n) => {
      const li = document.createElement('li');
      const b = document.createElement('button');
      b.className = 'nb-item'; b.dataset.id = n.id; b.title = n.title;
      b.innerHTML = '<span aria-hidden="true">📓</span> ';
      const t = document.createElement('span'); t.className = 'label'; t.textContent = n.title || 'Nomsiz bloknot';
      b.append(t);
      b.onclick = () => { this.closeMobile(); this.#opts.onOpenNotebook(n.id); };
      li.append(b);
      return li;
    }));
    this.#markActive();
  }

  #merge(page) {
    this.#today = page.today;
    this.#next = page.nextBefore;
    for (const g of page.groups) for (const it of g.items) if (!this.#items.some((x) => x.id === it.id)) this.#items.push(it);
  }

  #render() {
    if (!this.#items.length) {
      const p = document.createElement('div');
      p.className = 'conv-empty';
      p.textContent = 'Hali suhbatlar yo\'q.';
      this.#list.replaceChildren(p);
    } else this.#list.replaceChildren(...this.#items.map((it) => this.#item(it)));
    this.#more.hidden = !this.#next;
    this.#markActive();
  }

  #markActive() {
    this.#list.querySelectorAll('.conv').forEach((b) => b.classList.toggle('active', !this.#activeNb && b.dataset.id === this.#activeId));
    this.#nbList?.querySelectorAll('.nb-item').forEach((b) => b.classList.toggle('active', b.dataset.id === this.#activeNb));
  }

  #closeMenus() { this.#list.querySelectorAll('.conv-menu').forEach((m) => m.remove()); }

  #item(it) {
    const row = document.createElement('div');
    row.className = 'conv';
    row.dataset.id = it.id;
    const title = document.createElement('button');
    title.className = 'conv-title';
    title.textContent = it.title || (it.messageCount ? 'Suhbat' : 'Yangi suhbat');
    title.title = `${it.title || 'Yangi suhbat'} — ${dayLabel(it.date, this.#today)}`;
    title.onclick = () => { this.closeMobile(); this.#opts.onSelect(it.id); };
    const more = document.createElement('button');
    more.className = 'conv-more icon'; more.setAttribute('aria-label', 'Amallar'); more.textContent = '⋮';
    more.onclick = (e) => { e.stopPropagation(); this.#menu(row, it, title); };
    row.append(title, more);
    return row;
  }

  #menu(row, it, titleBtn) {
    this.#closeMenus();
    const m = document.createElement('div');
    m.className = 'conv-menu';
    const rename = document.createElement('button'); rename.textContent = '✎ Nomini o\'zgartirish';
    const del = document.createElement('button'); del.textContent = '🗑 O\'chirish'; del.className = 'danger';
    rename.onclick = () => {
      m.remove();
      const input = document.createElement('input');
      input.className = 'conv-rename'; input.value = it.title || ''; input.maxLength = 200;
      titleBtn.replaceWith(input); input.focus(); input.select();
      const finish = async (save) => {
        if (save && input.value.trim() && input.value.trim() !== it.title) {
          await this.#opts.onRename(it.id, input.value.trim());
          it.title = input.value.trim();
        }
        titleBtn.textContent = it.title || 'Yangi suhbat';
        input.replaceWith(titleBtn);
      };
      input.onkeydown = (e) => { if (e.key === 'Enter') finish(true); if (e.key === 'Escape') finish(false); e.stopPropagation(); };
      input.onblur = () => finish(true);
    };
    del.onclick = async () => {
      if (!del.dataset.sure) { del.dataset.sure = '1'; del.textContent = 'Rostdan o\'chirilsinmi?'; return; }   // 2-step, no dialog
      m.remove();
      await this.#opts.onDelete(it.id);
      this.#items = this.#items.filter((x) => x.id !== it.id);
      this.#render();
    };
    m.append(rename, del);
    row.append(m);
  }
}
