// Ordered playback of reply chunks with lip-sync.
//   speech chunk → server audio + viseme timeline (Azure) or browser voice + text-estimated visemes
//   sfx chunk    → VocalFx (real clip or synthesized laugh/sigh/gasp/scoff), mouth driven by loudness
// All audio flows through one AnalyserNode, so jaw "energy" follows what is actually heard.
import { VocalFx } from './sfx.js';
import { loadVoices, planSpeech } from './voices.js';
import { words, wordVisemes, sampleWord } from './visemes.js';

// Browser-voice prosody per emotion (used only when the server sends no audio)
const BROWSER_PROSODY = {
  angry: { rate: 1.12, pitch: 1.05 }, scolding: { rate: 1.08, pitch: 1.02 }, shocked: { rate: 1.05, pitch: 1.25 },
  skeptical: { rate: 0.92, pitch: 0.92 }, laughing: { rate: 1.08, pitch: 1.12 }, encouraging: { rate: 1, pitch: 1.06 },
  thinking: { rate: 0.9, pitch: 1 },
  angry_flame: { rate: 1.15, pitch: 1.08 }, mocking: { rate: 0.95, pitch: 0.95 }, shy_flustered: { rate: 1.1, pitch: 1.15 },
  hurt_apologetic: { rate: 0.9, pitch: 0.97 }, teaching: { rate: 1, pitch: 1 },
};
const SFX_MOUTH = { laugh: 'A', gasp: 'O', sigh: 'E', scoff: 'MBP', pause: 'rest' };

export class SpeechPlayer {
  #avatar; #queue = []; #playing = false; #stopped = false;
  #ctx = null; #analyser = null; #buf = null; #fx = null;
  #current = null;          // { audio?, raf }
  onIdle = () => {};
  /** (item) => void — item = { text, speech, segment, durationMs }; fired right before a chunk plays */
  onChunkStart = (_item) => {};
  /** Level-based speed for the browser voice (A1 slower … C2 faster). */
  levelRate = 1;

  constructor(avatar) { this.#avatar = avatar; }

  /** chunk = { text, speech: {audioBase64, audioMime, durationMs, visemes}, segment: {index,type,emotion,intensity,firstOfSegment} } */
  enqueue(chunk, lang) {
    this.#stopped = false;
    const item = { ...chunk, lang };
    // Decode NOW (in parallel with whatever is playing) so the next sentence starts with zero gap.
    if (chunk.speech?.audioBase64) {
      this.#ensureCtx();
      const bytes = Uint8Array.from(atob(chunk.speech.audioBase64), (c) => c.charCodeAt(0));
      item.decoded = this.#ctx.decodeAudioData(bytes.buffer).catch(() => null);
    }
    this.#queue.push(item);
    if (!this.#playing) this.#next();
  }

  /** Wait for a body action (e.g. the tea sip) before the next queued chunk plays. Times out after 8 s. */
  enqueueBarrier(promise) {
    this.#queue.push({ barrier: Promise.race([promise, new Promise((r) => setTimeout(r, 15000))]) });
    if (!this.#playing) this.#next();
  }

  /** Play a vocal effect right now, outside the queue (body actions: slurp, sigh). */
  fx(type, intensity = 1) {
    this.#ensureCtx();
    if (this.#ctx.state === 'suspended') this.#ctx.resume();
    return this.#fx.play(type, intensity);
  }

  /** Barge-in: user pressed Space while Ustoz was talking/laughing. */
  stop() {
    this.#gen++;                       // any playback loop started before this point must not continue
    this.#stopped = true;
    this.#queue = [];
    if (this.#current) { cancelAnimationFrame(this.#current.raf); try { this.#current.src?.stop(); } catch { /* not started */ } }
    this.#fx?.stop();
    window.speechSynthesis?.cancel();
    this.#current = null;
    this.#playing = false;
    this.#avatar.setVocal(null);
    this.#avatar.setMouth('rest');
    this.#avatar.setEnergy(0);
  }

  get busy() { return this.#playing || this.#queue.length > 0; }

  /**
   * Call from a user gesture (Space press / Send click): browsers only let an AudioContext start inside one.
   * Also primes speechSynthesis voices (they load asynchronously in Chrome).
   */
  unlock() {
    this.#ensureCtx();
    if (this.#ctx.state === 'suspended') this.#ctx.resume();
    if (!this.#unlocked) {
      this.#unlocked = true;
      const b = this.#ctx.createBuffer(1, 1, 22050), src = this.#ctx.createBufferSource();   // iOS: play 1 silent sample
      src.buffer = b; src.connect(this.#ctx.destination); src.start(0);
      window.speechSynthesis?.getVoices();
    }
  }
  #unlocked = false;

  /** Instant local vocal reaction (no server round-trip) — e.g. a scoff the moment a cut-off clip is released. */
  async react(type, intensity = 1) {
    if (this.#playing) return;
    await this.#playVocal(type, intensity);
  }

  /** Playback generation: stop() bumps it, so a stale loop (whose timer fires late) can't drain the new queue. */
  #gen = 0;

  async #next() {
    const gen = this.#gen;
    const item = this.#queue.shift();
    if (!item || this.#stopped) {
      this.#playing = false;
      this.#avatar.setVocal(null); this.#avatar.setMouth('rest'); this.#avatar.setEnergy(0);
      if (!this.#stopped) this.onIdle();
      return;
    }
    this.#playing = true;
    if (item.barrier) { await item.barrier; if (gen === this.#gen) this.#next(); return; }
    // Browser voice: every utterance has its own start-up pause → sentence by sentence it sounded choppy.
    // Join the following queued sentences of the same voice + mood into ONE continuous utterance.
    const isText = (it) => it && !it.barrier && !it.speech?.audioBase64 && !it.speech?.audioMime?.startsWith('sfx/');
    if (isText(item)) {
      while (isText(this.#queue[0]) && this.#queue[0].lang === item.lang
             && (this.#queue[0].segment?.emotion ?? '') === (item.segment?.emotion ?? '') && item.text.length < 400) {
        const nx = this.#queue.shift();
        item.text = `${item.text.trim()} ${nx.text.trim()}`;
      }
    }
    const s = item.speech || {};
    this.onChunkStart({ ...item, durationMs: s.durationMs || 0 });
    try {
      if (s.audioMime?.startsWith('sfx/')) await this.#playVocal(s.audioMime.slice(4), item.segment?.intensity || 1);
      else if (s.audioBase64) await this.#playAudio(s, item.decoded);
      else await this.#playBrowserVoice(item.text, item.lang, s, item.segment?.emotion);
    } catch (e) { console.warn('[Ustoz] playback failed', e); }
    if (gen === this.#gen) this.#next();   // a barge-in happened meanwhile → the new loop owns the queue
  }

  #ensureCtx() {
    if (this.#ctx) return;
    this.#ctx = new AudioContext();
    this.#analyser = this.#ctx.createAnalyser();
    this.#analyser.fftSize = 512;
    this.#buf = new Uint8Array(this.#analyser.fftSize);
    this.#analyser.connect(this.#ctx.destination);
    this.#fx = new VocalFx(this.#ctx, this.#analyser);
  }

  #loudness() {
    this.#analyser.getByteTimeDomainData(this.#buf);
    let sum = 0; for (const v of this.#buf) { const x = (v - 128) / 128; sum += x * x; }
    return Math.min(1, Math.sqrt(sum / this.#buf.length) * 4);
  }

  /** Laugh/sigh/gasp/scoff: face switches to the vocal expression, mouth opens with each burst. */
  async #playVocal(type, intensity) {
    this.#ensureCtx();
    if (this.#ctx.state === 'suspended') await this.#ctx.resume();
    const cur = { raf: 0 };
    this.#current = cur;
    this.#avatar.setVocal(type);
    this.#avatar.setMouth(SFX_MOUTH[type] || 'rest');
    const frame = () => { this.#avatar.setEnergy(this.#loudness()); cur.raf = requestAnimationFrame(frame); };
    cur.raf = requestAnimationFrame(frame);
    try { await this.#fx.play(type, intensity); }
    finally { cancelAnimationFrame(cur.raf); this.#avatar.setVocal(null); this.#avatar.setEnergy(0); }
  }

  /** Gapless Web Audio playback; the viseme clock is the AudioContext clock (sample-accurate). */
  async #playAudio(s, decoded) {
    this.#ensureCtx();
    if (this.#ctx.state === 'suspended') await this.#ctx.resume();
    const buffer = await decoded;
    if (!buffer || this.#stopped) return;
    await new Promise((resolve) => {
      const src = this.#ctx.createBufferSource();
      src.buffer = buffer;
      src.connect(this.#analyser);
      const cues = s.visemes || [];
      const t0 = this.#ctx.currentTime + 0.01;
      const cur = { src, raf: 0 };
      this.#current = cur;
      const frame = () => {
        const ms = (this.#ctx.currentTime - t0) * 1000, loud = this.#loudness();
        this.#avatar.setMouth(cueAt(cues, ms) ?? (loud > 0.15 ? 'E' : 'rest'));
        this.#avatar.setEnergy(loud);
        cur.raf = requestAnimationFrame(frame);
      };
      src.onended = () => { cancelAnimationFrame(cur.raf); src.disconnect(); this.#avatar.autoEmphasis = false; this.#avatar.blinkSoon(40); resolve(); };
      this.#avatar.autoEmphasis = true;
      this.#avatar.emphasis('stress');
      src.start(t0);
      cur.raf = requestAnimationFrame(frame);
    });
  }

  /** No server audio: browser voice + text-estimated viseme clock (re-anchored on word boundaries). */
  /**
   * Browser voice (no server audio). The mouth follows the REAL voice, word by word:
   *  • every speechSynthesis 'boundary' event says which word is being spoken right now → we animate THAT word's
   *    sounds (vowels open the jaw, m/b/p close the lips) and measure the voice's actual speed (ms per letter);
   *  • between words the lips settle briefly; voices that send no boundary events fall back to the measured speed.
   * Energy is per-syllable, not a constant — that's what removes the "robot opening and closing" look.
   */
  /** Called once when Teacher has to use a substitute voice (e.g. no Uzbek voice in Chrome). */
  onVoiceNote = () => {};
  #notes = new Set();

  async #playBrowserVoice(shownText, lang, s, emotion) {
    await loadVoices();
    const plan = planSpeech(shownText, lang);
    if (plan.note && !this.#notes.has(plan.note)) { this.#notes.add(plan.note); this.onVoiceNote(plan.note, plan.voice?.name); }
    const text = plan.text;                                   // what the voice reads (may be Cyrillic for uz)
    return new Promise((resolve) => {
      const ws = words(text).map((w) => ({ ...w, seq: wordVisemes(w.text) }));
      const cur = { raf: 0 };
      this.#current = cur;
      const intensity = ['angry', 'scolding', 'angry_flame'].includes(emotion) ? 1.15 : emotion === 'shy_flustered' ? 0.8 : 1;
      let t0 = 0, msPerChar = 68, wordIdx = -1, wordStart = 0, lastB = null, boundaries = 0, done = false;
      let waitForVoice = false;      // online voices: 'start' can fire before the sound is audible → wait for the 1st word

      const wordAt = (charIndex) => ws.findIndex((w) => charIndex >= w.start && charIndex < w.end + 1);
      // what follows each word: "!" → exclaim, "?" → question, "." → sentence end (blink after it)
      const after = (w) => (text.slice(w.end).match(/^\s*([!?.,…]?)/) || [])[1] || '';
      let announced = -1;
      const onWordStart = (i, now) => {
        if (i === announced || i < 0) return;
        announced = i;
        const w = ws[i], p = after(w);
        if (i === 0) this.#avatar.emphasis('stress');                       // the first word of a line
        else if (p === '!') this.#avatar.emphasis('exclaim');
        else if (p === '?') this.#avatar.emphasis('question');
        if (p === '.' || p === '!' || p === '?' || p === '…')
          this.#avatar.blinkSoon(Math.max(120, (w.end - w.start) * msPerChar));
      };
      const frame = (now) => {
        if (done) return;
        // no boundary events yet (or ever): continuous cursor at the measured / default speed
        if (boundaries === 0 && waitForVoice && now - t0 < 700) {   // not audible yet: keep the lips closed
          this.#avatar.setMouth('rest'); this.#avatar.setEnergy(0);
          cur.raf = requestAnimationFrame(frame);
          return;
        }
        if (boundaries === 0 && t0) {
          const ch = (now - t0) / msPerChar;
          const i = ws.findIndex((w) => ch < w.end + 1);
          if (i !== wordIdx && i >= 0) { wordIdx = i; wordStart = t0 + ws[i].start * msPerChar; onWordStart(i, now); }
        }
        const w = ws[wordIdx];
        let shape = 'rest', energy = 0;
        if (w) {
          const dur = Math.max(120, (w.end - w.start) * msPerChar);
          const p = (now - wordStart) / dur;
          if (p <= 1) ({ shape, energy } = sampleWord(w.seq, p));
          else shape = 'rest';                                         // short gap between words
        }
        this.#avatar.setMouth(shape);
        this.#avatar.setEnergy(Math.min(1, energy * intensity));
        cur.raf = requestAnimationFrame(frame);
      };
      const begin = () => { t0 = performance.now(); wordIdx = ws.length ? 0 : -1; wordStart = t0; onWordStart(wordIdx, t0); cur.raf = requestAnimationFrame(frame); };
      const finish = () => {
        if (done) return; done = true; cancelAnimationFrame(cur.raf);
        this.#avatar.setMouth('rest'); this.#avatar.setEnergy(0);
        resolve();
      };

      const synth = window.speechSynthesis;
      const voice = plan.voice;
      if (!synth || !voice) {                                    // no voice for this language: subtitles + silent mouth
        const total = text.length * msPerChar;
        begin(); setTimeout(finish, total + 200); return;
      }
      const u = new SpeechSynthesisUtterance(text);
      const pr = BROWSER_PROSODY[emotion] || { rate: 1, pitch: 1 };
      const rate = pr.rate * this.levelRate;
      u.voice = voice; u.lang = voice.lang; u.rate = rate; u.pitch = pr.pitch;
      msPerChar = 68 / rate;
      waitForVoice = voice.localService === false;
      u.onboundary = (ev) => {
        if (ev.name && ev.name !== 'word') return;
        const now = performance.now();
        const i = wordAt(ev.charIndex);
        if (i < 0) return;
        // learn this voice's real speed from consecutive boundaries (smoothed, clamped to human range)
        if (lastB && ev.charIndex > lastB.char && !/[.,!?;:—–…]/.test(text.slice(lastB.char, ev.charIndex))) {
          const measured = (now - lastB.time) / (ev.charIndex - lastB.char);
          msPerChar = Math.min(110, Math.max(40, msPerChar * 0.6 + measured * 0.4));
        }
        lastB = { char: ev.charIndex, time: now };
        boundaries++;
        wordIdx = i; wordStart = now;
        onWordStart(i, now);
      };
      u.onstart = begin; u.onend = finish; u.onerror = finish;
      this._utterance = u;                                     // Chrome bug: an unreferenced utterance is GC'd → no 'end'
      if (synth.paused) synth.resume();
      synth.speak(u);
      // Chrome sometimes never fires start/end (voice stalls) → don't freeze the whole queue: give up after a while.
      setTimeout(() => { if (!t0) begin(); }, 1500);
      const guard = setInterval(() => {
        if (done) return clearInterval(guard);
        if (!synth.speaking && t0 && performance.now() - t0 > 600) { clearInterval(guard); finish(); }
      }, 500);
      setTimeout(() => { if (!done) { clearInterval(guard); synth.cancel(); finish(); } }, text.length * 160 + 5000);
    });
  }
}

function cueAt(cues, ms) {
  if (!cues.length) return null;
  let lo = 0, hi = cues.length - 1, ans = -1;
  while (lo <= hi) { const mid = (lo + hi) >> 1; if (cues[mid].offsetMs <= ms) { ans = mid; lo = mid + 1; } else hi = mid - 1; }
  return ans < 0 ? 'rest' : cues[ans].shape;
}

