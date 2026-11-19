// Image attachments: drag & drop, paste (Ctrl+V), file picker. Images are downsized in the browser to the
// vision model's sweet spot (≤1568 px long edge) and re-encoded — which also strips EXIF metadata (GPS etc.)
// before anything leaves the device. Uploaded right away so the next Space turn only sends ids.
import * as auth from './api.js';

const MAX_EDGE = 1568;
const MAX_FILES = 4;
const ACCEPT = ['image/jpeg', 'image/png', 'image/webp', 'image/gif'];

export class Attachments {
  #tray; #items = [];          // { id?, url?, localUrl, status: 'uploading'|'ready'|'error', el }
  #getSessionId;
  onAdded = () => {};          // fired when an image is attached (avatar leans in)
  onError = (_msg) => {};

  /** @param {{ tray: HTMLElement, dropTarget: HTMLElement, fileInput: HTMLInputElement, button: HTMLElement, getSessionId: () => string|null }} o */
  constructor({ tray, dropTarget, fileInput, button, getSessionId }) {
    this.#tray = tray;
    this.#getSessionId = getSessionId;
    button.onclick = () => fileInput.click();
    fileInput.onchange = () => { this.addFiles([...fileInput.files]); fileInput.value = ''; };

    let depth = 0;
    dropTarget.addEventListener('dragenter', (e) => { if (hasFiles(e)) { depth++; dropTarget.classList.add('dragging'); } });
    dropTarget.addEventListener('dragleave', () => { if (--depth <= 0) { depth = 0; dropTarget.classList.remove('dragging'); } });
    dropTarget.addEventListener('dragover', (e) => { if (hasFiles(e)) e.preventDefault(); });
    dropTarget.addEventListener('drop', (e) => {
      if (!hasFiles(e)) return;
      e.preventDefault(); depth = 0; dropTarget.classList.remove('dragging');
      this.addFiles([...e.dataTransfer.files]);
    });
    window.addEventListener('paste', (e) => {
      const files = [...(e.clipboardData?.files || [])].filter((f) => f.type.startsWith('image/'));
      if (files.length) { e.preventDefault(); this.addFiles(files); }
    });
  }

  get busy() { return this.#items.some((i) => i.status === 'uploading'); }

  /** Ids for the next turn; clears the tray (images move into the chat bubble). */
  take() {
    const ready = this.#items.filter((i) => i.status === 'ready');
    const out = ready.map((i) => ({ id: i.id, url: i.url, localUrl: i.localUrl }));
    this.#items.forEach((i) => i.el.remove());
    this.#items = [];
    this.#tray.hidden = true;
    return out;
  }

  async addFiles(files) {
    for (const file of files) {
      if (this.#items.length >= MAX_FILES) { this.onError(`Bir xabarga ko'pi bilan ${MAX_FILES} ta rasm.`); break; }
      if (!ACCEPT.includes(file.type)) { this.onError('Faqat JPEG, PNG, WebP yoki GIF rasm.'); continue; }
      const item = { status: 'uploading', localUrl: URL.createObjectURL(file) };
      item.el = this.#thumb(item);
      this.#items.push(item);
      this.#tray.hidden = false;
      this.onAdded();
      try {
        const blob = file.type === 'image/gif' ? file : await downscale(file);   // keep GIFs as-is
        const form = new FormData();
        form.append('file', blob, 'image');
        const sid = this.#getSessionId();
        if (sid) form.append('sessionId', sid);
        const r = await fetch('/api/media', {
          method: 'POST', body: form,
          headers: { Authorization: `Bearer ${await auth.token()}` },
        });
        if (!r.ok) throw new Error((await r.text()) || `HTTP ${r.status}`);
        const ref = await r.json();
        Object.assign(item, { id: ref.id, url: ref.url, status: 'ready' });
      } catch (e) {
        item.status = 'error';
        this.onError(`Rasm yuklanmadi: ${String(e.message || e).replace(/^"|"$/g, '')}`);
      }
      item.el.dataset.status = item.status;
    }
  }

  #thumb(item) {
    const el = document.createElement('div');
    el.className = 'att';
    el.dataset.status = 'uploading';
    const img = document.createElement('img');
    img.src = item.localUrl; img.alt = '';
    const x = document.createElement('button');
    x.type = 'button'; x.className = 'att-x'; x.setAttribute('aria-label', 'Olib tashlash'); x.textContent = '×';
    x.onclick = () => {
      this.#items = this.#items.filter((i) => i !== item);
      el.remove();
      if (item.id) auth.api(`/api/media/${item.id}`, { method: 'DELETE' }).catch(() => {});
      if (!this.#items.length) this.#tray.hidden = true;
    };
    el.append(img, x);
    this.#tray.append(el);
    return el;
  }
}

const hasFiles = (e) => [...(e.dataTransfer?.types || [])].includes('Files');

async function downscale(file) {
  const bmp = await createImageBitmap(file);
  const k = Math.min(1, MAX_EDGE / Math.max(bmp.width, bmp.height));
  const w = Math.round(bmp.width * k), h = Math.round(bmp.height * k);
  const canvas = new OffscreenCanvas(w, h);
  canvas.getContext('2d').drawImage(bmp, 0, 0, w, h);
  bmp.close();
  const type = file.type === 'image/png' ? 'image/png' : 'image/jpeg';    // keep PNG for screenshots/diagrams
  return canvas.convertToBlob({ type, quality: 0.86 });
}
