import { paintHair } from './hair.js';
// "Ustoz" bust avatar — procedural SVG on a blendshape-like rig.
//
// Layers, evaluated every frame:
//   1. base expression   ← pipeline state (idle/listening/thinking) or the current segment's emotion while speaking
//   2. anger modulation  ← persistent angerLevel 0..10 (furrow, narrowed eyes, flush, frown)
//   3. procedural life   ← blinks (incl. slow skeptical blinks), micro-saccades, breathing, speech bob
//   4. micro-expressions ← timed facialCues from the LLM (eye_roll, smirk, head_shake …) with attack/release
//   5. mouth             ← visemes × audio energy; during laughter purely audio-driven
// Same API maps 1:1 onto ARKit blendshapes if you swap this for a Three.js GLB later.

const STATES = ['idle', 'listening', 'thinking', 'speaking'];
const EMOTIONS = ['neutral', 'laughing', 'angry', 'scolding', 'shocked', 'thinking', 'skeptical', 'encouraging', 'explaining',
  'apologetic', 'curious',
  // contract v2
  'mocking', 'shy_flustered', 'angry_flame', 'hurt_apologetic', 'teaching', 'annoyed_cutoff'];

const MOUTH = {
  rest: { open: 0.04, width: 1.0, round: 0 },
  A:    { open: 0.95, width: 1.05, round: 0 },
  E:    { open: 0.5, width: 1.15, round: 0 },
  I:    { open: 0.3, width: 1.22, round: 0 },
  O:    { open: 0.75, width: 0.72, round: 0.8 },
  U:    { open: 0.35, width: 0.58, round: 1 },
  MBP:  { open: 0.0, width: 0.95, round: 0 },
  FV:   { open: 0.12, width: 1.0, round: 0, teeth: 1 },
  L:    { open: 0.42, width: 1.0, round: 0 },
};

// Rig channels and their neutral values
const NEUTRAL = {
  browL: 0, browR: 0, furrow: 0, lidL: 1, lidR: 1, squint: 0, lookX: 0, lookY: 0,
  smileL: 0.15, smileR: 0.15, tilt: 0, nod: 0, shake: 0, lean: 0, flush: 0, jaw: 0,
  fwd: 0,          // lean toward the viewer (curious_inspect): scales the head up slightly
  blush: 0,        // pink, bashful (≠ flush, which is red anger)
  forehead: 0,     // horizontal forehead creases (peshona tirishishi); raised brows add to it automatically
  hand: 0,         // 0..1 hand cupped to the ear (listening to a quiet voice)
  turn: 0,         // -1..1 whole upper body turned left/right ("o'ngga qarang")
  pitch: 0,        // -1 up .. 1 down
};
const X = (o) => ({ ...NEUTRAL, ...o });
const EXPR = {
  idle:        X({}),
  // active listening: leans in, steady eye contact, attentive tilt, faint open smile
  // active listening: leans in, ear turned toward the student, eyes locked on, brows gathered in concentration
  listening:   X({ browL: 0.32, browR: 0.12, furrow: 0.22, lidL: 1.0, lidR: 1.0, squint: 0.12, smileL: 0.12, smileR: 0.18,
                   tilt: 8, fwd: 1.0, lookX: -0.12 }),
  thinking:    X({ browL: 0.35, browR: -0.05, furrow: 0.2, lidL: 0.9, lidR: 0.9, lookX: -0.6, lookY: -0.7, smileL: 0, smileR: 0, tilt: -4 }),
  neutral:     X({ smileL: 0.1, smileR: 0.1 }),
  explaining:  X({ browL: 0.25, furrow: 0.1, smileL: 0.2, smileR: 0.2, tilt: -2 }),
  encouraging: X({ browL: 0.4, browR: 0.4, smileL: 0.85, smileR: 0.85, squint: 0.25, tilt: 3 }),
  laughing:    X({ browL: 0.35, browR: 0.35, smileL: 1, smileR: 1, squint: 0.75, lidL: 0.55, lidR: 0.55, lean: -4, tilt: 2 }),
  angry:       X({ browL: -0.35, browR: -0.35, furrow: 1, lidL: 0.75, lidR: 0.75, squint: 0.35, smileL: -0.5, smileR: -0.5, flush: 0.55 }),
  scolding:    X({ browL: -0.2, browR: -0.2, furrow: 0.8, lidL: 0.92, lidR: 0.92, smileL: -0.35, smileR: -0.35, flush: 0.35, tilt: -2 }),
  shocked:     X({ browL: 1, browR: 1, lidL: 1.35, lidR: 1.35, smileL: -0.1, smileR: -0.1, lean: -3, jaw: 0.35 }),
  skeptical:   X({ browL: -0.15, browR: 0.6, furrow: 0.25, lidL: 0.8, lidR: 0.8, squint: 0.3, smileL: -0.1, smileR: 0.45, tilt: -3 }),
  // negative furrow = inner brow ends RAISED → the soft, worried "I'm sorry" look
  apologetic:  X({ browL: 0.1, browR: 0.1, furrow: -0.8, lidL: 0.86, lidR: 0.86, lookY: 0.3, smileL: 0.12, smileR: 0.12, tilt: 6 }),
  curious:     X({ browL: 0.5, browR: 0.2, lidL: 1.12, lidR: 1.12, lookY: 0.35, fwd: 1, tilt: 4, smileL: 0.1, smileR: 0.2 }),
  // --- contract v2 emotions ---
  teaching:        X({ browL: 0.3, browR: 0.2, furrow: 0.05, smileL: 0.25, smileR: 0.25, tilt: -2 }),
  mocking:         X({ browL: -0.2, browR: 0.75, furrow: 0.2, lidL: 0.78, lidR: 0.85, squint: 0.35, smileL: -0.05, smileR: 0.8, tilt: -4 }),
  // bashful: eyes slide down and away, shy smile, head ducks, cheeks go pink
  shy_flustered:   X({ browL: 0.25, browR: 0.25, furrow: -0.35, lidL: 0.78, lidR: 0.78, lookX: 0.7, lookY: 0.6, squint: 0.2,
                       smileL: 0.55, smileR: 0.7, tilt: 7, nod: 3, blush: 1 }),
  angry_flame:     X({ browL: -0.55, browR: -0.55, furrow: 1.15, lidL: 0.66, lidR: 0.66, squint: 0.45, smileL: -0.7, smileR: -0.7,
                       flush: 0.85, jaw: 0.08, fwd: 0.5 }),
  // "chala gap": frown, creased forehead, narrowed eyes, one corner pulled down, head tipped away
  annoyed_cutoff:  X({ browL: -0.35, browR: -0.1, furrow: 0.95, forehead: 0.55, lidL: 0.7, lidR: 0.76, squint: 0.55,
                       smileL: -0.45, smileR: -0.2, tilt: -5, lookX: 0.2, flush: 0.2 }),
  hurt_apologetic: X({ browL: 0.1, browR: 0.1, furrow: -0.85, lidL: 0.84, lidR: 0.84, lookY: 0.35, smileL: 0.1, smileR: 0.1, tilt: 7 }),
};

// Micro-expressions: duration (ms) and per-frame deltas as a function of p (0..1) and envelope e (0..1).
const TAU = Math.PI * 2;
const CUES = {
  eye_roll:        { dur: 1000, f: (p, e) => ({ lookY: -1.2 * e, lookX: Math.cos(p * Math.PI) * 0.7 * e, lidL: -0.15 * e, lidR: -0.15 * e, tilt: 2 * e }) },
  squint:          { dur: 900,  f: (p, e) => ({ squint: 0.8 * e, lidL: -0.3 * e, lidR: -0.3 * e, furrow: 0.2 * e }) },
  wide_eyes_shock: { dur: 1000, f: (p, e) => ({ lidL: 0.45 * e, lidR: 0.45 * e, browL: 0.9 * e, browR: 0.9 * e, lean: -3 * e, jaw: 0.3 * e }) },
  smirk:           { dur: 1300, f: (p, e) => ({ smileR: 0.75 * e, smileL: -0.15 * e, browR: 0.25 * e, squint: 0.15 * e }) },
  brow_furrow:     { dur: 1300, f: (p, e) => ({ furrow: 0.85 * e, browL: -0.3 * e, browR: -0.3 * e, lidL: -0.12 * e, lidR: -0.12 * e }) },
  slow_blink:      { dur: 750,  f: (p) => { const c = Math.sin(p * Math.PI); return { lidL: -1.1 * c, lidR: -1.1 * c }; } },
  head_shake:      { dur: 1000, f: (p, e) => ({ shake: Math.sin(p * TAU * 2.5) * 7 * e }) },
  nod:             { dur: 650,  f: (p) => ({ nod: Math.sin(p * TAU) * 5 }) },
  brow_raise_one:  { dur: 1000, f: (p, e) => ({ browR: 0.8 * e, browL: 0.1 * e }) },
  brow_raise:      { dur: 1000, f: (p, e) => ({ browL: 0.75 * e, browR: 0.85 * e, lidL: 0.15 * e, lidR: 0.15 * e }) },
  // --- body-language cues (Phase 5) ---
  laugh_hard:      { dur: 1600, f: (p, e) => ({ lean: -7 * e, tilt: 4 * e, jaw: 0.7 * e * (0.6 + 0.4 * Math.abs(Math.sin(p * TAU * 4))),
                                                squint: 0.85 * e, lidL: -0.5 * e, lidR: -0.5 * e, smileL: 1 * e, smileR: 1 * e,
                                                nod: Math.sin(p * TAU * 4) * 3 * e, browL: 0.3 * e, browR: 0.3 * e }) },
  apologetic_soft: { dur: 1800, f: (p, e) => ({ furrow: -0.6 * e, tilt: 5 * e, lookY: 0.35 * e, lidL: -0.12 * e, lidR: -0.12 * e,
                                                nod: Math.sin(p * Math.PI) * 2 }) },
  curious_inspect: { dur: 1600, f: (p, e) => ({ fwd: 1.2 * e, lookY: 0.5 * e, lookX: Math.sin(p * TAU) * 0.4 * e,
                                                lidL: 0.15 * e, lidR: 0.15 * e, browL: 0.4 * e, tilt: 3 * e }) },
  nod_agree:       { dur: 800,  f: (p) => ({ nod: Math.sin(p * TAU * 1.5) * 5 * (1 - p * 0.5) }) },
  shake_head_no:   { dur: 1000, f: (p, e) => ({ shake: Math.sin(p * TAU * 2.5) * 8 * e, furrow: 0.2 * e }) },
  thinking_look_up:{ dur: 1500, f: (p, e) => ({ lookY: -1.1 * e, lookX: -0.6 * e, browL: 0.35 * e, tilt: -3 * e, smileL: -0.1 * e }) },
  surprised_brow_raise: { dur: 900, f: (p, e) => ({ browL: 1 * e, browR: 1 * e, lidL: 0.35 * e, lidR: 0.35 * e, jaw: 0.15 * e }) },
  // --- contract v2 cues ---
  bashful_look:    { dur: 1500, f: (p, e) => ({ lookX: 0.9 * e, lookY: 0.7 * e, blush: 0.7 * e, smileL: 0.3 * e, smileR: 0.4 * e,
                                                tilt: 5 * e, lidL: -0.2 * e, lidR: -0.2 * e }) },
  // "hozir hushimdan ketaman": eyes roll up, lids droop, head sways back, then a shake to come round
  faint_dramatic:  { dur: 2200, f: (p, e) => ({ lookY: -1.3 * e, lidL: -0.55 * e, lidR: -0.55 * e, lean: -6 * e, jaw: 0.25 * e,
                                                tilt: Math.sin(p * TAU * 1.5) * 9 * e, blush: 0.4 * e,
                                                shake: p > 0.75 ? Math.sin((p - 0.75) * TAU * 6) * 5 * (1 - p) * 4 : 0 }) },
};
// v2 names that reuse existing motions
// --- v2.1 upper-face detail ---
CUES.forehead_crease = { dur: 1400, f: (p, e) => ({ forehead: 0.95 * e, furrow: 0.5 * e, browL: -0.1 * e, browR: -0.1 * e }) };
CUES.squint_annoyed = { dur: 1300, f: (p, e) => ({ squint: 0.75 * e, lidL: -0.3 * e, lidR: -0.25 * e, furrow: 0.5 * e, smileL: -0.3 * e }) };
// "ensasi qotishi": eyes roll up in an arc, a heavy slow blink at the top, head tips back, tiny exasperated shake
CUES.exasperated_roll = { dur: 1700, f: (p, e) => {
  const blink = p > 0.45 && p < 0.7 ? Math.sin(((p - 0.45) / 0.25) * Math.PI) : 0;
  return { lookY: -1.3 * Math.sin(Math.min(1, p * 1.6) * Math.PI * 0.5) * e, lookX: Math.cos(p * Math.PI) * 0.6 * e,
           lidL: -0.25 * e - 1.1 * blink, lidR: -0.25 * e - 1.1 * blink, lean: -4 * e, tilt: -6 * e,
           shake: p > 0.7 ? Math.sin((p - 0.7) * TAU * 3) * 3 * (1 - p) * 3 : 0, forehead: 0.4 * e };
} };
CUES.mouth_smile = { dur: 1400, f: (p, e) => ({ smileL: 0.9 * e, smileR: 0.9 * e, squint: 0.2 * e }) };
CUES.nod_small = { dur: 520, f: (p) => ({ nod: Math.sin(p * Math.PI) * 3.2, browL: 0.1 * Math.sin(p * Math.PI) }) };   // "mm-hm"
CUES.laugh_motion = CUES.laugh_hard;
CUES.ensa_qotish = CUES.exasperated_roll;
CUES.wide_eyes = CUES.wide_eyes_shock;
CUES.head_nod = CUES.nod_agree;
CUES.head_shake = CUES.shake_head_no;
const envelope = (p) => (p < 0.18 ? Math.max(0, p / 0.18) : p > 0.7 ? Math.max(0, (1 - p) / 0.3) : 1);
const ease = (x) => x * x * (3 - 2 * x);

const SVG = `
<svg viewBox="0 0 400 460" xmlns="http://www.w3.org/2000/svg" role="img" aria-label="Teacher">
  <defs>
    <radialGradient id="skin" gradientUnits="userSpaceOnUse" cx="186" cy="176" r="196">
      <stop offset="0" stop-color="#f3caa8"/><stop offset=".55" stop-color="#e2aa84"/><stop offset="1" stop-color="#c58862"/>
    </radialGradient>
    <linearGradient id="shadeR" x1="0" y1="0" x2="1" y2="0">
      <stop offset=".52" stop-color="#6e341c" stop-opacity="0"/><stop offset="1" stop-color="#6e341c" stop-opacity=".34"/>
    </linearGradient>
    <radialGradient id="jawShade" cx="50%" cy="100%" r="60%">
      <stop offset="0" stop-color="#7a3e24" stop-opacity=".32"/><stop offset="1" stop-color="#7a3e24" stop-opacity="0"/>
    </radialGradient>
    <radialGradient id="softHi" cx="50%" cy="50%" r="50%">
      <stop offset="0" stop-color="#fff4ea" stop-opacity=".55"/><stop offset="1" stop-color="#fff4ea" stop-opacity="0"/>
    </radialGradient>
    <radialGradient id="softSh" cx="50%" cy="50%" r="50%">
      <stop offset="0" stop-color="#8a4a30" stop-opacity=".35"/><stop offset="1" stop-color="#8a4a30" stop-opacity="0"/>
    </radialGradient>
    <radialGradient id="stubble" cx="50%" cy="40%" r="60%">
      <stop offset="0" stop-color="#4a3428" stop-opacity=".09"/><stop offset=".6" stop-color="#4a3428" stop-opacity=".05"/><stop offset="1" stop-color="#4a3428" stop-opacity="0"/>
    </radialGradient>
    <linearGradient id="hairG" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#3a2a1e"/><stop offset=".6" stop-color="#22170f"/><stop offset="1" stop-color="#110b07"/>
    </linearGradient>
    <radialGradient id="irisG" cx="50%" cy="50%" r="50%">
      <stop offset="0" stop-color="#9a6a3c"/><stop offset=".45" stop-color="#6e4526"/><stop offset=".85" stop-color="#3b2312"/><stop offset="1" stop-color="#1c110a"/>
    </radialGradient>
    <radialGradient id="scleraG" cx="50%" cy="45%" r="60%">
      <stop offset=".55" stop-color="#fbf8f5"/><stop offset="1" stop-color="#e3d3c8"/>
    </radialGradient>
    <linearGradient id="lipU" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#a95a4c"/><stop offset="1" stop-color="#8c4438"/></linearGradient>
    <linearGradient id="lipL" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#b8685a"/><stop offset=".6" stop-color="#c47767"/><stop offset="1" stop-color="#a65d50"/></linearGradient>
    <radialGradient id="mouthG" cx="50%" cy="40%" r="60%"><stop offset="0" stop-color="#5a1f1e"/><stop offset="1" stop-color="#2e0f10"/></radialGradient>
    <linearGradient id="suitG" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#2f4777"/><stop offset="1" stop-color="#16223f"/></linearGradient>
    <linearGradient id="lapelG" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#3a5590"/><stop offset="1" stop-color="#1c2b4f"/></linearGradient>
    <linearGradient id="shirtG" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fbfcfe"/><stop offset="1" stop-color="#d9dee8"/></linearGradient>
    <pattern id="tieP" width="10" height="10" patternUnits="userSpaceOnUse" patternTransform="rotate(40)">
      <rect width="10" height="10" fill="#7d1f3b"/><rect width="10" height="2.4" fill="#c0a050"/>
    </pattern>
    <linearGradient id="neckSh" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#6e341c" stop-opacity=".45"/><stop offset=".6" stop-color="#6e341c" stop-opacity="0"/>
    </linearGradient>
    <radialGradient id="blush"><stop offset="0" stop-color="#e0484d"/><stop offset="1" stop-color="#e0484d" stop-opacity="0"/></radialGradient>
    <clipPath id="faceClip"><path d="M200 96 C252 96 280 132 281 196 C282 236 274 270 258 296 C244 318 222 334 200 334 C178 334 156 318 142 296 C126 270 118 236 119 196 C120 132 148 96 200 96 Z"/></clipPath>
    <clipPath id="mouth-clip"><path id="mouth-inner-clip"/></clipPath>
    <linearGradient id="jawFade" gradientUnits="userSpaceOnUse" x1="0" y1="268" x2="0" y2="300">
      <stop offset="0" stop-color="#000"/><stop offset="1" stop-color="#fff"/>
    </linearGradient>
    <mask id="jawMask"><rect x="100" y="240" width="200" height="140" fill="url(#jawFade)"/></mask>
    <clipPath id="jawClip"><path d="M138 258 C144 300 168 334 200 334 C232 334 256 300 262 258 C252 282 230 294 200 294 C170 294 148 282 138 258 Z"/></clipPath>
    <clipPath id="eye-clip-l"><ellipse id="eye-clip-shape-l" cx="166" cy="206" rx="13.5" ry="8.6"/></clipPath>
    <clipPath id="eye-clip-r"><ellipse id="eye-clip-shape-r" cx="234" cy="206" rx="13.5" ry="8.6"/></clipPath>
  </defs>

  <g id="body">
    <!-- neck -->
    <path d="M168 292 C169 318 165 340 156 352 L244 352 C235 340 231 318 232 292 Z" fill="url(#skin)"/>
    <path id="throat" d="M168 292 C169 318 165 340 156 352 L244 352 C235 340 231 318 232 292 Z" fill="url(#neckSh)"/>
    <path id="adams" d="M196 318 Q200 314 204 318" stroke="#b07454" stroke-width="1.4" fill="none" opacity=".35"/>
    <!-- shirt + collar -->
    <path d="M150 344 L200 432 L250 344 Z" fill="url(#shirtG)"/>
    <path d="M162 334 L200 372 L184 396 L148 350 Z" fill="#f4f6fa" stroke="#c9cfd9" stroke-width="1"/>
    <path d="M238 334 L200 372 L216 396 L252 350 Z" fill="#eef1f6" stroke="#c9cfd9" stroke-width="1"/>
    <!-- tie -->
    <g id="tie">
      <path d="M189 370 L211 370 L207 388 L193 388 Z" fill="#6a1832"/>
      <path d="M193 388 L207 388 L216 462 L184 462 Z" fill="url(#tieP)"/>
      <path d="M193 388 L207 388 L203 396 L197 396 Z" fill="#000" opacity=".18"/>
    </g>
    <!-- jacket -->
    <path d="M24 462 C30 404 78 364 148 346 L170 338 L200 440 L230 338 L252 346 C322 364 370 404 376 462 Z" fill="url(#suitG)"/>
    <path d="M148 346 L170 338 L200 440 L176 392 L160 372 L168 356 Z" fill="url(#lapelG)"/>
    <path d="M252 346 L230 338 L200 440 L224 392 L240 372 L232 356 Z" fill="url(#lapelG)"/>
    <path d="M160 372 L168 356 L156 352" fill="none" stroke="#11161e" stroke-width="1.4" opacity=".6"/>
    <path d="M240 372 L232 356 L244 352" fill="none" stroke="#11161e" stroke-width="1.4" opacity=".6"/>
    <path d="M70 400 C90 392 112 388 130 388" stroke="#000" stroke-opacity=".18" stroke-width="2" fill="none"/>
    <path d="M330 400 C310 392 288 388 270 388" stroke="#000" stroke-opacity=".18" stroke-width="2" fill="none"/>
  </g>

  <g id="head">
    <!-- ears -->
    <g id="ear-l">
      <path d="M124 196 C108 190 104 220 110 240 C114 254 126 258 132 248 Z" fill="#d79d77"/>
      <path d="M121 206 C114 210 114 228 120 238" stroke="#b07454" stroke-width="2" fill="none" stroke-linecap="round"/>
      <path d="M116 214 C113 222 115 230 119 234" stroke="#c98a66" stroke-width="1.2" fill="none" opacity=".7"/>
      <ellipse cx="117" cy="248" rx="5" ry="6" fill="#e0a07e" opacity=".8"/>
    </g>
    <g id="ear-r">
      <path d="M276 196 C292 190 296 220 290 240 C286 254 274 258 268 248 Z" fill="#cf946e"/>
      <path d="M279 206 C286 210 286 228 280 238" stroke="#a96d4e" stroke-width="2" fill="none" stroke-linecap="round"/>
      <path d="M284 214 C287 222 285 230 281 234" stroke="#bd7f5d" stroke-width="1.2" fill="none" opacity=".7"/>
      <ellipse cx="283" cy="248" rx="5" ry="6" fill="#d49676" opacity=".8"/>
    </g>
    <!-- face -->
    <path d="M200 96 C252 96 280 132 281 196 C282 236 274 270 258 296 C244 318 222 334 200 334 C178 334 156 318 142 296 C126 270 118 236 119 196 C120 132 148 96 200 96 Z" fill="url(#skin)"/>
    <g clip-path="url(#faceClip)">
      <rect x="100" y="90" width="200" height="250" fill="url(#shadeR)"/>
      <ellipse cx="200" cy="336" rx="96" ry="44" fill="url(#jawShade)"/>
      <ellipse cx="182" cy="150" rx="46" ry="26" fill="url(#softHi)"/>
      <ellipse cx="150" cy="246" rx="22" ry="18" fill="url(#softHi)" opacity=".7"/>
      <ellipse cx="258" cy="246" rx="20" ry="26" fill="url(#softSh)" opacity=".6"/>
      <ellipse cx="166" cy="224" rx="17" ry="7" fill="url(#softSh)" opacity=".55"/>
      <ellipse cx="234" cy="224" rx="17" ry="7" fill="url(#softSh)" opacity=".6"/>
      <ellipse cx="200" cy="271" rx="30" ry="9" fill="url(#stubble)" opacity=".9"/>
      <ellipse cx="200" cy="314" rx="16" ry="8" fill="url(#softHi)" opacity=".5"/>
    </g>
    <!-- jaw: chin + jawline drop when the mouth opens -->
    <g id="jaw" mask="url(#jawMask)">
      <path d="M138 258 C144 300 168 334 200 334 C232 334 256 300 262 258 C252 282 230 294 200 294 C170 294 148 282 138 258 Z" fill="url(#skin)"/>
      <rect x="100" y="90" width="200" height="250" fill="url(#shadeR)" clip-path="url(#jawClip)"/>
      <path d="M138 258 C144 300 168 334 200 334 C232 334 256 300 262 258 C252 282 230 294 200 294 C170 294 148 282 138 258 Z" fill="url(#jawShade)" opacity=".8"/>
      <ellipse cx="200" cy="304" rx="64" ry="34" fill="url(#stubble)"/>
      <ellipse cx="200" cy="316" rx="15" ry="8" fill="url(#softHi)" opacity=".55"/>
      <path d="M186 324 Q200 330 214 324" stroke="#a46a48" stroke-width="1.2" fill="none" opacity=".35"/>
    </g>
    <!-- cheeks: apples lift with a smile, stretch down with a big open mouth -->
    <g id="cheeks">
      <ellipse cx="152" cy="246" rx="21" ry="15" fill="url(#softHi)" opacity=".75"/>
      <ellipse cx="250" cy="246" rx="19" ry="14" fill="url(#softHi)" opacity=".45"/>
      <ellipse cx="258" cy="252" rx="18" ry="22" fill="url(#softSh)" opacity=".45"/>
      <path d="M140 256 C146 268 158 274 170 274" stroke="#a96f4c" stroke-width="1.3" fill="none" opacity=".18"/>
      <path d="M260 256 C254 268 242 274 230 274" stroke="#9c6447" stroke-width="1.3" fill="none" opacity=".2"/>
    </g>
    <g id="flush" opacity="0">
      <ellipse cx="152" cy="252" rx="26" ry="16" fill="url(#blush)"/>
      <ellipse cx="248" cy="252" rx="26" ry="16" fill="url(#blush)"/>
      <ellipse cx="200" cy="150" rx="60" ry="22" fill="url(#blush)" opacity=".5"/>
    </g>
    <g id="blush" opacity="0">
      <ellipse cx="150" cy="250" rx="24" ry="13" fill="#ff8fb1" opacity=".55"/>
      <ellipse cx="250" cy="250" rx="24" ry="13" fill="#ff8fb1" opacity=".55"/>
      <path d="M136 246 l6 -6 M146 248 l6 -6 M156 250 l6 -6 M238 246 l6 -6 M248 248 l6 -6 M258 250 l6 -6" stroke="#e0567f" stroke-width="2" stroke-linecap="round"/>
      <ellipse cx="200" cy="248" rx="9" ry="6" fill="#ff8fb1" opacity=".5"/>
    </g>
    <!-- hair -->
    <g id="hair">
      <!-- short tapered sides (clipper fade) -->
      <path d="M121 196 C116 176 116 158 122 142 L132 148 C128 164 127 180 128 198 Z" fill="#1d1510" opacity=".32"/>
      <path d="M279 196 C284 176 284 158 278 142 L268 148 C272 164 273 180 272 198 Z" fill="#1d1510" opacity=".32"/>
      <g stroke="#1d1510" stroke-width=".45" opacity=".3"><path d="M123 150 v40 M126 148 v44 M129 150 v42 M277 150 v40 M274 148 v44 M271 150 v42"/></g>
      <!-- the hair itself: a painted bitmap (js/hair.js) — thousands of fine strands, sheen, baby hairs, forehead shadow -->
      <path d="M124 176 C116 132 128 94 158 76 C184 60 228 56 258 70 C282 80 293 104 290 138 C288 154 284 166 279 176 C279 164 275 152 266 144 C258 138 250 141 240 140 C228 139 218 134 206 134 C194 134 184 139 172 139 C160 139 150 138 142 145 C134 152 128 164 124 176 Z" fill="#20160f"/>
      <image id="hair-img" x="106" y="46" width="200" height="150" preserveAspectRatio="none"/>
      <g id="hair-front" fill="none" stroke-linecap="round"><path d="M249.8 122.7 C256.8 118.7 262.8 130.7 265.6 144.3" stroke="#46321f" stroke-width="0.58" opacity="0.71"/><path d="M267.2 124.9 C274.2 120.9 280.2 132.9 279.7 141.3" stroke="#24180f" stroke-width="0.39" opacity="0.69"/><path d="M219.7 118.1 C226.7 114.1 232.7 126.1 234.2 134.2" stroke="#24180f" stroke-width="0.49" opacity="0.60"/><path d="M269.4 123.3 C276.4 119.3 282.4 131.3 285.0 139.7" stroke="#46321f" stroke-width="0.57" opacity="0.59"/><path d="M200.1 117.4 C207.1 113.4 213.1 125.4 212.4 134.7" stroke="#24180f" stroke-width="0.66" opacity="0.64"/><path d="M276.9 124.9 C283.9 120.9 289.9 132.9 292.0 142.2" stroke="#46321f" stroke-width="0.42" opacity="0.84"/><path d="M230.7 116.8 C237.7 112.8 243.7 124.8 244.1 138.4" stroke="#33241a" stroke-width="0.37" opacity="0.64"/><path d="M248.2 118.8 C255.2 114.8 261.2 126.8 263.3 136.8" stroke="#33241a" stroke-width="0.47" opacity="0.64"/><path d="M255.7 120.6 C262.7 116.6 268.7 128.6 269.5 137.7" stroke="#33241a" stroke-width="0.69" opacity="0.56"/><path d="M260.0 124.4 C267.0 120.4 273.0 132.4 271.1 145.1" stroke="#33241a" stroke-width="0.48" opacity="0.80"/><path d="M236.2 120.9 C243.2 116.9 249.2 128.9 251.0 142.6" stroke="#24180f" stroke-width="0.61" opacity="0.83"/><path d="M275.4 123.8 C282.4 119.8 288.4 131.8 288.5 142.9" stroke="#33241a" stroke-width="0.39" opacity="0.77"/><path d="M263.8 125.0 C270.8 121.0 276.8 133.0 275.0 146.6" stroke="#24180f" stroke-width="0.42" opacity="0.70"/><path d="M229.0 117.2 C236.0 113.2 242.0 125.2 241.7 137.4" stroke="#24180f" stroke-width="0.46" opacity="0.65"/><path d="M214.2 115.1 C221.2 111.1 227.2 123.1 226.1 135.2" stroke="#33241a" stroke-width="0.41" opacity="0.56"/><path d="M278.9 125.1 C285.9 121.1 291.9 133.1 292.4 142.6" stroke="#46321f" stroke-width="0.47" opacity="0.63"/><path d="M252.1 120.0 C259.1 116.0 265.1 128.0 269.0 139.8" stroke="#33241a" stroke-width="0.47" opacity="0.61"/><path d="M278.5 125.2 C285.5 121.2 291.5 133.2 290.3 146.6" stroke="#33241a" stroke-width="0.39" opacity="0.68"/><path d="M211.9 118.7 C218.9 114.7 224.9 126.7 224.7 137.4" stroke="#46321f" stroke-width="0.65" opacity="0.84"/><path d="M236.3 119.8 C243.3 115.8 249.3 127.8 251.7 138.7" stroke="#33241a" stroke-width="0.51" opacity="0.82"/><path d="M209.0 118.2 C216.0 114.2 222.0 126.2 223.2 135.3" stroke="#33241a" stroke-width="0.65" opacity="0.60"/><path d="M239.3 120.3 C246.3 116.3 252.3 128.3 253.6 139.3" stroke="#24180f" stroke-width="0.63" opacity="0.78"/></g>
      <!-- neat sideburns -->
      <path d="M125 194 C124 204 125 212 127 218 C128 210 129 202 130 194 Z" fill="#2a1d14" opacity=".5"/>
      <path d="M275 194 C276 204 275 212 273 218 C272 210 271 202 270 194 Z" fill="#2a1d14" opacity=".5"/>
    </g>
    <!-- forehead creases / frown lines -->
    <g id="face"><!-- brows, eyes, glasses, nose, mouth: shift inside the head for a 3/4 turn / bow -->
    <g id="forehead" opacity="0" fill="none" stroke="#a96f4c" stroke-width="1.8" stroke-linecap="round">
      <path d="M170 148 Q185 143 200 147 Q215 151 230 146"/>
      <path d="M164 158 Q182 153 200 157 Q218 161 236 156"/>
      <path d="M172 168 Q186 164 200 167 Q214 170 228 166"/>
    </g>
    <path id="furrow-lines" d="M194 184 L196 196 M206 184 L204 196" stroke="#a46a48" stroke-width="2" stroke-linecap="round" opacity="0"/>
    <!-- brows: solid stroke + hair texture following the same path -->
    <path id="brow-l" stroke="#2a1b12" stroke-width="7.5" fill="none" stroke-linecap="round" opacity=".92"/>
    <path id="brow-l-tex" stroke="#4d3527" stroke-width="3.2" fill="none" stroke-dasharray="1.4 2.1" stroke-linecap="round"/>
    <path id="brow-r" stroke="#2a1b12" stroke-width="7.5" fill="none" stroke-linecap="round" opacity=".92"/>
    <path id="brow-r-tex" stroke="#4d3527" stroke-width="3.2" fill="none" stroke-dasharray="1.4 2.1" stroke-linecap="round"/>
    <!-- eyelid creases -->
    <path id="crease-l" d="M151 197 Q166 188 181 197" stroke="#a8714f" stroke-width="1.4" fill="none" opacity=".75"/>
    <path id="crease-r" d="M219 197 Q234 188 249 197" stroke="#a8714f" stroke-width="1.4" fill="none" opacity=".75"/>
    <!-- eyes -->
    <g clip-path="url(#eye-clip-l)">
      <ellipse id="eye-white-l" cx="166" cy="206" rx="13.5" ry="8.6" fill="url(#scleraG)"/>
      <g class="pupil">
        <circle cx="166" cy="206" r="7" fill="url(#irisG)"/>
        <circle class="pupil-core" cx="166" cy="206" r="3.1" fill="#0d0805"/>
        <circle cx="163.6" cy="203.6" r="1.7" fill="#fff" opacity=".95"/>
        <circle cx="168.8" cy="208.2" r=".8" fill="#fff" opacity=".55"/>
      </g>
      <rect id="lid-top-l" x="150" y="186" width="34" height="0" fill="#d99e78"/>
      <rect id="lid-low-l" x="150" y="216" width="34" height="20" fill="#d9a07c"/>
    </g>
    <g clip-path="url(#eye-clip-r)">
      <ellipse id="eye-white-r" cx="234" cy="206" rx="13.5" ry="8.6" fill="url(#scleraG)"/>
      <g class="pupil">
        <circle cx="234" cy="206" r="7" fill="url(#irisG)"/>
        <circle class="pupil-core" cx="234" cy="206" r="3.1" fill="#0d0805"/>
        <circle cx="231.6" cy="203.6" r="1.7" fill="#fff" opacity=".95"/>
        <circle cx="236.8" cy="208.2" r=".8" fill="#fff" opacity=".55"/>
      </g>
      <rect id="lid-top-r" x="216" y="186" width="34" height="0" fill="#d29672"/>
      <rect id="lid-low-r" x="216" y="216" width="34" height="20" fill="#d29a76"/>
    </g>
    <path id="lash-l" stroke="#170e09" stroke-width="2.6" fill="none" stroke-linecap="round"/>
    <path id="lash-r" stroke="#170e09" stroke-width="2.6" fill="none" stroke-linecap="round"/>
    <path id="lashlow-l" d="M155 215 Q166 220 177 215" stroke="#3a2418" stroke-width="1.3" fill="none" opacity=".55" stroke-dasharray="1.2 1.6"/>
    <path id="lashlow-r" d="M223 215 Q234 220 245 215" stroke="#3a2418" stroke-width="1.3" fill="none" opacity=".55" stroke-dasharray="1.2 1.6"/>
    <!-- glasses -->
    <g fill="none" stroke="#1d232d" stroke-width="2.4">
      <rect x="143" y="190" width="47" height="33" rx="11" fill="#cfe3ff" fill-opacity=".07"/>
      <rect x="210" y="190" width="47" height="33" rx="11" fill="#cfe3ff" fill-opacity=".07"/>
      <path d="M190 203 Q200 197 210 203"/><path d="M143 200 L124 195"/><path d="M257 200 L276 195"/>
    </g>
    <path d="M150 196 L160 192 M216 196 L228 191" stroke="#fff" stroke-width="2.4" stroke-linecap="round" opacity=".22"/>
    <!-- nose -->
    <path d="M199 212 C198 226 197 238 196 248" stroke="#fff3e8" stroke-width="3.2" fill="none" opacity=".28" stroke-linecap="round"/>
    <path d="M208 214 C210 230 215 244 214 253" stroke="#8a4a30" stroke-width="3.4" fill="none" opacity=".22" stroke-linecap="round"/>
    <ellipse cx="200" cy="251" rx="7" ry="4.6" fill="#fff0e4" opacity=".3"/>
    <g id="nose-l"><path d="M186 252 C182 262 190 266 196 262" stroke="#a86a4b" stroke-width="1.7" fill="none" stroke-linecap="round"/>
      <ellipse id="nostril-l" cx="193.5" cy="260" rx="3.4" ry="1.9" fill="#6d3626" opacity=".7"/></g>
    <g id="nose-r"><path d="M214 252 C218 262 210 266 204 262" stroke="#9c6044" stroke-width="1.7" fill="none" stroke-linecap="round"/>
      <ellipse id="nostril-r" cx="206.5" cy="260" rx="3.4" ry="1.9" fill="#6d3626" opacity=".7"/></g>
    <!-- nasolabial folds -->
    <path d="M181 262 C171 272 170 286 175 297" stroke="#9c6044" stroke-width="1.6" fill="none" opacity=".3" stroke-linecap="round"/>
    <path d="M219 262 C229 272 230 286 225 297" stroke="#8f573d" stroke-width="1.6" fill="none" opacity=".32" stroke-linecap="round"/>
    <!-- mouth -->
    <g id="mouth">
      <path id="mouth-inner" fill="url(#mouthG)"/>
      <rect id="teeth" x="168" y="272" width="64" height="9" rx="3" fill="#f6f1ea" clip-path="url(#mouth-clip)"/>
      <path id="tongue" fill="#b8505a" clip-path="url(#mouth-clip)"/>
      <path id="lip-upper" fill="url(#lipU)"/>
      <path id="lip-lower" fill="url(#lipL)"/>
      <path id="lip-shine" fill="#fff" opacity=".18"/>
      <path id="lips" fill="none" stroke="#6e2f29" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round"/>
    </g>
    </g>
  </g>

  <!-- hand cupped to the ear ("gapir, eshitmayapman") — slides up from below while listening to a quiet voice -->
  <g id="hand" opacity="0">
    <!-- forearm in the jacket sleeve, coming up from below -->
    <path d="M338 462 C334 400 328 330 318 278 L344 270 C356 320 372 392 384 462 Z" fill="url(#suitG)" stroke="#0e1730" stroke-width="1.2"/>
    <path d="M314 282 C320 276 340 270 346 274 L348 286 C340 282 324 286 318 292 Z" fill="#f4f6fa" stroke="#c9cfd9" stroke-width="1"/>
    <!-- palm (cupped, facing forward) just behind the ear -->
    <path d="M300 268 C294 252 296 232 304 222 C312 214 326 216 330 228 C334 242 332 260 324 272 C318 280 306 280 300 268 Z" fill="url(#skin)" stroke="#a9704c" stroke-width="1.2"/>
    <!-- four fingers rising behind the ear, tips curling forward over its back edge -->
    <path d="M304 226 C300 214 298 202 294 194" stroke="#a9704c" stroke-width="8.2" fill="none" stroke-linecap="round"/><path d="M304 226 C300 214 298 202 294 194" stroke="url(#skin)" stroke-width="6.6" fill="none" stroke-linecap="round"/>
    <path d="M311 222 C309 208 306 196 301 188" stroke="#a9704c" stroke-width="8.6" fill="none" stroke-linecap="round"/><path d="M311 222 C309 208 306 196 301 188" stroke="url(#skin)" stroke-width="7" fill="none" stroke-linecap="round"/>
    <path d="M318 222 C318 208 315 198 310 192" stroke="#a9704c" stroke-width="8.4" fill="none" stroke-linecap="round"/><path d="M318 222 C318 208 315 198 310 192" stroke="url(#skin)" stroke-width="6.8" fill="none" stroke-linecap="round"/>
    <path d="M325 228 C326 216 324 206 320 200" stroke="#a9704c" stroke-width="7.6" fill="none" stroke-linecap="round"/><path d="M325 228 C326 216 324 206 320 200" stroke="url(#skin)" stroke-width="6" fill="none" stroke-linecap="round"/>
    <path d="M294.5 196 l-1.2 -2.6 M301.5 190 l-1.2 -2.6 M310.5 194 l-1 -2.6 M320.4 202 l-.8 -2.4" stroke="#f7dcc6" stroke-width="2.4" stroke-linecap="round"/>
    <!-- thumb tucked under the ear lobe -->
    <path d="M302 262 C296 260 290 256 288 250" stroke="#a9704c" stroke-width="8.6" fill="none" stroke-linecap="round"/><path d="M302 262 C296 260 290 256 288 250" stroke="url(#skin)" stroke-width="7" fill="none" stroke-linecap="round"/>
    <path d="M306 232 C312 236 318 236 324 232" fill="none" stroke="#a9704c" stroke-width="1" opacity=".55"/>
  </g>
</svg>`;

const lerp = (a, b, k) => a + (b - a) * k;
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

/** setAttribute only when the value really changed (the rig writes ~40 attributes per frame). */
const SET = (el, k, v) => { const c = el.__a || (el.__a = {}); v = String(v); if (c[k] !== v) { c[k] = v; el.setAttribute(k, v); } };

export class Avatar {
  #scene; #sceneTr = ''; #frame = 0;
  /** soft | strict | savage — the mentor style picked in the sidebar. */
  style = 'strict';
  state = 'idle';
  emotion = 'neutral';
  anger = 0;
  #el = {};
  #cur = { ...NEUTRAL, open: 0.04, width: 1, round: 0, teeth: 0 };
  #mouthTarget = MOUTH.rest;
  #energy = 0;
  #vocal = null;                // 'laugh' | 'sigh' | 'gasp' | 'scoff' while a vocalization plays
  #inspecting = false;          // an image is being analysed → curious face while thinking
  #cues = [];                   // { name, start, dur }
  #blink = { start: -1e9, dur: 140, next: performance.now() + 800 + Math.random() * 2500, double: false };
  #saccade = { x: 0, y: 0, next: 0 };

  #host;
  constructor(host) {
    this.#host = host;
    host.innerHTML = SVG;
    try { host.querySelector('#hair-img')?.setAttribute('href', paintHair()); } catch (e) { console.warn('[hair]', e); }
    const q = (s) => host.querySelector(s);
    this.#el = {
      head: q('#head'), body: q('#body'), blush: q('#blush'), forehead: q('#forehead'), browL: q('#brow-l'), browR: q('#brow-r'), furrow: q('#furrow-lines'),
      browLt: q('#brow-l-tex'), browRt: q('#brow-r-tex'), lashL: q('#lash-l'), lashR: q('#lash-r'), hand: q('#hand'),
      lipU: q('#lip-upper'), lipL: q('#lip-lower'), shine: q('#lip-shine'),
      jaw: q('#jaw'), cheeks: q('#cheeks'), earL: q('#ear-l'), earR: q('#ear-r'), hair: q('#hair'), face: q('#face'), tie: q('#tie'),
      nostrilL: q('#nostril-l'), nostrilR: q('#nostril-r'), noseL: q('#nose-l'), noseR: q('#nose-r'),
      creaseL: q('#crease-l'), creaseR: q('#crease-r'), lashLowL: q('#lashlow-l'), lashLowR: q('#lashlow-r'),
      pupilCores: host.querySelectorAll('.pupil-core'), throat: q('#throat'), adams: q('#adams'),
      pupils: host.querySelectorAll('.pupil'), flush: q('#flush'),
      eyeL: [q('#eye-white-l'), q('#eye-clip-shape-l'), q('#lid-top-l'), q('#lid-low-l')],
      eyeR: [q('#eye-white-r'), q('#eye-clip-shape-r'), q('#lid-top-r'), q('#lid-low-r')],
      inner: q('#mouth-inner'), clip: q('#mouth-inner-clip'), lips: q('#lips'), teeth: q('#teeth'), tongue: q('#tongue'),
    };
    requestAnimationFrame(this.#tick);
  }

  // ---------- public API ----------
  setState(s) {
    if (!STATES.includes(s)) { this.setEmotion(s); s = 'speaking'; }   // back-compat: setState('encouraging')
    if (s === 'listening' && this.state !== 'listening') { this.cue('nod'); this.#listenSince = performance.now(); this.#lastVoice = 0; }
    this.state = s;
  }
  setEmotion(e) { if (EMOTIONS.includes(e)) this.emotion = e; }
  setAnger(level) { this.anger = clamp(Number(level) || 0, 0, 10); }
  /** 0..10 — bashfulness (from shyLevel): pink cheeks + eyes avoiding contact. */
  setShy(level) { this.shy = clamp(Number(level) || 0, 0, 10); }
  shy = 0;
  /** Trigger a timed micro-expression (eye_roll, squint, wide_eyes_shock, smirk, brow_furrow, slow_blink, head_shake, nod, brow_raise). */
  cue(name) {
    name = { nod: 'nod_agree' }[name] ?? name;
    const c = CUES[name];
    if (!c) return;
    if (name === 'slow_blink') this.#blink.next = performance.now() + 1200;   // don't double up with a normal blink
    this.#cues.push({ name, start: performance.now(), dur: c.dur });
  }
  clearCues() { this.#cues = []; }
  setMouth(shape) { this.#mouthTarget = MOUTH[shape] || MOUTH.rest; }
  setEnergy(v) { this.#energy = clamp(v, 0, 1); }
  /** Non-verbal vocalization in progress → mouth/face follow audio instead of visemes. */
  setVocal(type) { this.#vocal = type; }
  /**
   * Emphasis on a word, shaped by the CURRENT emotion so the face agrees with the words:
   *   angry / annoyed → brows drive DOWN and in, a forward head jab      mocking → one brow arches
   *   hurt / shy      → inner brows lift (soft, pleading)                  question → both brows up + head tilt
   *   teaching / neutral → a light brow lift and nod
   */
  emphasis(kind = 'stress') {
    this.#flashKind = kind; this.#flash = 1; this.#lastFlash = performance.now();
  }
  /** Blink at the end of a sentence (people blink at phrase boundaries, not mid-word). */
  blinkSoon(delay = 60) { setTimeout(() => { this.#blinkNow = true; }, delay); }
  #blinkNow = false;

  #applyEmphasis(f) {
    const k = this.#flash;
    if (k < 0.02) return;
    const e = this.emotion;
    if (this.#flashKind === 'question') { f.browL += 0.35 * k; f.browR += 0.35 * k; f.tilt += 3 * k; return; }
    if (['angry_flame', 'angry', 'annoyed_cutoff', 'scolding'].includes(e)) {
      f.furrow += 0.55 * k; f.browL -= 0.22 * k; f.browR -= 0.22 * k; f.nod += 2 * k; f.squint += 0.2 * k; return;
    }
    if (['mocking', 'skeptical'].includes(e)) { f.browR += 0.45 * k; f.smileR += 0.15 * k; return; }
    if (['hurt_apologetic', 'apologetic', 'shy_flustered'].includes(e)) { f.furrow -= 0.35 * k; f.browL += 0.1 * k; f.browR += 0.1 * k; return; }
    const big = this.#flashKind === 'exclaim' ? 1.5 : 1;
    f.browL += 0.22 * k * big; f.browR += 0.22 * k * big; f.nod += 1.2 * k * big;
  }

  /** Persistent body orientation: turn -1 (left) .. 1 (right), pitch -1 (up) .. 1 (down). Eased, whole upper body. */
  setBodyLook(turn = 0, pitch = 0) { this.#bodyLook = { turn: clamp(turn, -1, 1), pitch: clamp(pitch, -1, 1) }; }
  /** Lying down / resting: heavy, half-closed eyes. */
  setRest(on) { this.#rest = !!on; }
  /** Sipping tea: lips round onto the piala, eyes close with pleasure. */
  setSip(on) { this.#sip = !!on; if (on) this.setMouth('U'); else this.setMouth('rest'); }
  #bodyLook = { turn: 0, pitch: 0 }; #rest = false; #sip = false;
  // secondary motion / micro-life state
  #hairSpring = { x: 0, v: 0 }; #prevRot = 0; #prevE = 0; #flash = 0; #pupil = 3.1;
  #flashKind = 'stress'; #lastFlash = 0;
  /** true while real audio plays (loudness-based stress detection); browser voice uses word-level emphasis instead */
  autoEmphasis = false;

  /** Live mic level while listening (0..1, 0 = silence). A pause after speech earns a small "mm-hm" nod. */
  setListenLevel(level) {
    const now = performance.now();
    if (level > 0.08) { this.#voiceSince ||= now; this.#lastVoice = now; return; }
    const spoke = this.#voiceSince && this.#lastVoice - this.#voiceSince > 450;          // a real phrase, not a click
    if (spoke && now - this.#lastVoice > 280 && now - this.#lastNod > 1600 && this.state === 'listening') {
      this.#lastNod = now; this.#voiceSince = 0;
      this.cue('nod_small');
    }
  }
  #voiceSince = 0; #lastVoice = 0; #lastNod = 0; #listenSince = 0;

  /** User attached an image: lean in and examine it until the reply starts. */
  inspect(on = true) { this.#inspecting = on; if (on) this.cue('curious_inspect'); }

  // ---------- frame ----------
  #baseExpression() {
    const speaking = this.state === 'speaking';
    if (this.#vocal === 'laugh') return EXPR.laughing;
    if (this.state === 'thinking' && this.#inspecting) return EXPR.curious;   // looking at an uploaded image
    if (this.#vocal === 'gasp') return EXPR.shocked;
    if (speaking) return EXPR[this.emotion] || EXPR.neutral;
    return EXPR[this.state] || EXPR.idle;
  }

  #tick = () => {
    const now = performance.now();        // one clock for cues, blinks and frames (rAF's timestamp can lag it)
    const c = this.#cur, base = this.#baseExpression();
    const a = this.anger / 10;
    const laughing = this.#vocal === 'laugh' || (this.state === 'speaking' && this.emotion === 'laughing');
    const warm = laughing || this.#inspecting ||
      ['encouraging', 'apologetic', 'curious', 'shy_flustered', 'hurt_apologetic'].includes(this.emotion);

    // 1+2) base expression with anger modulation (anger lingers on the face, even when idle)
    const target = { ...base };
    if (!warm) {
      target.furrow = clamp(target.furrow + 0.6 * a, -1, 1);
      target.browL -= 0.3 * a; target.browR -= 0.3 * a;
      target.lidL -= 0.15 * a; target.lidR -= 0.15 * a;
      target.smileL -= 0.3 * a; target.smileR -= 0.3 * a;
      target.flush = clamp(target.flush + 0.45 * Math.max(0, a - 0.3), 0, 1);
    }
    // the chosen style is part of his character, always visible
    if (!laughing && !this.#sip) {
      if (this.style === 'soft') { target.smileL += 0.28; target.smileR += 0.28; target.furrow -= 0.2; target.browL += 0.1; target.browR += 0.1; target.squint += 0.1; }
      else if (this.style === 'savage') { target.furrow = clamp(target.furrow + 0.5, -1, 1.2); target.browL -= 0.18; target.browR -= 0.18;
        target.smileL -= 0.2; target.smileR -= 0.2; target.squint += 0.18; target.flush = Math.max(target.flush, 0.18); }
      else { target.furrow += 0.12; target.smileL -= 0.06; target.smileR -= 0.06; }
    }
    const sh = this.shy / 10;
    target.blush = clamp(target.blush + sh, 0, 1);
    if (sh > 0.3 && this.#vocal !== 'laugh') { target.lookX += 0.4 * sh; target.lookY += 0.3 * sh; }
    target.turn = this.#bodyLook.turn; target.pitch = this.#bodyLook.pitch;
    if (this.#bodyLook.pitch < 0) { target.browL += 0.35; target.browR += 0.35; }           // looking up lifts the brows
    if (this.#bodyLook.pitch > 0) { target.lidL = Math.min(target.lidL, 0.62); target.lidR = Math.min(target.lidR, 0.62); }   // looking down: lids drop
    if (this.#rest) { target.lidL = Math.min(target.lidL, 0.5); target.lidR = Math.min(target.lidR, 0.5); target.smileL += 0.2; target.smileR += 0.2; }
    if (this.#sip) { target.lidL = 0.35; target.lidR = 0.35; target.browL += 0.25; target.browR += 0.25; }
    if (this.state === 'listening') {
      const quietFor = now - Math.max(this.#lastVoice, this.#listenSince);
      target.hand = now - this.#listenSince > 1000 && quietFor > 1100 ? 1 : 0;     // "louder please" — cup the ear
    }
    for (const k in NEUTRAL) c[k] = lerp(c[k], target[k], k === 'flush' || k === 'blush' ? 0.04 : k === 'hand' ? 0.12 : k === 'turn' || k === 'pitch' ? 0.07 : 0.1);

    // 3) procedural life
    const f = { ...c };                                   // per-frame output (cues are additive on top)
    const t = now / 1000;
    const b = this.#blink;
    if (this.#blinkNow) { this.#blinkNow = false; b.next = 0; }
    if (now > b.next) {
      const slow = this.emotion === 'skeptical' || (this.state === 'thinking' && Math.random() < 0.3);
      b.start = now; b.dur = slow ? 380 : 130 + Math.random() * 40;
      b.double = !slow && Math.random() < 0.15;
      b.next = now + (this.anger > 6 ? 3500 : 1800) + Math.random() * 4200;   // angry stare → fewer blinks
    }
    const bp = (now - b.start) / b.dur;
    if (bp >= 0 && bp <= 1) { const k = Math.sin(bp * Math.PI); f.lidL -= 1.2 * k; f.lidR -= 1.2 * k; }
    else if (b.double && bp > 1.6 && bp <= 2.6) { const k = Math.sin((bp - 1.6) * Math.PI); f.lidL -= 1.2 * k; f.lidR -= 1.2 * k; }

    if (now > this.#saccade.next) {
      const range = this.state === 'listening' ? 0.07 : this.state === 'speaking' ? 0.12 : 0.35;   // talking/listening → eye contact
      this.#saccade = { x: (Math.random() - 0.5) * range, y: (Math.random() - 0.5) * range * 0.6,
                        next: now + 350 + Math.random() * 1600 };
    }
    f.lookX += this.#saccade.x + f.turn * 1.25; f.lookY += this.#saccade.y + f.pitch * 1.15;

    // 4) micro-expressions
    this.#cues = this.#cues.filter((q) => now - q.start < q.dur);
    for (const q of this.#cues) {
      const p = clamp((now - q.start) / q.dur, 0, 1);
      const d = CUES[q.name].f(p, ease(envelope(p)));
      for (const k in d) f[k] += d[k];
    }

    // emphasis: a jump in voice energy = a stressed syllable → quick brow flash + tiny nod
    // Real-audio mode only (Azure): a big loudness jump counts as stress, at most every 700 ms.
    if (this.autoEmphasis && this.state === 'speaking' && this.#energy - this.#prevE > 0.35 && now - this.#lastFlash > 700)
      this.emphasis('stress');
    this.#prevE = this.#energy;
    this.#flash *= 0.9;
    this.#applyEmphasis(f);

    // 5) mouth
    const m = this.#mouthTarget, e = this.#energy;
    let open, width = m.width, round = m.round, teeth = m.teeth || 0;
    if (this.#vocal === 'laugh') { open = 0.15 + e * 0.95; width = 1.15; round = 0; teeth = 1; }
    else if (this.#vocal === 'gasp') { open = 0.3 + e * 0.6; width = 0.75; round = 0.7; }
    else if (this.#vocal === 'sigh') { open = 0.12 + e * 0.35; width = 0.9; round = 0.3; }
    else if (this.#vocal === 'scoff') { open = e * 0.25; width = 1.05; }
    else if (this.state === 'speaking') open = m.open * (0.7 + 0.6 * e);
    else open = m.open;
    open = Math.max(open, f.jaw);
    c.open = lerp(c.open, open, 0.45);
    c.width = lerp(c.width, width, 0.4);
    c.round = lerp(c.round, round, 0.4);
    c.teeth = lerp(c.teeth, teeth || (c.open > 0.25 && c.round < 0.5 ? 1 : 0), 0.4);

    // head motion: breathing sway + tilt + nod + shake + lean; laughter shakes the head with the audio
    const laughBob = laughing ? e * 4 * Math.sin(t * 18) : 0;
    const speechBob = this.state === 'speaking' && !laughing ? e * 2 * Math.sin(t * 9) : 0;
    const dy = f.nod + f.lean + laughBob + speechBob + Math.sin(t * 1.6) * 1.2;
    const rot = f.tilt + Math.sin(t * 0.7) * 1.2 + f.shake;
    const sc = 1 + clamp(f.fwd, 0, 1.5) * 0.045;                      // lean in = slightly bigger, a bit lower
    // Idle (not speaking / listening / laughing): the face moves slowly → draw every 2nd frame (30 fps). The maths above
    // still runs every frame, so blinks and transitions keep their speed; only the expensive repaint is halved.
    this.#frame = (this.#frame + 1) | 0;
    if (this.state === 'idle' && !this.#vocal && !laughing && (this.#frame & 1)) { requestAnimationFrame(this.#tick); return; }
    // whole-body look: a 3/4 turn (narrower, shifted, sheared body + head) · bow forward to look down · chin up to look up
    const at = Math.abs(f.turn), down = Math.max(0, f.pitch), up = Math.max(0, -f.pitch);
    SET(this.#el.head, 'transform',
      `translate(${(f.shake * 0.5 + f.turn * 18).toFixed(2)} ${(dy + f.fwd * 4 + down * 26 - up * 8).toFixed(2)}) rotate(${(rot + f.turn * 4).toFixed(2)} 200 320) `
      + `translate(200 320) scale(${(sc * (1 - 0.1 * at) * (1 - 0.03 * down)).toFixed(3)} ${(sc * (1 - 0.06 * up) * (1 - 0.03 * down)).toFixed(3)}) translate(-200 -320)`);
    // The body turns around its base (y=460): narrower + a little skew; the angle is chosen so the neck top moves
    // exactly as far as the head (head 18 · base 8 → 10 units over 168) — the neck never separates from the head.
    const skew = -Math.atan(10 * f.turn / 168) * 180 / Math.PI;
    SET(this.#el.body, 'transform', `translate(${(f.turn * 8).toFixed(2)} ${(Math.sin(t * 1.6) * 1.5 + (laughing ? e * 1.5 : 0) + down * 12).toFixed(2)}) `
      + `translate(200 460) skewX(${skew.toFixed(2)}) scale(${(1 - 0.14 * at).toFixed(3)} ${(1 + Math.sin(t * 1.6) * 0.006 - down * 0.05).toFixed(4)}) translate(-200 -460)`);
    // the face slides inside the head (3/4 view); bowing shows more of the crown, looking up more of the chin
    SET(this.#el.face, 'transform', `translate(${(f.turn * 10).toFixed(2)} ${(down * 12 - up * 7).toFixed(2)})`);

    const hd = clamp(f.hand, 0, 1);
    SET(this.#el.hand, 'opacity', Math.min(1, hd * 2.5).toFixed(2));
    SET(this.#el.hand, 'transform', `translate(${(rot * 0.6).toFixed(1)} ${((1 - hd) * 240 + dy * 0.6).toFixed(1)})`);
    this.#life(f, c, t, rot, laughing);
    this.#drawBrows(f);
    this.#drawEyes(f);
    SET(this.#el.flush, 'opacity', clamp(f.flush, 0, 1).toFixed(2));
    SET(this.#el.blush, 'opacity', clamp(f.blush, 0, 1).toFixed(2));
    // raised brows wrinkle the forehead too (surprise / teasing), not only the explicit crease channel
    const raise = Math.max(0, (f.browL + f.browR) / 2 - 0.35) * 1.3;
    SET(this.#el.forehead, 'opacity', clamp(f.forehead + raise, 0, 0.9).toFixed(2));
    SET(this.#el.forehead, 'transform', `translate(0 ${(-Math.max(0, (f.browL + f.browR) / 2) * 4).toFixed(1)})`);
    this.#drawMouth(c, f);
    requestAnimationFrame(this.#tick);
  };

  /** Everything that makes the face and clothes feel alive: jaw, cheeks, nostrils, pupils, hair, ears, tie, throat. */
  #life(f, c, t, rot, laughing) {
    const el = this.#el;
    const open = c.open, avgSmile = Math.max(0, (f.smileL + f.smileR) / 2);
    // jaw & chin follow the mouth (the biggest single realism cue)
    const jaw = open * 24 * 0.42;
    SET(el.jaw, 'transform', `translate(0 ${jaw.toFixed(2)}) scale(1 ${(1 + open * 0.03).toFixed(3)})`);
    SET(el.throat, 'transform', `translate(0 ${(jaw * 0.35).toFixed(2)})`);
    SET(el.adams, 'transform', `translate(0 ${(Math.sin(t * 9) * this.#energy * 1.2 + jaw * 0.2).toFixed(2)})`);
    // cheeks: lift with smile/squint, pulled down by a wide-open jaw
    const lift = avgSmile * 3.4 + clamp(f.squint, 0, 1) * 1.8 - jaw * 0.28;
    SET(el.cheeks, 'transform', `translate(0 ${(-lift).toFixed(2)})`);
    // ears ride up a hair with a big smile, down with the jaw
    const ear = -avgSmile * 1.2 + jaw * 0.12;
    const tl = clamp(f.turn, -1, 1);
    // turning to his left (viewer's right, turn>0) shows his right ear (viewer's left) and hides the other one
    SET(el.earL, 'transform', `translate(${(tl * 4).toFixed(2)} ${ear.toFixed(2)}) translate(132 0) scale(${(1 + 0.25 * tl).toFixed(3)} 1) translate(-132 0)`);
    SET(el.earR, 'transform', `translate(${(tl * 4).toFixed(2)} ${ear.toFixed(2)}) translate(268 0) scale(${(1 - 0.25 * tl).toFixed(3)} 1) translate(-268 0)`);
    // nostrils: breathing in, anger, and stressed syllables flare them
    const breath = (Math.sin(t * 1.6) + 1) / 2;
    const flare = breath * 0.14 + (this.anger / 10) * 0.35 + this.#flash * 0.3 + (laughing ? 0.2 : 0);
    SET(el.nostrilL, 'rx', (3.4 * (1 + flare)).toFixed(2)); SET(el.nostrilR, 'rx', (3.4 * (1 + flare)).toFixed(2));
    SET(el.noseL, 'transform', `translate(${(-flare * 2.2).toFixed(2)} 0)`);
    SET(el.noseR, 'transform', `translate(${(flare * 2.2).toFixed(2)} 0)`);
    // pupils: wider when listening / shy / interested, tighter when angry or shocked
    const want = 3.1 * (this.state === 'listening' ? 1.14 : 1)
      * ({ angry_flame: 0.82, angry: 0.85, shocked: 0.78, shy_flustered: 1.2, laughing: 1.06, curious: 1.12 }[this.emotion] ?? 1)
      * (1 + Math.sin(t * 0.9) * 0.025);
    this.#pupil = lerp(this.#pupil, want, 0.05);
    el.pupilCores.forEach((p) => SET(p, 'r', this.#pupil.toFixed(2)));
    // hair: spring that lags behind head rotation (secondary motion) + a faint idle sway
    const sp = this.#hairSpring, vel = rot - this.#prevRot;
    this.#prevRot = rot;
    sp.v += (-sp.x * 0.12) - sp.v * 0.18 - vel * 0.9;
    sp.x += sp.v;
    const hairAngle = clamp(sp.x, -4, 4) + Math.sin(t * 0.9) * 0.25;
    SET(el.hair, 'transform', `rotate(${hairAngle.toFixed(2)} 200 200) translate(0 ${(laughing ? this.#energy * 0.8 : 0).toFixed(2)})`);
    el.hairFront ??= this.#host.querySelector('#hair-front');
    if (el.hairFront) {
      const talk = this.state === 'speaking' ? this.#energy : 0;
      const bounce = Math.sin(t * 11) * talk * 1.1 + Math.sin(t * 1.3) * 0.4;
      SET(el.hairFront, 'transform', `rotate(${(sp.x * 0.8 + bounce).toFixed(2)} 215 132) translate(0 ${(talk * 0.6).toFixed(2)})`);
    }
    // tie swings a little with breathing, turning and laughter
    const sway = Math.sin(t * 1.3) * 0.7 + f.turn * 3 + (laughing ? Math.sin(t * 18) * this.#energy * 2 : 0);
    SET(el.tie, 'transform', `rotate(${sway.toFixed(2)} 200 372)`);
    // eyelid crease & lower lashes move with the lid / squint
    for (const [crease, low, lid, cx] of [[el.creaseL, el.lashLowL, f.lidL, 166], [el.creaseR, el.lashLowR, f.lidR, 234]]) {
      const closed = clamp(1 - lid, 0, 1);
      SET(crease, 'd', `M${cx - 15} ${(197 + closed * 3).toFixed(1)} Q${cx} ${(188 + closed * 7).toFixed(1)} ${cx + 15} ${(197 + closed * 3).toFixed(1)}`);
      const sq = clamp(f.squint, 0, 1) * 3;
      SET(low, 'd', `M${cx - 11} ${(215 - sq).toFixed(1)} Q${cx} ${(220 - sq * 1.4).toFixed(1)} ${cx + 11} ${(215 - sq).toFixed(1)}`);
    }
    // background parallax hint for the stage (the room shifts a little opposite to the head)
    // Move ONLY the background layer (composited transform) — never touch a CSS variable on the card: that
    // forced a style recalc of the whole avatar + a re-raster of the blurred room on every frame (the lag).
    this.#scene ??= this.#host.closest?.('.call-card')?.querySelector('.scene') ?? null;
    if (this.#scene) {
      const tr = `translate3d(${(f.turn * -12).toFixed(1)}px, ${(breath * -1.5).toFixed(1)}px, 0)`;
      if (tr !== this.#sceneTr) { this.#sceneTr = tr; this.#scene.style.transform = tr; }
    }
  }

  #drawBrows(f) {
    const fu = clamp(f.furrow, -1, 1.2);
    const brow = (outerX, innerX, y, raise, dir) => {
      const oy = y - raise * 10 + 1.5;                      // tail sits a touch lower than the head of the brow
      const iy = y - raise * 8 + fu * 7;                    // inner end drops when furrowing (rises when negative)
      const ix = innerX + dir * fu * 3;                     // and pulls toward the nose
      const mx = outerX + (ix - outerX) * 0.42, my = Math.min(oy, iy) - 8 + fu * 3;   // arch toward the outer third
      return `M${outerX} ${oy.toFixed(1)} Q${mx.toFixed(1)} ${my.toFixed(1)} ${ix.toFixed(1)} ${iy.toFixed(1)}`;
    };
    const l = brow(146, 187, 179, f.browL, 1), r = brow(254, 213, 179, f.browR, -1);
    SET(this.#el.browL, 'd', l); SET(this.#el.browLt, 'd', l);
    SET(this.#el.browR, 'd', r); SET(this.#el.browRt, 'd', r);
    SET(this.#el.furrow, 'opacity', clamp((fu - 0.35) * 1.5, 0, 0.8).toFixed(2));
  }

  #drawEyes(f) {
    const lx = clamp(f.lookX, -1.4, 1.4) * 4.6, ly = clamp(f.lookY, -1.4, 1.4) * 3.4;
    this.#el.pupils.forEach((p) => SET(p, 'transform', `translate(${lx.toFixed(2)} ${ly.toFixed(2)})`));
    const eye = ([white, clipShape, top, low], lash, lid, cx) => {
      const ry = 8.6 * (1 + Math.max(0, lid - 1) * 0.9);     // wide-open eyes get taller
      SET(white, 'ry', ry.toFixed(1)); SET(clipShape, 'ry', ry.toFixed(1));
      const closed = clamp(1 - lid, 0, 1);                   // 0 = open, 1 = shut
      const topY = 206 - ry - 1;
      const edge = topY + closed * (2 * ry + 2);             // where the upper lid's rim is now
      SET(top, 'y', topY.toFixed(1));
      SET(top, 'height', (closed * (2 * ry + 2)).toFixed(1));
      SET(low, 'y', (206 + ry - clamp(f.squint, 0, 1) * ry * 0.9).toFixed(1));
      // lash line rides on the lid rim: arched when open, a flat downward curve when shut
      const arch = 5 * (1 - closed) - 2 * closed;
      SET(lash, 'd', `M${cx - 14.5} ${(edge + 3.2).toFixed(1)} Q${cx} ${(edge + 1 - arch).toFixed(1)} ${cx + 14.5} ${(edge + 3.2).toFixed(1)}`);
    };
    eye(this.#el.eyeL, this.#el.lashL, f.lidL, 166);
    eye(this.#el.eyeR, this.#el.lashR, f.lidR, 234);
  }

  #drawMouth(c, f) {
    const cx = 200, cy = 286;
    const w = 23 * c.width * (1 - 0.25 * c.round);
    const h = 24 * c.open;
    const corner = (s) => -clamp(s, -1, 1.3) * 6 * (1 - c.open * 0.5);    // + = corner up
    const ly = cy + corner(f.smileL), ry = cy + corner(f.smileR);
    const avg = (f.smileL + f.smileR) / 2;
    const up = 2 + h * 0.3, low = 1 + h + Math.max(0, avg) * 2;
    const xL = cx - w, xR = cx + w;
    const L = `${xL.toFixed(1)},${ly.toFixed(1)}`, R = `${xR.toFixed(1)},${ry.toFixed(1)}`;
    const opening = `M${L} Q${cx},${(cy - up).toFixed(1)} ${R} Q${cx},${(cy + low).toFixed(1)} ${L} Z`;
    // upper lip: cupid's bow on top, the opening's upper edge underneath
    const ut = cy - up - 5.2 + Math.max(0, avg) * 1.2;
    const upper = `M${L} C${(cx - w * 0.55).toFixed(1)},${(ut + 1.5).toFixed(1)} ${cx - 6},${(ut - 1.4).toFixed(1)} ${cx},${(ut + 1.8).toFixed(1)}`
      + ` C${cx + 6},${(ut - 1.4).toFixed(1)} ${(cx + w * 0.55).toFixed(1)},${(ut + 1.5).toFixed(1)} ${R}`
      + ` Q${cx},${(cy - up + (h < 2 ? 1.5 : 0)).toFixed(1)} ${L} Z`;
    // lower lip: fuller, its top follows the opening's lower edge
    const lb = cy + low + 7;
    const lower = `M${L} Q${cx},${(cy + low - (h < 2 ? 1.5 : 0)).toFixed(1)} ${R}`
      + ` C${(cx + w * 0.55).toFixed(1)},${lb.toFixed(1)} ${(cx - w * 0.55).toFixed(1)},${lb.toFixed(1)} ${L} Z`;
    const line = h < 2 ? `M${L} Q${cx},${(cy + 1 - avg * 3).toFixed(1)} ${R}` : opening;
    SET(this.#el.inner, 'd', h < 2 ? '' : opening);
    SET(this.#el.clip, 'd', opening);
    SET(this.#el.lipU, 'd', upper);
    SET(this.#el.lipL, 'd', lower);
    SET(this.#el.shine, 'd', `M${cx - w * 0.35} ${(lb - 4.5).toFixed(1)} Q${cx} ${(lb - 2.2).toFixed(1)} ${cx + w * 0.3} ${(lb - 4.5).toFixed(1)} Q${cx} ${(lb - 3.4).toFixed(1)} ${cx - w * 0.35} ${(lb - 4.5).toFixed(1)} Z`);
    SET(this.#el.lips, 'd', line);
    SET(this.#el.teeth, 'y', (cy - up + 0.5).toFixed(1));
    SET(this.#el.teeth, 'opacity', (h < 3 ? 0 : c.teeth).toFixed(2));
    SET(this.#el.tongue, 'd', h < 8 ? '' : `M${cx - w * 0.6},${cy + low} Q${cx},${cy + low * 0.35} ${cx + w * 0.6},${cy + low} Z`);
  }
}
