// Realistic hair, painted ONCE into a bitmap (canvas → PNG) and placed in the SVG head as an <image>.
// Thousands of fine, semi-transparent strands with dark roots, a glossy sheen band, baby hairs on the hairline and a
// soft contact shadow on the forehead — far more lifelike than vector shapes, and cheap to animate (just a bitmap).
//
// Style: a modern, combed side part (part on his right = viewer's left), volume lifted at the front, short sides.

/** Outline of the hair volume in the avatar's 400×460 viewBox (also used as the clip). */
const HAIR_VOLUME = 'M124 176 C116 132 128 94 158 76 C184 60 228 56 258 70 C282 80 293 104 290 138 C288 154 284 166 279 176 '
  + 'C279 164 275 152 266 144 C258 138 250 141 240 140 C228 139 218 134 206 134 C194 134 184 139 172 139 C160 139 150 138 142 145 C134 152 128 164 124 176 Z';

/** Where the painted bitmap sits (viewBox units). A little margin around the volume for baby hairs + shadow. */
const HAIR_BOX = { x: 106, y: 46, w: 200, h: 150 };

const SCALE = 4;                                     // bitmap px per viewBox unit (crisp on HiDPI)

export function paintHair() {
  const { x: X0, y: Y0, w: W, h: H } = HAIR_BOX;
  const c = document.createElement('canvas');
  c.width = W * SCALE; c.height = H * SCALE;
  const g = c.getContext('2d');
  g.scale(SCALE, SCALE);
  g.translate(-X0, -Y0);
  g.lineCap = 'round';
  const vol = new Path2D(HAIR_VOLUME);
  let seed = 20261119;
  const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
  const R = (a, b) => a + (b - a) * rnd();

  // 1) contact shadow on the forehead, just under the hairline
  g.save();
  g.filter = 'blur(2.5px)';
  g.fillStyle = 'rgba(70, 35, 18, 0.38)';
  g.translate(0, 4.5);
  g.fill(vol);
  g.restore();

  g.save();
  g.clip(vol);
  // 2) base colour: lighter on the crown where the light falls, darker at the edges and the back
  const base = g.createRadialGradient(214, 92, 6, 205, 118, 105);
  base.addColorStop(0, '#4b3526'); base.addColorStop(0.45, '#2b1d14'); base.addColorStop(1, '#120b07');
  g.fillStyle = base;
  g.fillRect(X0, Y0, W, H);

  // the flow: every strand grows from the part line / crown and is combed over to the right temple
  const flow = (u) => {
    let root;
    if (u < 0.33) root = [162 + R(-2.5, 2.5), 134 - (u / 0.33) * 46 + R(-2, 2)];               // along the part
    else { const k = (u - 0.33) / 0.67; root = [164 + k * 76 + R(-5, 5), 84 - 12 * Math.sin(Math.PI * k) + R(-4, 4)]; }
    const tip = [Math.min(296, root[0] + R(44, 80) * (1 - 0.3 * u)), root[1] + 30 + 36 * u + R(-6, 9)];
    const c1 = [root[0] + R(4, 12), root[1] - R(9, 18)];                                      // lift at the root = volume
    const c2 = [tip[0] - R(18, 30), tip[1] - R(14, 26)];
    return [root, c1, c2, tip];
  };
  const left = () => {                                                                        // short side left of the part
    const root = [161 + R(-2, 2), R(96, 134)];
    const tip = [R(122, 140), root[1] + R(26, 46)];
    return [root, [root[0] - R(6, 12), root[1] - R(4, 10)], [tip[0] + R(6, 14), tip[1] - R(14, 22)], tip];
  };
  const right = () => {                                                                       // over the right side to the temple
    const root = [R(214, 262), R(68, 92)];
    const tip = [Math.min(292, root[0] + R(22, 40)), R(140, 176)];
    return [root, [root[0] + R(10, 20), root[1] - R(2, 8)], [tip[0] + R(2, 8), tip[1] - R(30, 46)], tip];
  };
  const curve = (p, o = [0, 0], k = 1) => {
    g.beginPath();
    g.moveTo(p[0][0] + o[0], p[0][1] + o[1]);
    g.bezierCurveTo(p[1][0] + o[0], p[1][1] + o[1], p[2][0] + o[0] * k, p[2][1] + o[1] * k, p[3][0] + o[0] * k, p[3][1] + o[1] * k);
  };
  const at = (p, t) => {                                                                      // point on the bezier
    const u = 1 - t;
    return [0, 1].map((i) => u * u * u * p[0][i] + 3 * u * u * t * p[1][i] + 3 * u * t * t * p[2][i] + t * t * t * p[3][i]);
  };

  // 3) strands, in clumps (real hair groups into locks), dark → mid browns
  const browns = ['#1b120c', '#24180f', '#2e2016', '#3a291d', '#45311f', '#523a27'];
  const strands = [];
  for (let i = 0; i < 420; i++) {
    const r = rnd();
    const p = r < 0.16 ? left() : r < 0.42 ? right() : flow(rnd());
    const n = 8 + Math.floor(rnd() * 8);
    for (let j = 0; j < n; j++) {
      const o = [R(-2.6, 2.6), R(-1.8, 1.8)];
      curve(p, o, 0.4);
      g.lineWidth = R(0.22, 0.6);
      g.strokeStyle = browns[Math.floor(rnd() * browns.length)];
      g.globalAlpha = R(0.35, 0.85);
      g.stroke();
      strands.push([p, o]);
    }
  }
  // 4) dark roots near the part and the crown (depth)
  g.globalAlpha = 0.5;
  for (const [p, o] of strands) {
    if (rnd() < 0.5 || p[0][0] > 172) continue;                                           // only along the part line
    const a = at(p, 0), b = at(p, 0.1);
    g.beginPath(); g.moveTo(a[0] + o[0], a[1] + o[1]); g.lineTo(b[0] + o[0], b[1] + o[1]);
    g.lineWidth = 0.5; g.strokeStyle = '#0b0705'; g.stroke();
  }
  // 5) the sheen: freshly washed hair shines in a band across the top of the curve (anisotropic highlight)
  g.globalCompositeOperation = 'lighter';
  for (const [p, o] of strands) {
    if (rnd() < 0.35) continue;
    const t0 = R(0.16, 0.26), t1 = t0 + R(0.12, 0.22);
    const a = at(p, t0), m = at(p, (t0 + t1) / 2), b = at(p, t1);
    g.beginPath(); g.moveTo(a[0] + o[0], a[1] + o[1]); g.quadraticCurveTo(m[0] + o[0], m[1] + o[1] - 0.6, b[0] + o[0], b[1] + o[1]);
    g.lineWidth = R(0.25, 0.55);
    g.strokeStyle = rnd() < 0.8 ? '#4e3a2b' : '#76593f';
    g.globalAlpha = R(0.1, 0.3);
    g.stroke();
  }
  g.globalCompositeOperation = 'source-over';
  // 6) ambient occlusion: darker toward the hairline edge and the back
  const ao = g.createLinearGradient(0, 70, 0, 180);
  ao.addColorStop(0, 'rgba(0,0,0,0)'); ao.addColorStop(0.7, 'rgba(0,0,0,0)'); ao.addColorStop(1, 'rgba(0,0,0,0.35)');
  g.globalAlpha = 1; g.fillStyle = ao; g.fillRect(X0, Y0, W, H);
  g.restore();

  // 7a) the outline is not a hard edge: strands follow it and stick out a little (no more "helmet")
  const rim = [[124, 176], [121, 156], [123, 132], [131, 108], [143, 90], [158, 77], [178, 67], [200, 62], [222, 61], [242, 64],
               [260, 71], [274, 82], [285, 98], [291, 118], [290, 140], [286, 158], [279, 176]];
  for (let i = 0; i < 520; i++) {
    const k = rnd() * (rim.length - 1), i0 = Math.floor(k), f = k - i0;
    const A = rim[i0], B = rim[i0 + 1];
    const x = A[0] + (B[0] - A[0]) * f, y = A[1] + (B[1] - A[1]) * f;
    const tx = B[0] - A[0], ty = B[1] - A[1], tl = Math.hypot(tx, ty) || 1;           // tangent (clockwise)
    const nx = ty / tl, ny = -tx / tl;                                                 // outward normal
    const len = R(4, 11), out = R(-1.5, 2.6);
    const sx = x - nx * 2, sy = y - ny * 2;
    g.beginPath(); g.moveTo(sx, sy);
    g.quadraticCurveTo(sx + tx / tl * len * 0.5 + nx * out, sy + ty / tl * len * 0.5 + ny * out,
                       sx + tx / tl * len + nx * out * 0.6, sy + ty / tl * len + ny * out * 0.6);
    g.lineWidth = R(0.2, 0.55); g.strokeStyle = browns[Math.floor(rnd() * 4)]; g.globalAlpha = R(0.4, 0.85); g.stroke();
  }
  // 7) baby hairs + loose fly-aways crossing the hairline (outside the clip — that's what makes the edge look real)
  const edge = [[126, 172], [130, 160], [136, 152], [143, 145], [154, 139], [166, 139], [178, 139], [192, 135], [206, 134],
                [220, 136], [234, 139], [248, 140], [260, 141], [270, 148], [277, 160], [281, 174]];
  for (let i = 0; i < 340; i++) {
    const k = rnd() * (edge.length - 1), i0 = Math.floor(k), f = k - i0;
    const x = edge[i0][0] + (edge[i0 + 1][0] - edge[i0][0]) * f, y = edge[i0][1] + (edge[i0 + 1][1] - edge[i0][1]) * f;
    const len = R(2.5, 8), dir = x < 162 ? -1 : 1;
    g.beginPath(); g.moveTo(x, y - 2); g.quadraticCurveTo(x + dir * len * 0.5, y + len * 0.4, x + dir * len, y + len * R(0.5, 1.1));
    g.lineWidth = R(0.15, 0.4); g.strokeStyle = rnd() < 0.5 ? '#2a1c13' : '#3a291d'; g.globalAlpha = R(0.35, 0.8); g.stroke();
  }
  for (let i = 0; i < 14; i++) {                                                              // a few longer fly-aways on top
    const x = R(170, 260), y = R(62, 78);
    g.beginPath(); g.moveTo(x, y + 4); g.bezierCurveTo(x + R(-4, 6), y - R(4, 9), x + R(6, 14), y - R(2, 8), x + R(12, 22), y + R(-2, 4));
    g.lineWidth = R(0.15, 0.3); g.strokeStyle = '#3a291d'; g.globalAlpha = R(0.3, 0.6); g.stroke();
  }
  return c.toDataURL('image/png');
}
