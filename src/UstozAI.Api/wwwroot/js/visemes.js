// Text → mouth shapes for the browser-voice path (no Azure visemes). Uzbek Latin, Russian Cyrillic and English
// are close enough to "one letter ≈ one sound" for believable lip-sync when the timing comes from the real voice.
//
// Each word becomes a list of { shape, w } where w is a relative duration weight: vowels hold the mouth open longer,
// consonants are quick transitions, and m/b/p fully close the lips — that alternation is what reads as speech.

const VOWEL = { a: 'A', 'а': 'A', 'я': 'A', o: 'O', 'о': 'O', 'ё': 'O', u: 'U', 'у': 'U', 'ю': 'U', 'ў': 'O',
                e: 'E', 'е': 'E', 'э': 'E', i: 'I', 'и': 'I', 'ы': 'I', y: 'I' };
const CONS = {
  m: 'MBP', b: 'MBP', p: 'MBP', 'м': 'MBP', 'б': 'MBP', 'п': 'MBP',
  f: 'FV', v: 'FV', 'ф': 'FV', 'в': 'FV', w: 'U',
  l: 'L', n: 'L', t: 'L', d: 'L', r: 'L', 'л': 'L', 'н': 'L', 'т': 'L', 'д': 'L', 'р': 'L',
  s: 'I', z: 'I', j: 'I', 'с': 'I', 'з': 'I', 'ц': 'I', 'й': 'I',
  'ш': 'U', 'щ': 'U', 'ж': 'U', 'ч': 'U',
  k: 'E', g: 'E', q: 'E', x: 'E', h: 'E', 'к': 'E', 'г': 'E', 'х': 'E', 'қ': 'E', 'ғ': 'E', 'ҳ': 'E',
};

/** Split text into words with their character offsets (matches speechSynthesis boundary charIndex). */
export function words(text) {
  const out = [];
  const re = /[\p{L}\p{N}'’ʻ`]+/gu;
  let m;
  while ((m = re.exec(text))) out.push({ start: m.index, end: m.index + m[0].length, text: m[0] });
  return out;
}

/** One word → [{shape, w}] with digraphs (sh, ch, o', g') handled and repeated shapes merged. */
export function wordVisemes(word) {
  const s = word.toLowerCase().replace(/[o][‘’ʻ'`]/g, 'o').replace(/[g][‘’ʻ'`]/g, 'g');
  const seq = [];
  for (let i = 0; i < s.length; i++) {
    const c = s[i], n = s[i + 1];
    let shape, w, vowel = false;
    if ((c === 's' || c === 'c') && n === 'h') { shape = 'U'; w = 1.0; i++; }
    else if (VOWEL[c]) { shape = VOWEL[c]; w = 1.7; vowel = true; }
    else if (CONS[c]) { shape = CONS[c]; w = shape === 'MBP' ? 0.9 : 0.75; }
    else continue;
    const prev = seq[seq.length - 1];
    if (prev && prev.shape === shape && prev.vowel === vowel) prev.w += w * 0.6;
    else seq.push({ shape, w, vowel });
  }
  return seq;
}

/** Shape at progress p (0..1) through a word's viseme list + a syllable "energy" (vowels open the jaw). */
export function sampleWord(seq, p) {
  if (!seq.length) return { shape: 'rest', energy: 0 };
  const total = seq.reduce((a, v) => a + v.w, 0);
  let t = Math.min(0.999, Math.max(0, p)) * total;
  for (const v of seq) {
    if (t < v.w) {
      const local = t / v.w;                                   // 0..1 inside this sound
      const energy = v.vowel ? 0.55 + 0.4 * Math.sin(local * Math.PI) : v.shape === 'MBP' ? 0.05 : 0.35;
      return { shape: v.shape, energy };
    }
    t -= v.w;
  }
  return { shape: 'rest', energy: 0 };
}
