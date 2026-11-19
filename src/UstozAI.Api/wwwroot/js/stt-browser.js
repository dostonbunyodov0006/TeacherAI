// Free speech-to-text: the browser's own recognizer (Web Speech API — Chrome & Edge, online).
// Used when the server has no STT key. Runs alongside the recorder (which still measures duration / voiced time
// for the cut-off check); only the TEXT is sent to the server, never the audio.
//
// Limits: the language must be chosen up front (no auto-detection) — "Avto" uses the interface language; pick
// UZ / RU / EN in the top bar when you switch languages. Firefox has no Web Speech recognition.

const SR = window.SpeechRecognition || window.webkitSpeechRecognition;

export class BrowserStt {
  static get supported() { return !!SR; }

  #rec = null; #final = ''; #interim = ''; #ended = null; #onInterim;
  /** Last recognizer error ('not-allowed', 'network', 'no-speech', 'language-not-supported', 'audio-capture'…). */
  lastError = null;

  /** @param {(text: string) => void} onInterim live partial transcript (shown as subtitles while you speak) */
  constructor(onInterim = () => {}) { this.#onInterim = onInterim; }

  start(lang) {
    this.abort();
    const rec = new SR();
    rec.lang = lang;
    rec.continuous = true;
    rec.interimResults = true;
    rec.maxAlternatives = 1;
    this.#final = ''; this.#interim = ''; this.lastError = null;
    rec.onresult = (e) => {
      let interim = '';
      for (let i = e.resultIndex; i < e.results.length; i++) {
        const r = e.results[i];
        if (r.isFinal) this.#final += r[0].transcript + ' ';
        else interim += r[0].transcript;
      }
      this.#interim = interim;
      this.#onInterim((this.#final + interim).trim());
    };
    this.#ended = new Promise((resolve) => {
      rec.onend = resolve;
      rec.onerror = (e) => { if (e.error !== 'aborted') this.lastError = e.error; console.warn('[STT] error:', e.error); resolve(); };
    });
    try { rec.start(); } catch (e) { this.lastError = 'start-failed'; console.warn('[STT] start failed', e); }
    this.#rec = rec;
  }

  /** Stop listening and return the final transcript (waits briefly for the last result). */
  async stop() {
    const rec = this.#rec;
    if (!rec) return '';
    try { rec.stop(); } catch { /* not started */ }
    // the recognizer usually ends within ~300 ms of stop(); don't make the student wait longer than 700 ms
    await Promise.race([this.#ended, new Promise((r) => setTimeout(r, 700))]);
    this.#rec = null;
    return (this.#final + (this.#final ? '' : this.#interim)).trim();
  }

  abort() {
    if (!this.#rec) return;
    try { this.#rec.abort(); } catch { /* ignore */ }
    this.#rec = null;
  }
}
