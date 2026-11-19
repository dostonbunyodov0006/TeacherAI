// "Bloknot": the student's own notes page, opened next to Teacher (like the board). Autosaves while typing.
import * as auth from './api.js';

export class NotebookPanel {
  #panel; #title; #content; #status; #id = null; #timer = 0; #dirty = false;
  onSaved = () => {};          // sidebar refresh (title / order)
  onClosed = () => {};

  constructor(panel) {
    this.#panel = panel;
    this.#title = panel.querySelector('#nb-title');
    this.#content = panel.querySelector('#nb-content');
    this.#status = panel.querySelector('#nb-status');
    const changed = () => { this.#dirty = true; this.#status.textContent = 'Yozilmoqda…'; clearTimeout(this.#timer); this.#timer = setTimeout(() => this.save(), 700); };
    this.#title.addEventListener('input', changed);
    this.#content.addEventListener('input', changed);
    panel.querySelector('#nb-close').onclick = () => this.close();
    panel.querySelector('#nb-delete').onclick = (e) => this.#delete(e.currentTarget);
  }

  get openId() { return this.#panel.hidden ? null : this.#id; }

  async create() {
    const n = await auth.api('/api/notebooks', { method: 'POST' });
    this.#show(n);
    this.#title.select();
    return n;
  }

  async open(id) {
    await this.save();
    const n = await auth.api(`/api/notebooks/${id}`);
    this.#show(n);
  }

  async save() {
    if (!this.#dirty || !this.#id) return;
    this.#dirty = false;
    try {
      await auth.api(`/api/notebooks/${this.#id}`, { method: 'PUT',
        body: JSON.stringify({ title: this.#title.value, content: this.#content.value }) });
      this.#status.textContent = 'Saqlandi ✓';
      this.onSaved();
    } catch { this.#status.textContent = 'Saqlab bo\'lmadi — qayta urinilmoqda'; this.#dirty = true; this.#timer = setTimeout(() => this.save(), 3000); }
  }

  async close() {
    await this.save();
    this.#panel.hidden = true;
    this.#panel.closest('.stage')?.classList.remove('with-notebook');
    this.#id = null;
    this.onClosed();
  }

  #show(n) {
    this.#id = n.id;
    this.#title.value = n.title;
    this.#content.value = n.content;
    this.#status.textContent = '';
    this.#panel.hidden = false;
    this.#panel.closest('.stage')?.classList.add('with-notebook');
    this.#content.focus();
  }

  async #delete(btn) {
    if (!btn.dataset.sure) { btn.dataset.sure = '1'; btn.textContent = 'Rostdan?'; setTimeout(() => { delete btn.dataset.sure; btn.textContent = '🗑'; }, 3000); return; }
    const id = this.#id;
    this.#dirty = false;
    await auth.api(`/api/notebooks/${id}`, { method: 'DELETE' });
    this.#panel.hidden = true;
    this.#id = null;
    this.onSaved();
    this.onClosed();
  }
}
