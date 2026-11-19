// Non-verbal vocalizations: laugh / sigh / gasp / scoff.
// Procedural voice synthesis (glottal sawtooth → /a/ formant filters + breath noise), so laughter
//            is still a *voice* with natural pitch fall and breath, never TTS reading "ha-ha-ha".
// Everything is routed into the shared analyser, so the avatar's mouth/jaw follow the actual sound.

export class VocalFx {
  #ctx; #out; #noise;
  #active = new Set();

  /** @param {AudioContext} ctx  @param {AudioNode} out  (the analyser feeding the speakers) */
  constructor(ctx, out) {
    this.#ctx = ctx; this.#out = out;
    const len = ctx.sampleRate * 2, buf = ctx.createBuffer(1, len, ctx.sampleRate), d = buf.getChannelData(0);
    for (let i = 0; i < len; i++) d[i] = Math.random() * 2 - 1;
    this.#noise = buf;
  }

  /** Plays the vocalization; resolves when it ends. */
  async play(type, intensity = 1) {
    if (type === 'pause') return wait(350 * intensity);
    switch (type) {
      case 'laugh': return this.#laugh(intensity);
      case 'sigh': return this.#sigh();
      case 'gasp': return this.#gasp();
      case 'scoff': return this.#scoff();
      case 'slurp': return this.#slurp();
    }
  }

  stop() { for (const n of this.#active) { try { n.stop(); } catch { /* already stopped */ } } this.#active.clear(); }

  #track(node, endTime) {
    this.#active.add(node);
    node.onended = () => this.#active.delete(node);
    return wait((endTime - this.#ctx.currentTime) * 1000);
  }

  // ---------- procedural voice ----------
  /** Glottal source → three /a/ formants → gain. Returns { input frequency param, gain node, stop(t) }. */
  #voice(t0, f0, vowel = [780, 1220, 2600]) {
    const ctx = this.#ctx;
    const osc = ctx.createOscillator(); osc.type = 'sawtooth'; osc.frequency.setValueAtTime(f0, t0);
    const vib = ctx.createOscillator(); vib.frequency.value = 5.5;           // slight jitter = less robotic
    const vibG = ctx.createGain(); vibG.gain.value = f0 * 0.012; vib.connect(vibG).connect(osc.frequency);
    const out = ctx.createGain(); out.gain.value = 0;
    const gains = [1, 0.55, 0.22], qs = [7, 9, 12];
    vowel.forEach((fr, i) => {
      const bp = ctx.createBiquadFilter(); bp.type = 'bandpass'; bp.frequency.value = fr; bp.Q.value = qs[i];
      const g = ctx.createGain(); g.gain.value = gains[i] * 3.2;
      osc.connect(bp).connect(g).connect(out);
    });
    out.connect(this.#out);
    osc.start(t0); vib.start(t0);
    return { osc, out, stop: (t) => { osc.stop(t); vib.stop(t); this.#active.add(osc); osc.onended = () => this.#active.delete(osc); } };
  }

  #breath(t0, dur, from, to, peak, q = 1.2) {
    const ctx = this.#ctx;
    const src = ctx.createBufferSource(); src.buffer = this.#noise;
    const bp = ctx.createBiquadFilter(); bp.type = 'bandpass'; bp.Q.value = q;
    bp.frequency.setValueAtTime(from, t0); bp.frequency.exponentialRampToValueAtTime(to, t0 + dur);
    const g = ctx.createGain();
    g.gain.setValueAtTime(0.0001, t0);
    g.gain.exponentialRampToValueAtTime(peak, t0 + Math.min(0.12, dur * 0.3));
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
    src.connect(bp).connect(g).connect(this.#out);
    src.start(t0, Math.random()); src.stop(t0 + dur + 0.05);
    this.#active.add(src); src.onended = () => this.#active.delete(src);
  }

  #laugh(intensity) {
    const ctx = this.#ctx, t0 = ctx.currentTime + 0.02;
    const bursts = 3 + intensity * 2 + Math.floor(Math.random() * 2);
    const spacing = intensity === 3 ? 0.17 : 0.2;
    const v = this.#voice(t0, 185);
    let t = t0;
    for (let i = 0; i < bursts; i++) {
      const p = i / bursts;
      const f0 = (intensity === 3 ? 230 : 195) * (1 - 0.28 * p) * (0.96 + Math.random() * 0.08); // pitch falls
      const amp = (0.5 + 0.5 * (1 - p)) * (0.7 + 0.15 * intensity);
      const voiced = 0.085 + Math.random() * 0.03;
      this.#breath(t, 0.07, 1800, 1400, 0.12 * amp);                // the "h" of "ha"
      v.osc.frequency.setValueAtTime(f0 * 1.06, t + 0.04);
      v.osc.frequency.exponentialRampToValueAtTime(f0, t + 0.04 + voiced);
      v.out.gain.setValueAtTime(0.0001, t + 0.035);
      v.out.gain.exponentialRampToValueAtTime(0.09 * amp, t + 0.05);
      v.out.gain.exponentialRampToValueAtTime(0.0001, t + 0.05 + voiced);
      t += spacing * (0.9 + Math.random() * 0.2);
    }
    if (intensity >= 2) { this.#breath(t + 0.05, 0.38, 900, 2600, 0.09, 0.8); t += 0.45; }  // gasping inhale
    v.stop(t + 0.05);
    return wait((t + 0.08 - ctx.currentTime) * 1000);
  }

  #sigh() {
    const ctx = this.#ctx, t0 = ctx.currentTime + 0.02, dur = 1.05;
    this.#breath(t0, dur, 1500, 420, 0.22, 1.1);
    const v = this.#voice(t0, 125, [520, 1000, 2400]);               // faint voiced "uhh", falling
    v.osc.frequency.exponentialRampToValueAtTime(95, t0 + dur);
    v.out.gain.setValueAtTime(0.0001, t0);
    v.out.gain.exponentialRampToValueAtTime(0.025, t0 + 0.25);
    v.out.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
    v.stop(t0 + dur + 0.05);
    return wait(dur * 1000 + 80);
  }

  #gasp() {
    const ctx = this.#ctx, t0 = ctx.currentTime + 0.02;
    this.#breath(t0, 0.32, 700, 2800, 0.3, 0.9);
    const v = this.#voice(t0 + 0.05, 260, [700, 1300, 2700]);          // short "ah!" on the inhale
    v.out.gain.setValueAtTime(0.0001, t0 + 0.05);
    v.out.gain.exponentialRampToValueAtTime(0.05, t0 + 0.1);
    v.out.gain.exponentialRampToValueAtTime(0.0001, t0 + 0.3);
    v.stop(t0 + 0.35);
    return wait(420);
  }

  /** Sipping hot tea: a short airy slurp rising in pitch. */
  #slurp() {
    const ctx = this.#ctx, t0 = ctx.currentTime + 0.02;
    this.#breath(t0, 0.42, 500, 2400, 0.16, 2.2);
    this.#breath(t0 + 0.18, 0.3, 900, 3200, 0.08, 3);
    return wait(520);
  }

  #scoff() {
    const ctx = this.#ctx, t0 = ctx.currentTime + 0.02;
    const v = this.#voice(t0, 120, [280, 900, 2300]);                 // nasal "hm"
    v.out.gain.setValueAtTime(0.0001, t0);
    v.out.gain.exponentialRampToValueAtTime(0.06, t0 + 0.03);
    v.out.gain.exponentialRampToValueAtTime(0.0001, t0 + 0.16);
    v.stop(t0 + 0.2);
    this.#breath(t0 + 0.14, 0.12, 1400, 900, 0.18);                    // the "-ph" puff
    return wait(330);
  }
}

const wait = (ms) => new Promise((r) => setTimeout(r, Math.max(0, ms)));
