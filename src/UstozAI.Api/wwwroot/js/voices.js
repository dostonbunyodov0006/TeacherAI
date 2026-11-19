// Choosing a browser voice that can actually SAY the line.
// Chrome has no Uzbek voice (Edge has "Sardor/Madina Online (Natural)"). Without one, an Uzbek line used to be
// silent. Fallback: Uzbek Latin → Cyrillic, read by a Russian voice — understandable Uzbek, for free.

const cache = { voices: [] };

/** Voices load asynchronously (Chrome returns [] at first) — wait up to `ms` for them. */
export function loadVoices(ms = 1500) {
  const synth = window.speechSynthesis;
  if (!synth) return Promise.resolve([]);
  const now = synth.getVoices();
  if (now.length) return Promise.resolve((cache.voices = now));
  return new Promise((resolve) => {
    const done = () => { cache.voices = synth.getVoices(); resolve(cache.voices); };
    synth.addEventListener('voiceschanged', done, { once: true });
    setTimeout(done, ms);
  });
}

const norm = (l) => (l || '').replace('_', '-').toLowerCase();
/** Natural / online voices sound far more human than the old desktop ones. */
// Teacher is a man → male voices first (Sardor, Dmitry, Pavel, Andrew, Guy…).
const MALE = /sardor|dmitry|pavel|andrew|guy|christopher|eric|brian|roger|steffan|david|mark|george|ahmet|male/i;
const quality = (v) => (/natural/i.test(v.name) ? 3 : 0) + (/online|google/i.test(v.name) ? 2 : 0) + (MALE.test(v.name) ? 1.5 : 0) +
  (v.default ? 0.2 : 0);
function best(voices, lang) {
  const l = norm(lang), base = l.split('-')[0];
  const exact = voices.filter((v) => norm(v.lang) === l);
  const same = exact.length ? exact : voices.filter((v) => norm(v.lang).startsWith(base));
  return same.sort((a, b) => quality(b) - quality(a))[0] || null;
}

/**
 * @returns {{ voice: SpeechSynthesisVoice|null, text: string, note: string|null }}
 *   text = what the voice should read (may be transliterated); note = why a fallback was used (shown once).
 */
export function planSpeech(text, lang) {
  const voices = cache.voices.length ? cache.voices : (window.speechSynthesis?.getVoices() || []);
  if (!voices.length) return { voice: null, text, note: 'no-voices' };
  const own = best(voices, lang);
  if (own) return { voice: own, text, note: null };
  if (lang.startsWith('uz')) {
    const ru = best(voices, 'ru-RU');
    if (ru) return { voice: ru, text: uzToCyrillic(text), note: 'uz-via-ru' };
    const tr = best(voices, 'tr-TR');
    if (tr) return { voice: tr, text, note: 'uz-via-tr' };
  }
  const any = best(voices, 'en-US') || voices[0];
  return { voice: any, text, note: 'other-voice' };
}


/**
 * Uzbek Latin → Cyrillic tuned for a RUSSIAN voice (no ў/қ/ғ/ҳ — a Russian voice skips letters it doesn't know).
 * "Assalomu alaykum, o'g'lim! Bugun nima o'rganamiz?" → "Ассалому алайкум, оглим! Бугун нима органамиз?"
 */
const DI = { "o'": 'о', "g'": 'г', sh: 'ш', ch: 'ч', yo: 'ё', yu: 'ю', ya: 'я', ye: 'е' };
const ONE = { a: 'а', b: 'б', d: 'д', e: 'е', f: 'ф', g: 'г', h: 'х', i: 'и', j: 'ж', k: 'к', l: 'л', m: 'м', n: 'н', o: 'о',
  p: 'п', q: 'к', r: 'р', s: 'с', t: 'т', u: 'у', v: 'в', x: 'х', y: 'й', z: 'з', c: 'ц', w: 'в' };
function uzToCyrillic(text) {
  return text.replace(/[ʻ‘’`]/g, "'").replace(/[A-Za-z']+/g, (w) => {
    if (/^teacher$/i.test(w)) return 'Тичер';                      // the name, as it is pronounced
    let out = '';
    const lw = w.toLowerCase();
    for (let i = 0; i < w.length;) {
      const up = w[i] !== lw[i];
      const two = lw.slice(i, i + 2);
      let c;
      if (DI[two]) { c = DI[two]; i += 2; }
      else if (lw[i] === 'e' && i === 0) { c = 'э'; i++; }                              // word-initial e → э
      else if (lw[i] === "'") { c = i > 0 && i < w.length - 1 ? 'ъ' : ''; i++; }      // tutuq: ma'no → маъно
      else { c = ONE[lw[i]] ?? w[i]; i++; }
      out += up && c ? c[0].toUpperCase() + c.slice(1) : c;
    }
    return out;
  });
}
