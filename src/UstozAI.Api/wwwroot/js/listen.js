// Space-hold controller: active-listening body language + live mic ring + client-side cut-off verdict.
//
//   press   → audio unlocked (gesture), avatar = active_listening (leans in, eye contact, attentive tilt)
//   speech  → mic ring pulses with the voice; avatar does a small "mm-hm" nod at the student's pauses
//   release → verdict: too short (< 0.8 s) / silent (< 0.25 s voiced) → instant local reaction, no upload;
//             otherwise the clip + its stats (durationMs, voicedMs, clippedEnd) go to the server
const MIN_DURATION_MS = 500;
const MIN_VOICED_MS = 150;

export class ListeningController {
  #lastMic = '';
  #avatar; #recorder; #wrap; #ring; #t0 = 0; #timer = 0; #clock;

  #wave; #levels = new Array(40).fill(0); #drawRaf = 0;

  constructor({ avatar, recorder, avatarWrap, clockEl, waveCanvas }) {
    this.#wave = waveCanvas;
    this.#avatar = avatar;
    this.#recorder = recorder;
    this.#wrap = avatarWrap;
    this.#clock = clockEl;
    this.#ring = document.createElement('div');
    this.#ring.className = 'mic-ring';
    this.#ring.setAttribute('aria-hidden', 'true');
    avatarWrap.append(this.#ring);
    let smooth = 0;
    recorder.onLevel = (rms, voiced) => {
      smooth = smooth * 0.8 + Math.min(1, rms * 9) * 0.2;
      const mv = smooth.toFixed(2);
      if (mv !== this.#lastMic) { this.#lastMic = mv; this.#wrap.style.setProperty('--mic', mv); }
      this.#avatar.setListenLevel(voiced ? smooth : 0);
      this.#lastLevel = smooth;
    };
    this.#drawIdle();
  }

  #lastLevel = 0;

  /** Bars scroll right→left; height = mic level. Drawn at ~30 fps only while listening. */
  #drawLoop = () => {
    const c = this.#wave; if (!c) return;
    this.#levels.push(this.#lastLevel); this.#levels.shift();
    const g = c.getContext('2d'), w = c.width, h = c.height, n = this.#levels.length, bw = w / n;
    g.clearRect(0, 0, w, h);
    const grad = g.createLinearGradient(0, 0, w, 0);
    grad.addColorStop(0, '#22c58b55'); grad.addColorStop(1, '#22c58b');
    g.fillStyle = grad;
    this.#levels.forEach((v, i) => {
      const bh = Math.max(3, Math.min(1, v * 1.4) * (h - 6));
      const x = i * bw + bw * 0.2, y = (h - bh) / 2;
      g.beginPath(); g.roundRect(x, y, bw * 0.6, bh, 2); g.fill();
    });
    this.#drawRaf = setTimeout(this.#drawLoop, 33);
  };

  #drawIdle() {
    const c = this.#wave; if (!c) return;
    const g = c.getContext('2d'), w = c.width, h = c.height, n = this.#levels.length, bw = w / n;
    g.clearRect(0, 0, w, h);
    g.fillStyle = '#8e9bb455';
    for (let i = 0; i < n; i++) { g.beginPath(); g.roundRect(i * bw + bw * 0.35, h / 2 - 1.5, bw * 0.3, 3, 1.5); g.fill(); }
  }

  async start() {
    await this.#recorder.start();
    clearTimeout(this.#drawRaf); this.#levels.fill(0); this.#drawLoop();
    this.#avatar.setState('listening');
    this.#wrap.dataset.listening = 'true';
    this.#t0 = performance.now();
    clearInterval(this.#timer);
    this.#timer = setInterval(() => {
      if (this.#clock) this.#clock.textContent = `${((performance.now() - this.#t0) / 1000).toFixed(1)} s`;
    }, 100);
  }

  /** @returns {{ verdict: 'ok'|'too_short'|'silent', rec: object|null }} */
  stop() {
    clearInterval(this.#timer);
    clearTimeout(this.#drawRaf); this.#drawIdle();
    if (this.#clock) this.#clock.textContent = '';
    this.#wrap.dataset.listening = 'false';
    this.#lastMic = '0'; this.#wrap.style.setProperty('--mic', '0');
    const rec = this.#recorder.stop();
    if (!rec) return { verdict: 'too_short', rec: null };
    if (rec.durationMs < MIN_DURATION_MS) return { verdict: 'too_short', rec };
    if (rec.voicedMs < MIN_VOICED_MS || !rec.wavBase64) return { verdict: 'silent', rec };
    return { verdict: 'ok', rec };
  }
}
