// Offline image "edits": when no image-generation API is configured, the server returns presets and the
// browser renders stylized variations of the uploaded image on a canvas. Real edits come from the server.

const LABELS = { sketch: 'Qalam chizgi', popart: 'Pop-art', sepia: 'Eski surat', pixel: 'Piksel', mirror: 'Ko\'zgu', noir: 'Oq-qora' };
const presetLabel = (p) => LABELS[p] ?? p;

/** Returns [{ preset, label, url }] (blob URLs). */
export async function renderVariations(srcUrl, presets) {
  const img = await loadImage(srcUrl);
  const max = 900, k = Math.min(1, max / Math.max(img.width, img.height));
  const w = Math.round(img.width * k), h = Math.round(img.height * k);
  const out = [];
  for (const preset of presets) {
    const c = document.createElement('canvas'); c.width = w; c.height = h;
    const g = c.getContext('2d', { willReadFrequently: true });
    g.drawImage(img, 0, 0, w, h);
    FX[preset]?.(g, w, h, img);
    const blob = await new Promise((r) => c.toBlob(r, 'image/jpeg', 0.9));
    out.push({ preset, label: presetLabel(preset), url: URL.createObjectURL(blob) });
  }
  return out;
}

const FX = {
  sepia(g, w, h) { map(g, w, h, (r, gr, b) => [r * .393 + gr * .769 + b * .189, r * .349 + gr * .686 + b * .168, r * .272 + gr * .534 + b * .131]); vignette(g, w, h); },
  noir(g, w, h) { map(g, w, h, (r, gr, b) => { let y = .3 * r + .59 * gr + .11 * b; y = (y - 128) * 1.45 + 128; return [y, y, y]; }); vignette(g, w, h); },
  popart(g, w, h) {
    const pal = [[255, 64, 129], [0, 188, 212], [255, 235, 59], [33, 33, 33]];
    map(g, w, h, (r, gr, b) => { const y = .3 * r + .59 * gr + .11 * b; return pal[y > 190 ? 2 : y > 120 ? 1 : y > 60 ? 0 : 3]; });
  },
  pixel(g, w, h, img) {
    const s = Math.max(8, Math.round(Math.max(w, h) / 64));
    g.imageSmoothingEnabled = false;
    g.drawImage(img, 0, 0, Math.ceil(w / s), Math.ceil(h / s));
    g.drawImage(g.canvas, 0, 0, Math.ceil(w / s), Math.ceil(h / s), 0, 0, w, h);
  },
  mirror(g, w, h, img) { g.save(); g.translate(w, 0); g.scale(-1, 1); g.drawImage(img, 0, 0, w, h); g.restore(); },
  sketch(g, w, h) {                              // grayscale → Sobel edges → inverted pencil lines on paper
    const src = g.getImageData(0, 0, w, h), d = src.data, gray = new Float32Array(w * h);
    for (let i = 0; i < w * h; i++) gray[i] = .3 * d[i * 4] + .59 * d[i * 4 + 1] + .11 * d[i * 4 + 2];
    const out = g.createImageData(w, h);
    for (let y = 1; y < h - 1; y++) for (let x = 1; x < w - 1; x++) {
      const i = y * w + x;
      const gx = -gray[i - w - 1] - 2 * gray[i - 1] - gray[i + w - 1] + gray[i - w + 1] + 2 * gray[i + 1] + gray[i + w + 1];
      const gy = -gray[i - w - 1] - 2 * gray[i - w] - gray[i - w + 1] + gray[i + w - 1] + 2 * gray[i + w] + gray[i + w + 1];
      const v = 255 - Math.min(255, Math.hypot(gx, gy) * 1.2);
      out.data.set([v * .98, v * .96, v * .9, 255], i * 4);
    }
    g.putImageData(out, 0, 0);
  },
};

function map(g, w, h, fn) {
  const im = g.getImageData(0, 0, w, h), d = im.data;
  for (let i = 0; i < d.length; i += 4) { const [r, gr, b] = fn(d[i], d[i + 1], d[i + 2]); d[i] = r; d[i + 1] = gr; d[i + 2] = b; }
  g.putImageData(im, 0, 0);
}
function vignette(g, w, h) {
  const grd = g.createRadialGradient(w / 2, h / 2, Math.min(w, h) * .35, w / 2, h / 2, Math.max(w, h) * .75);
  grd.addColorStop(0, 'rgba(0,0,0,0)'); grd.addColorStop(1, 'rgba(0,0,0,.45)');
  g.fillStyle = grd; g.fillRect(0, 0, w, h);
}
function loadImage(url) {
  return new Promise((res, rej) => { const i = new Image(); i.onload = () => res(i); i.onerror = rej; i.src = url; });
}
