// Stage controller: the visual "weather" around the avatar.
//   rage  0..10 → flame aura behind the bust (SVG flames + heat shimmer), stage tremor at ≥ 9
//   shy   0..10 → soft pink glow + floating hearts
//   topic       → "📌 Mavzu" pill; pivots back to the topic get a short "Mavzuga qaytamiz" flash
// Values are eased (not snapped) so mood swings feel like weather, not a light switch.

const FLAMES = 7;

export class StageController {
  #lastFire = ''; #lastShy = '';
  #wrap; #aura; #hearts; #pill; #pillText; #flash;
  #rage = 0; #rageTarget = 0; #shy = 0; #shyTarget = 0; #raf = 0; #topic = '';

  constructor({ avatarWrap, pill }) {
    this.#wrap = avatarWrap;
    this.#pill = pill;
    this.#pillText = pill.querySelector('.topic-text');
    this.#flash = pill.querySelector('.topic-flash');

    // Flame aura: several tongues of fire with staggered flicker; all driven by one CSS var (--rage 0..1).
    this.#aura = document.createElement('div');
    this.#aura.className = 'rage-aura';
    this.#aura.setAttribute('aria-hidden', 'true');
    this.#aura.innerHTML = `
      <svg viewBox="0 0 400 460" preserveAspectRatio="none">
        <defs>
          <linearGradient id="flame-g" x1="0" y1="1" x2="0" y2="0">
            <stop offset="0" stop-color="#ff3d00"/><stop offset=".55" stop-color="#ff9100"/><stop offset="1" stop-color="#ffd54f" stop-opacity="0"/>
          </linearGradient>
          <filter id="heat"><feTurbulence type="fractalNoise" baseFrequency="0.012 0.04" numOctaves="2" seed="3">
            <animate attributeName="baseFrequency" dur="3s" values="0.012 0.04;0.014 0.06;0.012 0.04" repeatCount="indefinite"/></feTurbulence>
            <feDisplacementMap in="SourceGraphic" scale="18"/></filter>
        </defs>
        <g filter="url(#heat)">${Array.from({ length: FLAMES }, (_, i) => {
          const x = 30 + i * (340 / (FLAMES - 1)), h = 180 + ((i * 53) % 90), w = 70;
          return `<path class="flame" style="--i:${i}" fill="url(#flame-g)"
            d="M${x - w / 2} 460 C${x - w / 2} ${460 - h * .45} ${x - 6} ${460 - h * .7} ${x} ${460 - h}
               C${x + 8} ${460 - h * .65} ${x + w / 2} ${460 - h * .45} ${x + w / 2} 460 Z"/>`;
        }).join('')}</g>
      </svg>`;
    this.#hearts = document.createElement('div');
    this.#hearts.className = 'shy-hearts';
    this.#hearts.setAttribute('aria-hidden', 'true');
    this.#hearts.innerHTML = Array.from({ length: 6 }, (_, i) => `<i style="--i:${i}">♥</i>`).join('');
    avatarWrap.prepend(this.#aura);
    avatarWrap.append(this.#hearts);
    this.#loop();
  }

  setRage(level) { this.#rageTarget = Math.max(0, Math.min(10, Number(level) || 0)); }
  setShy(level) { this.#shyTarget = Math.max(0, Math.min(10, Number(level) || 0)); }
  /** Shyness fades on its own after the flustered moment (it's a reaction, not a mood). */
  coolShy(afterMs = 2500) { clearTimeout(this._shyT); this._shyT = setTimeout(() => this.setShy(0), afterMs); }

  /** @param {{announce?: boolean}} o announce = this reply pulled the conversation back to the topic */
  setTopic(topic, { announce = false } = {}) {
    topic = (topic || '').trim();
    if (!topic) return;
    const changed = topic !== this.#topic;
    this.#topic = topic;
    this.#pill.hidden = false;
    this.#pillText.textContent = topic;
    if (changed) { this.#pill.classList.remove('bump'); void this.#pill.offsetWidth; this.#pill.classList.add('bump'); }
    if (announce) {
      this.#flash.classList.remove('show'); void this.#flash.offsetWidth; this.#flash.classList.add('show');
    }
  }
  get topic() { return this.#topic; }
  clearTopic() { this.#topic = ''; this.#pill.hidden = true; }

  #loop = () => {
    this.#rage += (this.#rageTarget - this.#rage) * 0.04;
    this.#shy += (this.#shyTarget - this.#shy) * 0.06;
    const r = this.#rage / 10, s = this.#shy / 10;
    // flames only really ignite above ~4: below that the mentor is just grumpy, not on fire
    const fire = Math.max(0, (r - 0.35) / 0.65);
    // Only write when the value visibly changes: an unchanged CSS variable written every frame still forces a
    // style recalc of the whole avatar and a repaint of the huge blurred halo (was the main source of lag).
    const fr = fire.toFixed(2), sh = s.toFixed(2), tremor = r >= 0.9 ? 'true' : 'false';
    if (fr !== this.#lastFire) { this.#lastFire = fr; this.#wrap.style.setProperty('--rage', fr); }
    if (sh !== this.#lastShy) { this.#lastShy = sh; this.#wrap.style.setProperty('--shy', sh); }
    if (this.#wrap.dataset.tremor !== tremor) this.#wrap.dataset.tremor = tremor;
    this.#raf = requestAnimationFrame(this.#loop);
  };
}
