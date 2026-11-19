// Teacher's whole body: a 2D skeletal puppet (shoulders/elbows/hips/knees, 2-bone IK for the hands) drawn in an
// SVG room, with the existing expressive head (avatar.js — face, lip-sync, cues) mounted on its neck.
//
//   close-up  (default): the bust you talk to; look_* turns head + shoulders via avatar.setBodyLook()
//   wide shot (sit / lie_down / drink_tea): camera pulls back, the room appears (chair, xontaxta, choynak, piyola,
//             ko'rpacha) and the head flies onto the puppet's neck
//
// BodyDirector.perform(action) returns a Promise that resolves when the action is "done" — the speech player
// waits on it when the reply says so (sip the tea FIRST, then "Xo'sh, endi darsni boshlaymiz!").

const SVGNS = 'http://www.w3.org/2000/svg';
const VB_W = 680, VB_H = 620;
const HEAD_ANCHOR = { x: 200 / 400, y: 345 / 460 };      // collar point inside the avatar's own viewBox
const HEAD_W = 214;                                        // avatar host width in scene units (head ≈ 107 wide)
const UPPER = 92, FORE = 84, THIGH = 104, SHIN = 100, TORSO = 150;
const lerp = (a, b, k) => a + (b - a) * k;
const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const POSTURE = {
  stand: { hipX: 340, hipY: 382, lie: 0, thighFs: 1, sit: 0 },
  sit:   { hipX: 340, hipY: 432, lie: 0, thighFs: 0.33, sit: 1 },
  lie:   { hipX: 430, hipY: 500, lie: 1, thighFs: 1, sit: 0 },
};

const ROOM = `
<defs>
  <linearGradient id="bFloor" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#6b4a33"/><stop offset="1" stop-color="#3a2618"/></linearGradient>
  <linearGradient id="bWood" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#8a5a36"/><stop offset="1" stop-color="#4e3020"/></linearGradient>
  <linearGradient id="bWoodSide" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#5a3822"/><stop offset=".5" stop-color="#8d5d38"/><stop offset="1" stop-color="#4a2e1c"/></linearGradient>
  <linearGradient id="bSuit" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#2f4777"/><stop offset="1" stop-color="#18264a"/></linearGradient>
  <linearGradient id="bSuitSide" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#1a2a50"/><stop offset=".45" stop-color="#2c4374"/><stop offset="1" stop-color="#16223f"/></linearGradient>
  <linearGradient id="bLapel" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#3a5590"/><stop offset="1" stop-color="#1c2b4f"/></linearGradient>
  <linearGradient id="bPants" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#18223d"/><stop offset=".5" stop-color="#2a3a63"/><stop offset="1" stop-color="#151e36"/></linearGradient>
  <linearGradient id="bThigh" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#4a64a0"/><stop offset="1" stop-color="#2c3f6e"/></linearGradient>
  <linearGradient id="bShoe" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#5a3420"/><stop offset="1" stop-color="#24140b"/></linearGradient>
  <linearGradient id="bCushion" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#a3283d"/><stop offset="1" stop-color="#5e1222"/></linearGradient>
  <linearGradient id="bShirt" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ffffff"/><stop offset="1" stop-color="#d6dde9"/></linearGradient>
  <pattern id="bTie" width="9" height="9" patternUnits="userSpaceOnUse" patternTransform="rotate(40)">
    <rect width="9" height="9" fill="#7d1f3b"/><rect width="9" height="2.2" fill="#c0a050"/>
  </pattern>
  <pattern id="bAtlas" width="26" height="40" patternUnits="userSpaceOnUse">
    <rect width="26" height="40" fill="#7a1f3a"/><path d="M0 20 Q6.5 0 13 20 T26 20" fill="none" stroke="#e8b84a" stroke-width="5"/>
    <path d="M0 26 Q6.5 6 13 26 T26 26" fill="none" stroke="#2b6cb0" stroke-width="3"/>
  </pattern>
  <pattern id="bRugField" width="34" height="12" patternUnits="userSpaceOnUse">
    <rect width="34" height="12" fill="#9b2630"/>
    <path d="M17 1 L23 6 L17 11 L11 6 Z" fill="#c9a14a" opacity=".85"/><circle cx="17" cy="6" r="1.6" fill="#1f2f55"/>
    <path d="M0 6 L4 3 L4 9 Z M34 6 L30 3 L30 9 Z" fill="#e8d8b0" opacity=".5"/>
  </pattern>
  <pattern id="bCloth" width="16" height="16" patternUnits="userSpaceOnUse">
    <rect width="16" height="16" fill="#b5283c"/><rect x="0" y="0" width="16" height="3" fill="#e2b13c"/><rect x="0" y="8" width="16" height="2" fill="#1f4f9a"/>
  </pattern>
  <radialGradient id="bSkin" cx="40%" cy="35%" r="75%"><stop offset="0" stop-color="#f3caa6"/><stop offset="1" stop-color="#c88d66"/></radialGradient>
  <radialGradient id="bPorcelain" cx="35%" cy="30%" r="80%"><stop offset="0" stop-color="#ffffff"/><stop offset=".7" stop-color="#e8eef7"/><stop offset="1" stop-color="#b9c6da"/></radialGradient>
  <radialGradient id="bShadow" cx="50%" cy="50%" r="50%"><stop offset="0" stop-color="#000" stop-opacity=".45"/><stop offset="1" stop-color="#000" stop-opacity="0"/></radialGradient>
  <g id="bPaxta"><circle r="3.2" fill="#1f4fa8"/><circle cx="-3" cy="-2.6" r="2.2" fill="#2a63c2"/><circle cx="3" cy="-2.6" r="2.2" fill="#2a63c2"/>
    <path d="M-6 3 Q-3 1 0 3 Q3 1 6 3" fill="none" stroke="#1f4fa8" stroke-width="1.2"/></g>
</defs>

<!-- wooden floor with perspective planks -->
<rect x="-300" y="548" width="1280" height="240" fill="url(#bFloor)"/>
<g stroke="#2a1a10" stroke-width="1.4" opacity=".55">
  <path d="M-60 548 L-260 640"/><path d="M30 548 L-110 640"/><path d="M120 548 L40 640"/><path d="M210 548 L180 640"/>
  <path d="M300 548 L310 640"/><path d="M390 548 L440 640"/><path d="M480 548 L580 640"/><path d="M570 548 L720 640"/>
  <path d="M660 548 L860 640"/><path d="M750 548 L1000 640"/>
</g>
<rect x="-300" y="540" width="1280" height="10" fill="#2b1c13"/>

<!-- gilam: a real rug in perspective (border · field · medallion · fringe) -->
<g id="b-rug">
  <ellipse cx="340" cy="590" rx="270" ry="22" fill="url(#bShadow)" opacity=".5"/>
  <path d="M176 556 L504 556 L590 624 L90 624 Z" fill="#6e1820"/>
  <path d="M188 559 L492 559 L566 624 L114 624 Z" fill="#1f2f55"/>
  <path d="M188 559 L492 559 L566 624 L114 624 Z" fill="none" stroke="#d8b25a" stroke-width="1.5" stroke-dasharray="3 4"/>
  <path d="M204 563 L476 563 L540 624 L140 624 Z" fill="url(#bRugField)"/>
  <ellipse cx="340" cy="593" rx="86" ry="17" fill="#1f2f55"/>
  <ellipse cx="340" cy="593" rx="62" ry="12" fill="#d8b25a"/>
  <ellipse cx="340" cy="593" rx="40" ry="7.5" fill="#9b2630"/>
  <path d="M310 593 L340 587 L370 593 L340 599 Z" fill="#f1e4c4"/>
  <g stroke="#efe2c2" stroke-width="1.4" opacity=".9">
    <path d="M180 556 v-6 M190 556 v-6 M200 556 v-6 M210 556 v-6 M220 556 v-6 M230 556 v-6 M240 556 v-6 M250 556 v-6 M260 556 v-6 M270 556 v-6 M280 556 v-6 M290 556 v-6 M300 556 v-6 M310 556 v-6 M320 556 v-6 M330 556 v-6 M340 556 v-6 M350 556 v-6 M360 556 v-6 M370 556 v-6 M380 556 v-6 M390 556 v-6 M400 556 v-6 M410 556 v-6 M420 556 v-6 M430 556 v-6 M440 556 v-6 M450 556 v-6 M460 556 v-6 M470 556 v-6 M480 556 v-6 M490 556 v-6 M500 556 v-6"/>
  </g>
</g>

<g id="b-kurpacha" opacity="0">
  <rect x="96" y="486" width="520" height="40" rx="14" fill="url(#bAtlas)"/>
  <rect x="96" y="486" width="520" height="40" rx="14" fill="none" stroke="#3b0f1d" stroke-width="2"/>
  <rect x="104" y="452" width="96" height="42" rx="18" fill="#e9d9b5" stroke="#b89a64" stroke-width="2"/>
</g>

<!-- wooden chair with a red cushion (behind the figure) -->
<g id="b-chair" opacity="0">
  <ellipse cx="340" cy="562" rx="120" ry="10" fill="url(#bShadow)"/>
  <rect x="272" y="456" width="10" height="94" rx="3" fill="#3e2616"/><rect x="398" y="456" width="10" height="94" rx="3" fill="#3e2616"/>
  <rect x="262" y="240" width="14" height="210" rx="5" fill="url(#bWoodSide)"/>
  <rect x="404" y="240" width="14" height="210" rx="5" fill="url(#bWoodSide)"/>
  <circle cx="269" cy="238" r="8" fill="#8d5d38"/><circle cx="411" cy="238" r="8" fill="#8d5d38"/>
  <path d="M258 250 Q340 228 422 250 L422 268 Q340 246 258 268 Z" fill="url(#bWood)" stroke="#3a2416" stroke-width="1"/>
  <path d="M276 300 Q340 286 404 300 L404 310 Q340 296 276 310 Z" fill="#6b4429"/>
  <path d="M276 360 Q340 348 404 360 L404 368 Q340 356 276 368 Z" fill="#6b4429"/>
  <path d="M246 432 L434 432 L442 452 L238 452 Z" fill="url(#bWood)" stroke="#3a2416" stroke-width="1"/>
  <rect x="238" y="452" width="204" height="12" rx="3" fill="#5a3822"/>
  <path d="M252 420 Q340 410 428 420 L436 436 Q340 428 244 436 Z" fill="url(#bCushion)"/>
  <path d="M244 436 Q340 428 436 436" fill="none" stroke="#d8b25a" stroke-width="1.5" stroke-dasharray="2 3"/>
  <path d="M240 464 L254 464 L252 520 L256 534 L250 560 L240 560 L236 534 L242 520 Z" fill="url(#bWoodSide)"/>
  <path d="M426 464 L440 464 L438 520 L444 534 L438 560 L428 560 L424 534 L428 520 Z" fill="url(#bWoodSide)"/>
  <rect x="250" y="516" width="180" height="7" rx="3" fill="#4e3020"/>
</g>

<!-- table with an Uzbek dasturxon, choynak, plate of navvot -->
<g id="b-table" opacity="0">
  <ellipse cx="560" cy="566" rx="100" ry="9" fill="url(#bShadow)"/>
  <rect x="486" y="456" width="9" height="94" fill="#3e2616"/><rect x="626" y="456" width="9" height="94" fill="#3e2616"/>
  <path d="M470 456 L482 456 L480 520 L485 534 L479 566 L469 566 L465 534 L471 520 Z" fill="url(#bWoodSide)"/>
  <path d="M638 456 L650 456 L649 520 L655 534 L648 566 L638 566 L634 534 L640 520 Z" fill="url(#bWoodSide)"/>
  <path d="M470 434 L650 434 L664 450 L456 450 Z" fill="url(#bWood)"/>
  <rect x="456" y="450" width="208" height="10" rx="2" fill="#5a3822"/>
  <!-- dasturxon -->
  <path d="M478 432 L642 432 L652 446 L620 482 L500 482 L468 446 Z" fill="url(#bCloth)"/>
  <path d="M500 482 L620 482" stroke="#e2b13c" stroke-width="2"/>
  <g stroke="#e2b13c" stroke-width="1.2"><path d="M504 482 v6 M512 482 v6 M520 482 v6 M528 482 v6 M536 482 v6 M544 482 v6 M552 482 v6 M560 482 v6 M568 482 v6 M576 482 v6 M584 482 v6 M592 482 v6 M600 482 v6 M608 482 v6 M616 482 v6"/></g>
  <ellipse id="b-pot-shadow" cx="492" cy="437" rx="30" ry="5" fill="#000" opacity=".3"/>
  <!-- plate with navvot (crystal sugar) and nuts -->
  <g transform="translate(622 441)">
    <ellipse cx="0" cy="2" rx="24" ry="6" fill="#000" opacity=".25"/>
    <ellipse cx="0" cy="0" rx="24" ry="6.5" fill="url(#bPorcelain)" stroke="#1f4fa8" stroke-width="1.4"/>
    <path d="M-12 -2 L-6 -9 L0 -3 L6 -10 L12 -2 Z" fill="#f2c96b" opacity=".9" stroke="#c99a3a" stroke-width=".8"/>
    <ellipse cx="-14" cy="-1" rx="4" ry="2.4" fill="#8a5a2b"/><ellipse cx="14" cy="-1" rx="4" ry="2.4" fill="#7a4a22"/>
  </g>

</g>

<!-- choynak: white porcelain with the cobalt "paxta" (cotton) pattern and gold rims -->
  <g id="b-pot" opacity="0">
    
    <path d="M-26 -12 C-48 -14 -48 18 -24 16" fill="none" stroke="#9fb0c8" stroke-width="7.5" stroke-linecap="round"/>
    <path d="M-26 -12 C-48 -14 -48 18 -24 16" fill="none" stroke="#ffffff" stroke-width="5" stroke-linecap="round"/>
    <path d="M24 2 C38 -2 44 -14 50 -26 C52 -29 57 -28 56 -24 C51 -8 45 10 28 16 Z" fill="url(#bPorcelain)" stroke="#9fb0c8" stroke-width="1"/>
    <path d="M51 -25 L56 -24" stroke="#d4a93c" stroke-width="2"/>
    <path d="M-30 8 C-32 -14 -16 -24 0 -24 C16 -24 32 -14 30 8 C28 22 14 28 0 28 C-14 28 -28 22 -30 8 Z" fill="url(#bPorcelain)" stroke="#9fb0c8" stroke-width="1"/>
    <path d="M-27 -6 C-14 -14 14 -14 27 -6" fill="none" stroke="#1f4fa8" stroke-width="5"/>
    <path d="M-27 -6 C-14 -14 14 -14 27 -6" fill="none" stroke="#d4a93c" stroke-width="1" transform="translate(0 -3)"/>
    <use href="#bPaxta" x="-15" y="8"/><use href="#bPaxta" x="0" y="13"/><use href="#bPaxta" x="15" y="8"/>
    <path d="M-22 22 C-10 27 10 27 22 22" fill="none" stroke="#1f4fa8" stroke-width="2.2"/>
    <ellipse cx="0" cy="-23" rx="15" ry="4" fill="#e8eef7" stroke="#d4a93c" stroke-width="1.2"/>
    <path d="M-12 -24 C-11 -33 11 -33 12 -24 Z" fill="url(#bPorcelain)" stroke="#9fb0c8" stroke-width="1"/>
    <path d="M-9 -27 C-4 -31 4 -31 9 -27" fill="none" stroke="#1f4fa8" stroke-width="2"/>
    <circle cx="0" cy="-34" r="4" fill="#1f4fa8"/><circle cx="-1.2" cy="-35.2" r="1.2" fill="#fff" opacity=".7"/>
    <path d="M-20 -10 C-16 -16 -8 -18 -4 -18" fill="none" stroke="#fff" stroke-width="3" stroke-linecap="round" opacity=".85"/>
  </g>
  <g id="b-pot-steam" fill="none" stroke="#fff" stroke-width="1.6" stroke-linecap="round" opacity="0"><path/><path/></g>
<g id="b-fig">
  <!-- legs: thigh (foreshortened when sitting) + shin, trouser crease, knee cap, leather shoes -->
  <path id="b-shin-l" fill="url(#bPants)" stroke="#0f1628" stroke-width="1.2"/>
  <path id="b-shin-r" fill="url(#bPants)" stroke="#0f1628" stroke-width="1.2"/>
  <path id="b-crease-l" stroke="#3d5288" stroke-width="1.2" fill="none" opacity=".8"/>
  <path id="b-crease-r" stroke="#3d5288" stroke-width="1.2" fill="none" opacity=".8"/>
  <g id="b-shoe-l"><path d="M-17 -6 C-17 -12 17 -12 17 -6 L19 6 C19 11 -19 11 -19 6 Z" fill="url(#bShoe)"/>
    <path d="M-19 6 C-19 11 19 11 19 6" fill="none" stroke="#120a05" stroke-width="3"/><ellipse cx="-4" cy="-4" rx="7" ry="2" fill="#fff" opacity=".22"/></g>
  <g id="b-shoe-r"><path d="M-17 -6 C-17 -12 17 -12 17 -6 L19 6 C19 11 -19 11 -19 6 Z" fill="url(#bShoe)"/>
    <path d="M-19 6 C-19 11 19 11 19 6" fill="none" stroke="#120a05" stroke-width="3"/><ellipse cx="4" cy="-4" rx="7" ry="2" fill="#fff" opacity=".22"/></g>
  <path id="b-thigh-l" fill="url(#bThigh)" stroke="#0f1628" stroke-width="1.2"/>
  <path id="b-thigh-r" fill="url(#bThigh)" stroke="#0f1628" stroke-width="1.2"/>
  <ellipse id="b-knee-l" rx="22" ry="11" fill="#4a64a0" opacity=".55"/>
  <ellipse id="b-knee-r" rx="22" ry="11" fill="#4a64a0" opacity=".55"/>
  <!-- neck + torso -->
  <rect id="b-neck" width="36" height="34" rx="10" fill="url(#bSkin)"/>
  <path id="b-torso" fill="url(#bSuit)" stroke="#0e1730" stroke-width="1.4"/>
  <path id="b-shirt" fill="url(#bShirt)"/>
  <path id="b-collar" fill="#ffffff" stroke="#c9cfd9" stroke-width="1"/>
  <path id="b-tie" fill="url(#bTie)" stroke="#4a0f22" stroke-width=".8"/>
  <path id="b-knot" fill="#7d1f3b"/>
  <path id="b-lapel-l" fill="url(#bLapel)" stroke="#0e1730" stroke-width="1"/>
  <path id="b-lapel-r" fill="url(#bLapel)" stroke="#0e1730" stroke-width="1"/>
  <path id="b-pocket" fill="none" stroke="#0e1730" stroke-width="1.4"/>
  <path id="b-square" fill="#f4f6fa"/>
  <path id="b-hem" fill="none" stroke="#0e1730" stroke-width="1.4" opacity=".8"/>
  <circle id="b-btn1" r="3.4" fill="#0d1428"/><circle id="b-btn2" r="3.4" fill="#0d1428"/>
  <!-- left arm (viewer's left) -->
  <path id="b-upper-l" fill="url(#bSuitSide)" stroke="#0e1730" stroke-width="1.2"/>
  <circle id="b-elbow-l" r="13.4" fill="url(#bSuitSide)"/>
  <path id="b-fore-l" fill="url(#bSuitSide)" stroke="#0e1730" stroke-width="1.2"/>
  <circle id="b-cuff-l" r="9.5" fill="#f4f6fa" stroke="#c9cfd9" stroke-width="1"/>
  <g id="b-hand-l">${hand('bSkin')}</g>
</g>`;

// Drawn ABOVE the head layer: the right arm and the piala (so the cup at the lips isn't hidden behind the face).
const FRONT = `
<defs>
  <radialGradient id="fSkin" cx="40%" cy="35%" r="75%"><stop offset="0" stop-color="#f3caa6"/><stop offset="1" stop-color="#c88d66"/></radialGradient>
  <linearGradient id="fSuitSide" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#1a2a50"/><stop offset=".45" stop-color="#2c4374"/><stop offset="1" stop-color="#16223f"/></linearGradient>
  <radialGradient id="fPorcelain" cx="35%" cy="30%" r="80%"><stop offset="0" stop-color="#ffffff"/><stop offset=".7" stop-color="#e8eef7"/><stop offset="1" stop-color="#b9c6da"/></radialGradient>
  <radialGradient id="fTea" cx="40%" cy="40%" r="70%"><stop offset="0" stop-color="#c47a28"/><stop offset="1" stop-color="#6b3a10"/></radialGradient>
</defs>
<g id="b-front-fig">
  <path id="b-upper-r" fill="url(#fSuitSide)" stroke="#0e1730" stroke-width="1.2"/>
  <circle id="b-elbow-r" r="13.4" fill="url(#fSuitSide)"/>
  <path id="b-fore-r" fill="url(#fSuitSide)" stroke="#0e1730" stroke-width="1.2"/>
  <circle id="b-cuff-r" r="9.5" fill="#f4f6fa" stroke="#c9cfd9" stroke-width="1"/>
  <g id="b-hand-r">${hand('fSkin')}</g>
</g>
<path id="b-stream" fill="none" stroke="#b5671d" stroke-width="3.2" stroke-linecap="round" stroke-dasharray="7 3" opacity="0"/>
<!-- piala: porcelain, cobalt paxta band, gold rim, amber tea with a highlight -->
<g id="b-piala">
  <ellipse cx="0" cy="11" rx="12" ry="2.6" fill="#000" opacity=".25"/>
  <path d="M-17 -9 L17 -9 C16 4 9 10 0 10 C-9 10 -16 4 -17 -9 Z" fill="url(#fPorcelain)" stroke="#8fa3c2" stroke-width="1"/>
  <path d="M-15.5 -3 C-8 0 8 0 15.5 -3" fill="none" stroke="#1f4fa8" stroke-width="3.2"/>
  <circle cx="-7" cy="3" r="1.8" fill="#1f4fa8"/><circle cx="0" cy="4.5" r="1.8" fill="#1f4fa8"/><circle cx="7" cy="3" r="1.8" fill="#1f4fa8"/>
  <rect x="-6" y="9" width="12" height="2.5" rx="1" fill="#d8e0ec" stroke="#8fa3c2" stroke-width=".6"/>
  <ellipse cx="0" cy="-9" rx="17" ry="3.6" fill="#f4f7fb" stroke="#d4a93c" stroke-width="1.2"/>
  <ellipse cx="0" cy="-8.6" rx="14.5" ry="2.6" fill="#f4f7fb"/>
  <ellipse id="b-tea" cx="0" cy="-8.6" rx="14.5" ry="2.6" fill="url(#fTea)"/>
  <ellipse id="b-tea-shine" cx="-5" cy="-9.2" rx="4" ry=".9" fill="#ffd9a0" opacity=".7"/>
  <g id="b-steam" fill="none" stroke="#ffffff" stroke-linecap="round" opacity="0"><path stroke-width="2"/><path stroke-width="1.6"/><path stroke-width="1.3"/></g>
</g>`;

/**
 * A hand in local space: wrist at (0,0), fingers pointing to +y, thumb on +x (mirrored for the other side).
 * Two finger sets: .open (resting / reaching) and .grip (curled around the piala) — toggled per frame.
 */
function hand(skin) {
  const f = (d, w) => `<path d="${d}" stroke="#a9704c" stroke-width="${w + 1.4}" fill="none" stroke-linecap="round"/>`
                    + `<path d="${d}" stroke="url(#${skin})" stroke-width="${w}" fill="none" stroke-linecap="round"/>`;
  return `
    <path d="M-10 -1 C-12.5 8 -12 16 -10 23 L9.5 23 C12 16 12.5 8 10 -1 Z" fill="url(#${skin})" stroke="#a9704c" stroke-width="1.2"/>
    <g class="open">
      ${f('M6.8 21 Q8 30 7.6 38', 5.2)}${f('M2.2 22 Q2.6 32 2.2 41', 5.4)}${f('M-2.6 22 Q-3 31 -3.2 39', 5.2)}${f('M-7.2 20 Q-8.4 27 -8.8 34', 4.6)}
      <path d="M7.6 37.5 l-.2 -2.6 M2.2 40.5 l0 -2.6 M-3.2 38.5 l0 -2.6 M-8.8 33.5 l0 -2.4" stroke="#f7dcc6" stroke-width="2.2" stroke-linecap="round"/>
    </g>
    <g class="grip" opacity="0">
      ${f('M6.8 21 Q10 28 5 31', 5.2)}${f('M2.2 22 Q5 30 0 33', 5.4)}${f('M-2.6 22 Q0 29 -4.6 31', 5.2)}${f('M-7.2 20 Q-5.6 26 -9 27', 4.6)}
    </g>
    ${f('M9.5 5 Q17 13 15 24', 5.6)}
    <path d="M-8.5 19.5 Q0 22.5 8.5 19.5" fill="none" stroke="#a9704c" stroke-width="1" opacity=".5"/>`;
}

/** Filled limb segment P→Q (widths w1 at P, w2 at Q) with rounded ends. */
function limb(P, Q, w1, w2) {
  const dx = Q.x - P.x, dy = Q.y - P.y, L = Math.hypot(dx, dy) || 1;
  const nx = -dy / L, ny = dx / L, a = w1 / 2, b = w2 / 2;
  const f = (v) => v.toFixed(1);
  return `M${f(P.x + nx * a)} ${f(P.y + ny * a)} L${f(Q.x + nx * b)} ${f(Q.y + ny * b)} `
    + `A${f(b)} ${f(b)} 0 0 1 ${f(Q.x - nx * b)} ${f(Q.y - ny * b)} L${f(P.x - nx * a)} ${f(P.y - ny * a)} `
    + `A${f(a)} ${f(a)} 0 0 1 ${f(P.x + nx * a)} ${f(P.y + ny * a)} Z`;
}

/** 2-bone IK: shoulder S → target T, bone lengths a, b. bend = +1/-1 picks the elbow side. Returns [elbow, wrist]. */
function ik(S, T, a, b, bend) {
  let dx = T.x - S.x, dy = T.y - S.y;
  let d = Math.hypot(dx, dy);
  const max = a + b - 0.01, min = Math.abs(a - b) + 0.01;
  if (d > max) { dx *= max / d; dy *= max / d; d = max; }
  if (d < min) { dx *= min / d; dy *= min / d; d = min; }
  const base = Math.atan2(dy, dx);
  const cosA = clamp((a * a + d * d - b * b) / (2 * a * d), -1, 1);
  const ang = base + bend * Math.acos(cosA);
  const E = { x: S.x + Math.cos(ang) * a, y: S.y + Math.sin(ang) * a };
  return [E, { x: S.x + dx, y: S.y + dy }];
}

/** setAttribute only when the value really changed (the rig writes ~40 attributes per frame). */
const SET = (el, k, v) => { const c = el.__a || (el.__a = {}); v = String(v); if (c[k] !== v) { c[k] = v; el.setAttribute(k, v); } };

export class BodyDirector {
  #card; #wrap; #avatar; #player; #svg; #front; #el = {};
  #mix = 0; #mixTarget = 0;                     // 0 = close-up, 1 = wide shot
  #pose = { ...POSTURE.stand, turn: 0, pitch: 0, lean: 0 };
  #target = { ...POSTURE.stand, turn: 0, pitch: 0, lean: 0 };
  #hands = { l: null, r: null };               // IK targets (scene coords) or null = rest pose
  #handPos = { l: null, r: null };             // smoothed targets
  #cup = { inHand: false, tilt: 0, level: 0, heat: 0 };          // level 0..1 (tea in the piala), heat 0..1 (steam)
  #pot = { x: 492, y: 406, a: 0, tx: 492, ty: 406, ta: 0, amount: 1, held: false };
  #pouring = false; #lastT = 0;
  #closeRect = null;
  #lookTimer = 0;
  posture = 'stand';

  constructor({ card, avatarWrap, avatar, player }) {
    this.#card = card; this.#wrap = avatarWrap; this.#avatar = avatar; this.#player = player;
    this.#svg = document.createElementNS(SVGNS, 'svg');
    SET(this.#svg, 'viewBox', `0 0 ${VB_W} ${VB_H}`);
    SET(this.#svg, 'preserveAspectRatio', 'xMidYMax slice');
    this.#svg.classList.add('body-scene');
    SET(this.#svg, 'aria-hidden', 'true');
    this.#svg.innerHTML = ROOM;
    card.insertBefore(this.#svg, avatarWrap);
    this.#front = this.#svg.cloneNode(false);
    this.#front.classList.add('body-front');
    this.#front.innerHTML = FRONT;
    avatarWrap.after(this.#front);
    for (const id of ['kurpacha', 'chair', 'table', 'fig', 'thigh-l', 'thigh-r', 'shin-l', 'shin-r', 'crease-l', 'crease-r',
                      'knee-l', 'knee-r', 'shoe-l', 'shoe-r', 'neck', 'torso', 'shirt', 'collar', 'tie', 'knot', 'lapel-l', 'lapel-r',
                      'pocket', 'square', 'hem', 'btn1', 'btn2', 'upper-l', 'fore-l', 'elbow-l', 'cuff-l', 'hand-l',
                      'upper-r', 'fore-r', 'elbow-r', 'cuff-r', 'hand-r', 'piala', 'steam', 'front-fig', 'pot', 'pot-steam', 'pot-shadow',
                      'stream', 'tea', 'tea-shine'])
      this.#el[id] = this.#svg.querySelector('#b-' + id) ?? this.#front.querySelector('#b-' + id);
    requestAnimationFrame(this.#tick);
  }

  get wide() { return this.#mixTarget === 1; }

  /** Perform a body action. Resolves when it is complete enough for the next speech segment to play. */
  async perform(action) {
    clearTimeout(this.#lookTimer);
    switch (action) {
      case 'look_right': case 'look_left': case 'look_up': case 'look_down': {
        const turn = action === 'look_right' ? 1 : action === 'look_left' ? -1 : 0;
        const pitch = action === 'look_up' ? -1 : action === 'look_down' ? 1 : 0;
        this.#look(turn, pitch);
        this.#lookTimer = setTimeout(() => this.#look(0, 0), 5500);   // searches for a while, then back to you
        await sleep(450);
        return;
      }
      case 'face_front': this.#look(0, 0); await sleep(350); return;
      case 'sit': await this.#setPosture('sit'); return;
      case 'lie_down': await this.#setPosture('lie'); this.#avatar.setRest?.(true); return;
      case 'stand':
        this.#avatar.setRest?.(false);
        if (this.posture === 'stand') return;
        await this.#setPosture('stand');
        await sleep(900);
        this.#mixTarget = 0;                 // back to the close-up for talking
        await sleep(700);
        return;
      case 'drink_tea': await this.#drinkTea(); return;
      case 'show_creator': this.#showCreator(); await sleep(300); return;
      default: return;
    }
  }

  /** "Seni kim yaratgan?" → the creator card with his photo; Teacher glances at it, then back to you. */
  #showCreator() {
    let card = this.#card.querySelector('.creator-card');
    if (!card) {
      card = document.createElement('figure');
      card.className = 'creator-card';
      card.innerHTML = `
        <div class="creator-photo" aria-hidden="true"><img src="img/creator.jpg" alt="" onerror="this.remove()"><span>DB</span></div>
        <figcaption>
          <small>Teacher AI yaratuvchisi · Создатель · Creator</small>
          <b>Doston Bunyodov Bunyodovich</b>
          <span>C# · .NET · ASP.NET Core</span>
        </figcaption>
        <button type="button" aria-label="Yopish">&times;</button>`;
      card.querySelector('button').onclick = () => card.classList.remove('show');
      this.#card.append(card);
    }
    card.classList.remove('show'); void card.offsetWidth; card.classList.add('show');
    clearTimeout(this._creatorT);
    this._creatorT = setTimeout(() => card.classList.remove('show'), 12000);
    this.#look(-0.6, 0);                                    // a proud glance toward the card
    clearTimeout(this.#lookTimer);
    this.#lookTimer = setTimeout(() => this.#look(0, 0), 2200);
  }

  #look(turn, pitch) {
    this.#target.turn = turn; this.#target.pitch = pitch;
    this.#avatar.setBodyLook(turn, pitch);
  }

  async #setPosture(p) {
    this.#avatar.setRest?.(false);
    this.posture = p;
    if (p !== 'stand' || this.#mixTarget === 1) this.#enterWide();
    Object.assign(this.#target, POSTURE[p]);
    this.#hands = { l: null, r: null };
    await sleep(1100);
  }

  #enterWide() {
    if (this.#mixTarget === 1) return;
    const c = this.#card.getBoundingClientRect(), w = this.#wrap.getBoundingClientRect();
    this.#closeRect = { x: w.left - c.left, y: w.top - c.top, w: w.width, h: w.height };
    this.#mixTarget = 1;
    this.#card.classList.add('wide');
  }

  /** Pour from the choynak until the piala is full, put the pot back, then take the piala and sip. */
  async #drinkTea() {
    if (this.posture !== 'sit') await this.#setPosture('sit');
    else this.#enterWide();
    const pot = this.#pot, cup = this.#cup;
    if (cup.level < 0.3) {
      if (pot.amount < 0.15) pot.amount = 1;                         // (a fresh pot was brewed meanwhile)
      // reach for the handle, lift the pot over the piala and tilt it — the emptier, the steeper
      this.#hands.r = 'pot'; this.#target.lean = 0.4; await sleep(800);
      pot.held = true; pot.tx = 494; pot.ty = 384; pot.ta = 8; await sleep(500);
      pot.ta = 30 + (1 - pot.amount) * 22; await sleep(450);
      this.#pouring = true;                                          // the stream runs, the piala fills …
      const t0 = performance.now();
      while (cup.level < 0.9 && performance.now() - t0 < 4000) await sleep(60);
      this.#pouring = false;                                         // … full: stop
      pot.ta = 8; await sleep(350);
      pot.amount = Math.max(0, pot.amount - 0.22);
      pot.tx = 492; pot.ty = 406; pot.ta = 0; await sleep(650);      // put it back on the table
      pot.held = false;
      this.#hands.r = null; this.#target.lean = 0; await sleep(350);
    }
    // take the piala (lean toward the table), lift it to the lips, sip, put it back
    const onTable = { x: 556, y: 432 };
    this.#target.lean = 1;
    this.#hands.r = { ...onTable }; await sleep(800);
    cup.inHand = true;
    this.#target.lean = 0.2;
    this.#hands.r = 'mouth'; await sleep(900);
    cup.tilt = 1;
    this.#avatar.setSip?.(true);
    this.#player.fx?.('slurp', 1);
    const sip0 = cup.level;
    for (let i = 0; i < 12; i++) { cup.level = Math.max(0, sip0 - 0.28 * (i + 1) / 12); await sleep(95); }   // the tea goes down
    this.#avatar.setSip?.(false);
    cup.tilt = 0;
    this.#player.fx?.('sigh', 1);
    await sleep(350);
    this.#target.lean = 1;
    this.#hands.r = { ...onTable }; await sleep(750);
    cup.inHand = false;
    this.#hands.r = null; this.#target.lean = 0; await sleep(300);
  }

  // ------------------------------------------------------------------ frame
  #tick = () => {
    const p = this.#pose, t = this.#target;
    for (const k of ['hipX', 'hipY', 'lie', 'thighFs', 'sit', 'turn', 'pitch', 'lean']) p[k] = lerp(p[k], t[k], k === 'lie' ? 0.06 : 0.08);
    this.#mix = lerp(this.#mix, this.#mixTarget, 0.07);
    if (Math.abs(this.#mix - this.#mixTarget) < 0.002) this.#mix = this.#mixTarget;

    this.#svg.style.opacity = this.#mix.toFixed(3);
    this.#front.style.opacity = this.#mix.toFixed(3);
    if (this.#mix > 0.001) this.#draw(performance.now() / 1000);
    else if (this.#card.classList.contains('wide')) { this.#card.classList.remove('wide'); this.#wrap.removeAttribute('style'); }
    requestAnimationFrame(this.#tick);
  };

  #draw(time) {
    const p = this.#pose, el = this.#el, f1 = (v) => v.toFixed(1);
    const breathe = Math.sin(time * 1.6) * 1.6;
    const hip = { x: p.hipX, y: p.hipY };
    const turnShift = p.turn * 30 + p.lean * 26;                                  // whole torso turns / leans toward the table
    const N = { x: hip.x + turnShift, y: hip.y - TORSO + breathe * 0.4 + p.lean * 6 + Math.max(0, p.pitch) * 22 };   // collar point (bows to look down)
    const sw = 70 * (1 - 0.32 * Math.abs(p.turn));
    const SL = { x: N.x - sw, y: N.y + 22 }, SR = { x: N.x + sw, y: N.y + 22 };
    const HL = { x: hip.x - 30, y: hip.y }, HR = { x: hip.x + 30, y: hip.y };

    // ---- legs. Sitting, seen from the front: the thigh comes toward the camera (short, wide, lit from above),
    // the shin drops straight to the floor, feet slightly apart. Standing: one long straight leg.
    const leg = (H, side) => {
      const spread = p.sit * 12 * side;
      const bend = p.lie * (side < 0 ? 26 : 12);
      const K = { x: H.x + spread + bend, y: H.y + THIGH * p.thighFs - bend * 0.3 };
      const A = { x: K.x + spread * 0.25 - bend * 0.6, y: K.y + SHIN };
      return [K, A];
    };
    const [KL, AL] = leg(HL, -1), [KR, AR] = leg(HR, 1);
    for (const [k, H, K, A] of [['l', HL, KL, AL], ['r', HR, KR, AR]]) {
      SET(el['thigh-' + k], 'd', limb(H, K, 46, 44));
      SET(el['shin-' + k], 'd', limb(K, A, 34, 26));
      SET(el['crease-' + k], 'd', `M${f1(K.x)} ${f1(K.y + 14)} L${f1(A.x)} ${f1(A.y - 8)}`);
      SET(el['knee-' + k], 'cx', f1(K.x)); SET(el['knee-' + k], 'cy', f1(K.y - 2));
      SET(el['knee-' + k], 'opacity', (0.55 * p.sit).toFixed(2));
      SET(el['shoe-' + k], 'transform', `translate(${f1(A.x)} ${f1(A.y + 10)})`);
    }

    // ---- torso: navy two-button jacket, white shirt, burgundy tie, notch lapels, pocket square
    SET(el.neck, 'x', f1(N.x - 18)); SET(el.neck, 'y', f1(N.y - 22));
    const hemY = hip.y + 18 - 16 * p.sit;                      // sitting: the jacket rides up onto the lap
    SET(el.torso, 'd', `M${f1(SL.x - 8)} ${f1(SL.y - 2)} Q${f1(N.x)} ${f1(N.y + 2)} ${f1(SR.x + 8)} ${f1(SR.y - 2)} `
      + `Q${f1(SR.x + 16)} ${f1(SR.y + 44)} ${f1(HR.x + 22)} ${f1(hemY)} L${f1(N.x + 4)} ${f1(hemY + 6)} L${f1(N.x - 4)} ${f1(hemY + 6)} `
      + `L${f1(HL.x - 22)} ${f1(hemY)} Q${f1(SL.x - 16)} ${f1(SL.y + 44)} ${f1(SL.x - 8)} ${f1(SL.y - 2)} Z`);
    SET(el.shirt, 'd', `M${f1(N.x - 22)} ${f1(N.y + 2)} L${f1(N.x)} ${f1(N.y + 96)} L${f1(N.x + 22)} ${f1(N.y + 2)} Z`);
    SET(el.collar, 'd', `M${f1(N.x - 18)} ${f1(N.y - 4)} L${f1(N.x)} ${f1(N.y + 14)} L${f1(N.x - 8)} ${f1(N.y + 26)} L${f1(N.x - 24)} ${f1(N.y + 6)} Z `
      + `M${f1(N.x + 18)} ${f1(N.y - 4)} L${f1(N.x)} ${f1(N.y + 14)} L${f1(N.x + 8)} ${f1(N.y + 26)} L${f1(N.x + 24)} ${f1(N.y + 6)} Z`);
    SET(el.knot, 'd', `M${f1(N.x - 6)} ${f1(N.y + 12)} L${f1(N.x + 6)} ${f1(N.y + 12)} L${f1(N.x + 4)} ${f1(N.y + 24)} L${f1(N.x - 4)} ${f1(N.y + 24)} Z`);
    SET(el.tie, 'd', `M${f1(N.x - 4)} ${f1(N.y + 24)} L${f1(N.x + 4)} ${f1(N.y + 24)} L${f1(N.x + 9)} ${f1(N.y + 84)} L${f1(N.x)} ${f1(N.y + 96)} L${f1(N.x - 9)} ${f1(N.y + 84)} Z`);
    // notch lapels (each side: collar → notch → roll line down to the top button)
    const lap = (d) => `M${f1(N.x + d * 22)} ${f1(N.y + 2)} L${f1(N.x + d * 38)} ${f1(N.y + 26)} L${f1(N.x + d * 30)} ${f1(N.y + 32)} `
      + `L${f1(N.x + d * 40)} ${f1(N.y + 42)} L${f1(N.x + d * 4)} ${f1(N.y + 108)} L${f1(N.x + d * 2)} ${f1(N.y + 100)} Z`;
    SET(el['lapel-l'], 'd', lap(-1)); SET(el['lapel-r'], 'd', lap(1));
    SET(el.pocket, 'd', `M${f1(N.x + 34)} ${f1(N.y + 58)} L${f1(N.x + 60)} ${f1(N.y + 56)}`);
    SET(el.square, 'd', `M${f1(N.x + 38)} ${f1(N.y + 58)} L${f1(N.x + 42)} ${f1(N.y + 50)} L${f1(N.x + 47)} ${f1(N.y + 56)} L${f1(N.x + 52)} ${f1(N.y + 49)} L${f1(N.x + 56)} ${f1(N.y + 57)} Z`);
    SET(el.hem, 'd', `M${f1(N.x + 4)} ${f1(N.y + 108)} L${f1(N.x + 6)} ${f1(hemY + 4)} M${f1(HL.x - 18)} ${f1(hip.y - 6)} L${f1(HL.x + 10)} ${f1(hip.y - 8)} M${f1(HR.x + 18)} ${f1(hip.y - 6)} L${f1(HR.x - 10)} ${f1(hip.y - 8)}`);
    SET(el.btn1, 'cx', f1(N.x + 3)); SET(el.btn1, 'cy', f1(N.y + 112));
    SET(el.btn2, 'cx', f1(N.x + 4)); SET(el.btn2, 'cy', f1(N.y + 134));

    // ---- arms: IK to a target, or a rest pose (sitting: palms on the knees; standing: along the body)
    const mouth = { x: N.x + 6 + p.turn * 6, y: N.y + 14 };                       // the piala rim lands on the lips
    const rest = (S, K, side) => p.sit > 0.5 ? { x: K.x + side * 4, y: K.y - 26 }
      : p.lie > 0.5 && side > 0 ? { x: hip.x + 26, y: hip.y - 6 }
      : p.lie > 0.5 ? { x: N.x - 30, y: N.y + 40 }
      : { x: S.x + side * 10, y: S.y + 168 };
    const handTarget = (key, S, K, side) => {
      const want = this.#hands[key] === 'mouth' ? mouth : this.#hands[key] === 'pot' ? this.#potHandle()
        : this.#hands[key] ?? rest(S, K, side);
      const cur = this.#handPos[key] ?? want;
      const k2 = this.#hands[key] === 'pot' && this.#pot.held ? 0.5 : 0.12;     // holding: glued to the handle
      const next = { x: lerp(cur.x, want.x, k2), y: lerp(cur.y, want.y, k2) };
      this.#handPos[key] = next;
      return next;
    };
    const [EL, WL] = ik(SL, handTarget('l', SL, KL, -1), UPPER, FORE, 1);
    const [ER, WR] = ik(SR, handTarget('r', SR, KR, 1), UPPER, FORE, -1);
    let handR = null;
    for (const [k, S, E, W, side] of [['l', SL, EL, WL, -1], ['r', SR, ER, WR, 1]]) {
      SET(el['upper-' + k], 'd', limb(S, E, 32, 27));
      SET(el['fore-' + k], 'd', limb(E, W, 27, 22));
      SET(el['elbow-' + k], 'cx', f1(E.x)); SET(el['elbow-' + k], 'cy', f1(E.y));
      const ang = Math.atan2(W.y - E.y, W.x - E.x);
      SET(el['cuff-' + k], 'cx', f1(W.x - Math.cos(ang) * 2)); SET(el['cuff-' + k], 'cy', f1(W.y - Math.sin(ang) * 2));
      // the hand: aligned with the forearm, thumb toward the body midline (left hand mirrored)
      const deg = ang * 180 / Math.PI - 90, mirror = side < 0 ? 1 : -1;
      SET(el['hand-' + k], 'transform', `translate(${f1(W.x)} ${f1(W.y)}) rotate(${deg.toFixed(1)}) scale(${mirror} 1)`);
      if (k === 'r') handR = { W, ang, mirror };
    }
    const gripping = this.#cup.inHand;
    const hr = el['hand-r'];
    if (hr.__grip !== gripping) {
      hr.__grip = gripping;
      hr.querySelector('.open').setAttribute('opacity', gripping ? '0' : '1');
      hr.querySelector('.grip').setAttribute('opacity', gripping ? '1' : '0');
    }

    // whole figure rotates to lie down (pivot at the hip)
    const lieDeg = -90 * p.lie;
    SET(el.fig, 'transform', `rotate(${lieDeg.toFixed(2)} ${hip.x} ${hip.y})`);
    SET(el['front-fig'], 'transform', `rotate(${lieDeg.toFixed(2)} ${hip.x} ${hip.y})`);
    SET(el.chair, 'opacity', clamp(p.sit, 0, 1).toFixed(2));
    SET(el.table, 'opacity', clamp(p.sit, 0, 1).toFixed(2));
    SET(el.kurpacha, 'opacity', clamp(p.lie, 0, 1).toFixed(2));

    // ---- choynak: follows its target (lifted / tilted while pouring)
    const dt = Math.min(0.05, Math.max(0, time - (this.#lastT || time))); this.#lastT = time;
    const pot = this.#pot, cup = this.#cup;
    pot.x = lerp(pot.x, pot.tx, 0.1); pot.y = lerp(pot.y, pot.ty, 0.1); pot.a = lerp(pot.a, pot.ta, 0.1);
    SET(el.pot, 'transform', `translate(${f1(pot.x)} ${f1(pot.y)}) rotate(${pot.a.toFixed(1)})`);
    SET(el.pot, 'opacity', clamp(p.sit * 1.2, 0, 1).toFixed(2));
    SET(el['pot-shadow'], 'opacity', (0.3 * clamp(1 - (406 - pot.y) / 20, 0, 1) * p.sit).toFixed(2));
    // hot pot: a thin steam from the spout, weaker as it empties
    const spout = this.#potPoint(56, -27);
    const ps = el['pot-steam'].children;
    for (let i = 0; i < 2; i++) {
      const ph = time * 1.4 + i * 1.7, h = 16 + i * 6;
      SET(ps[i], 'd', `M${f1(spout.x)} ${f1(spout.y)} q${f1(Math.sin(ph) * 5)} ${f1(-h / 2)} ${f1(Math.sin(ph + 1) * 3)} ${f1(-h)}`);
    }
    SET(el['pot-steam'], 'opacity', (0.35 * pot.amount * p.sit * (this.#pouring ? 0.4 : 1)).toFixed(2));

    // ---- piala: on the table, or held between the fingers and thumb (tilted while sipping)
    let cupAt = { x: 556, y: 432 }, cupRot = '';
    if (this.#cup.inHand && handR) {
      const { W, ang, mirror } = handR, lx = mirror * 4, ly = 30;              // a point inside the curled fingers
      const r = ang - Math.PI / 2;
      cupAt = { x: W.x + lx * Math.cos(r) - ly * Math.sin(r), y: W.y + lx * Math.sin(r) + ly * Math.cos(r) - 6 };
      cupRot = `rotate(${(-32 * this.#cup.tilt + lieDeg).toFixed(1)})`;
    }
    SET(el.piala, 'transform', `translate(${f1(cupAt.x)} ${f1(cupAt.y)}) ${cupRot}`);
    SET(el.piala, 'opacity', clamp(p.sit * 1.2, 0, 1).toFixed(2));

    // ---- pouring: an amber stream from the spout into the piala; the tea rises
    if (this.#pouring) {
      cup.level = Math.min(0.92, cup.level + dt * 0.42 * (0.6 + 0.4 * pot.amount));
      cup.heat = 1;
      const surf = { x: cupAt.x, y: cupAt.y - 9 + (1 - cup.level) * 13 };
      SET(el.stream, 'd', `M${f1(spout.x)} ${f1(spout.y)} Q${f1(spout.x + 4)} ${f1((spout.y + surf.y) / 2)} ${f1(surf.x + 2)} ${f1(surf.y)}`);
      SET(el.stream, 'stroke-dashoffset', f1(-time * 90));
      SET(el.stream, 'stroke-width', (1.8 + 1.6 * pot.amount).toFixed(1));
      SET(el.stream, 'opacity', '0.95');
    } else SET(el.stream, 'opacity', '0');
    // tea surface: lower and smaller as the level drops (the piala narrows to its foot)
    const lv = clamp(cup.level, 0, 1);
    SET(el.tea, 'cy', f1(-8.6 + (1 - lv) * 13)); SET(el.tea, 'rx', f1(14.5 * (0.5 + 0.5 * lv))); SET(el.tea, 'ry', f1(2.6 * (0.6 + 0.4 * lv)));
    SET(el.tea, 'opacity', lv > 0.03 ? '1' : '0');
    SET(el['tea-shine'], 'cy', f1(-9.2 + (1 - lv) * 13)); SET(el['tea-shine'], 'opacity', lv > 0.1 ? '0.7' : '0');
    // steam: three wisps that rise, curl and fade; less as the tea cools down or is drunk
    cup.heat = Math.max(0, cup.heat - dt / 90);                               // cools in ~1.5 min
    const st = el.steam.children;
    for (let i = 0; i < 3; i++) {
      const ph = time * (1.1 + i * 0.25) + i * 2.1, rise = ((time * 0.35 + i / 3) % 1);
      const x0 = (i - 1) * 6, y0 = -12 + (1 - lv) * 10 - rise * 6, h = 18 + 10 * Math.sin(ph * 0.5);
      SET(st[i], 'd', `M${f1(x0)} ${f1(y0)} q${f1(Math.sin(ph) * 6)} ${f1(-h / 2)} ${f1(Math.sin(ph + 1.3) * 3)} ${f1(-h)} q${f1(Math.sin(ph + 2) * -5)} ${f1(-h / 2)} 0 ${f1(-h * 0.8)}`);
      st[i].setAttribute('opacity', (0.9 * (1 - rise)).toFixed(2));
    }
    SET(el.steam, 'opacity', (0.55 * cup.heat * Math.min(1, lv * 3)).toFixed(2));

    // mount the head: avatar's collar anchor → puppet's collar point (rotated for lying)
    const card = this.#card.getBoundingClientRect();                            // one layout read per frame
    const rot = lieDeg;
    const neckPt = this.#toPx(this.#rotate(N, hip, rot), card);
    const scale = this.#scale(card);
    const w = HEAD_W * scale, h = w * 460 / 400;
    const target = { x: neckPt.x - HEAD_ANCHOR.x * w, y: neckPt.y - HEAD_ANCHOR.y * h, w, h };
    const from = this.#closeRect ?? target;
    const k = this.#mix;
    const x = lerp(from.x, target.x, k), y = lerp(from.y, target.y, k), ww = lerp(from.w, target.w, k), hh = lerp(from.h, target.h, k);
    const s = this.#wrap.style;
    s.left = `${x.toFixed(1)}px`; s.top = `${y.toFixed(1)}px`; s.width = `${ww.toFixed(1)}px`; s.height = `${hh.toFixed(1)}px`; s.bottom = 'auto';
    s.transformOrigin = `${HEAD_ANCHOR.x * 100}% ${HEAD_ANCHOR.y * 100}%`;
    s.transform = `rotate(${(rot * k).toFixed(2)}deg)`;
  }

  /** A point of the teapot (local coords) → scene coords, using its current position and tilt. */
  #potPoint(lx, ly) {
    const p = this.#pot, r = p.a * Math.PI / 180;
    return { x: p.x + lx * Math.cos(r) - ly * Math.sin(r), y: p.y + lx * Math.sin(r) + ly * Math.cos(r) };
  }
  /** Where the hand grips the handle (the loop on the pot's left side). */
  #potHandle() { return this.#potPoint(-40, 2); }

  #rotate(P, C, deg) {
    const r = deg * Math.PI / 180, c = Math.cos(r), sn = Math.sin(r);
    const dx = P.x - C.x, dy = P.y - C.y;
    return { x: C.x + dx * c - dy * sn, y: C.y + dx * sn + dy * c };
  }

  /** scene units → px inside the card (preserveAspectRatio xMidYMax slice) */
  #scale(r = this.#card.getBoundingClientRect()) {
    return Math.max(r.width / VB_W, r.height / VB_H);
  }
  #toPx(P, r = this.#card.getBoundingClientRect()) {
    const s = this.#scale(r);
    return { x: (r.width - VB_W * s) / 2 + P.x * s, y: (r.height - VB_H * s) + P.y * s };
  }
}
