// "Mikrofon testi": a self-check the student can run in 10 seconds. It answers "why doesn't Teacher hear me?"
// step by step — browser, internet, microphone permission, loudness, speech recognition, Teacher's voice — and
// ends with a plain verdict ("✅ ishlayapti" / "❌ sababi: … · yechim: …") plus a copyable report.
import { loadVoices, planSpeech } from './voices.js';

const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
const LANGS = { 'uz-UZ': "O'zbekcha", 'ru-RU': 'Русский', 'en-US': 'English' };
const PHRASE = { 'uz-UZ': '"Salom, Teacher, meni eshityapsizmi?"', 'ru-RU': '"Привет, Тичер, ты меня слышишь?"', 'en-US': '"Hello Teacher, can you hear me?"' };
const LISTEN_MS = 6000;

/** Which browser is this? (Brave / Opera / Yandex expose SpeechRecognition but it never works there.) */
function browserInfo() {
  const ua = navigator.userAgent;
  if (navigator.brave) return { name: 'Brave', sttOk: false };
  if (/YaBrowser/.test(ua)) return { name: 'Yandex Browser', sttOk: false };
  if (/OPR\//.test(ua)) return { name: 'Opera', sttOk: false };
  if (/Edg\//.test(ua)) return { name: 'Microsoft Edge', sttOk: !!SR };
  if (/Firefox\//.test(ua)) return { name: 'Firefox', sttOk: false };
  if (/Chrome\//.test(ua)) return { name: 'Google Chrome', sttOk: !!SR };
  if (/Safari\//.test(ua)) return { name: 'Safari', sttOk: !!SR };
  return { name: 'Noma\'lum brauzer', sttOk: !!SR };
}

const STT_ERR = {
  'not-allowed': ["Brauzer nutqni tanishga ruxsat bermadi.", "Manzil satridagi 🔒 → Mikrofon → «Ruxsat berish», keyin sahifani yangilang."],
  'service-not-allowed': ["Brauzer nutqni tanish xizmatini bloklagan.", "Chrome yoki Edge'ning oddiy (inkognito emas) oynasida oching."],
  network: ["Nutqni tanish serveriga ulanib bo'lmadi.", "Internetni tekshiring. Brave/Opera/Yandex'da bu ishlamaydi — Chrome yoki Edge'dan foydalaning."],
  'language-not-supported': ["Brauzer bu tildagi nutqni tanimaydi.", "Testda boshqa tilni tanlang yoki Chrome'da sinab ko'ring."],
  'audio-capture': ["Mikrofon topilmadi yoki band.", "Zoom/Telegram qo'ng'irog'ini yoping, mikrofon ulanganini tekshiring."],
  'no-speech': ["Brauzer hech qanday so'z eshitmadi.", "Balandroq va mikrofonga yaqinroq gapiring; Windows → Tovush → Kirish'da to'g'ri mikrofon tanlanganini tekshiring."],
  aborted: ["Tinglash to'xtatildi.", "Qayta sinang."],
};

export class MicTest {
  #dlg; #steps = {}; #report = []; #stream = null; #ctx = null; #rec = null; #raf = 0; #running = false;
  /** @param {{ getLang: () => string, onClose?: () => void }} o */
  constructor(o) {
    this.o = o;
    this.#dlg = document.createElement('dialog');
    this.#dlg.className = 'mic-test';
    this.#dlg.innerHTML = `
      <header><h3>🎙 Mikrofon testi</h3><button type="button" class="icon mt-x" aria-label="Yopish">&#10005;</button></header>
      <p class="mt-hint">«Boshlash»ni bosing va 6 soniya ichida baland ovozda ayting: <b class="mt-phrase"></b></p>
      <label class="mt-lang">Til: <select>${Object.entries(LANGS).map(([k, v]) => `<option value="${k}">${v}</option>`).join('')}</select></label>
      <ol class="mt-steps">
        <li data-k="browser"><i>⏳</i><div><b>Brauzer</b><span></span></div></li>
        <li data-k="net"><i>⏳</i><div><b>Internet</b><span></span></div></li>
        <li data-k="perm"><i>⏳</i><div><b>Mikrofonga ruxsat</b><span></span></div></li>
        <li data-k="level"><i>⏳</i><div><b>Ovoz balandligi</b><span></span><meter min="0" max="1" low=".08" high=".5" optimum=".3" value="0"></meter></div></li>
        <li data-k="stt"><i>⏳</i><div><b>Nutqni tanish</b><span></span><q class="mt-heard"></q></div></li>
        <li data-k="tts"><i>⏳</i><div><b>Teacher ovozi</b><span></span></div></li>
      </ol>
      <div class="mt-verdict" hidden></div>
      <footer>
        <button type="button" class="mt-copy ghost" hidden>📋 Natijani nusxalash</button>
        <button type="button" class="mt-start">▶ Boshlash</button>
      </footer>`;
    document.body.append(this.#dlg);
    for (const li of this.#dlg.querySelectorAll('.mt-steps li')) this.#steps[li.dataset.k] = li;
    this.#dlg.querySelector('.mt-x').onclick = () => this.close();
    this.#dlg.querySelector('.mt-start').onclick = () => this.run();
    this.#dlg.querySelector('.mt-copy').onclick = () => this.#copy();
    this.#dlg.addEventListener('close', () => this.#cleanup());
    this.#dlg.addEventListener('keydown', (e) => { if (e.code === 'Space' && e.target.tagName !== 'BUTTON') e.stopPropagation(); });
    this.#langSel.onchange = () => { this.#dlg.querySelector('.mt-phrase').textContent = PHRASE[this.#langSel.value]; };
  }

  get #langSel() { return this.#dlg.querySelector('.mt-lang select'); }

  open() {
    this.#langSel.value = LANGS[this.o.getLang()] ? this.o.getLang() : 'uz-UZ';
    this.#langSel.onchange();
    this.#reset();
    document.body.classList.add('modal-open');
    this.#dlg.showModal();
  }
  close() { this.#dlg.close(); }

  #reset() {
    for (const li of Object.values(this.#steps)) { this.#set(li.dataset.k, '⏳', ''); li.className = ''; }
    this.#steps.level.querySelector('meter').value = 0;
    this.#steps.stt.querySelector('.mt-heard').textContent = '';
    const v = this.#dlg.querySelector('.mt-verdict'); v.hidden = true; v.className = 'mt-verdict';
    this.#dlg.querySelector('.mt-copy').hidden = true;
    this.#report = [];
  }

  #set(k, icon, text, state) {
    const li = this.#steps[k];
    li.querySelector('i').textContent = icon;
    li.querySelector('span').textContent = text;
    if (state) li.className = state;
    if (state) this.#report.push(`${icon} ${li.querySelector('b').textContent}: ${text}`);
  }

  async run() {
    if (this.#running) return;
    this.#running = true;
    const startBtn = this.#dlg.querySelector('.mt-start');
    startBtn.disabled = true; startBtn.textContent = '… tekshirilmoqda';
    this.#reset();
    const lang = this.#langSel.value;
    let problem = null;                                     // the first blocking problem → the verdict
    const fail = (k, text, fix) => { this.#set(k, '❌', text, 'bad'); problem ??= { text, fix }; };
    try {
      // 1) browser
      const b = browserInfo();
      if (b.sttOk) this.#set('browser', '✅', `${b.name} — ovozni taniy oladi`, 'ok');
      else fail('browser', `${b.name} — bu brauzerda nutqni tanish ishlamaydi`, 'Saytni Google Chrome yoki Microsoft Edge\'da oching.');

      // 2) internet (the recogniser runs on Google / Microsoft servers)
      if (navigator.onLine) this.#set('net', '✅', 'ulangan', 'ok');
      else fail('net', 'internet yo\'q', 'Nutqni tanish internet orqali ishlaydi — internetga ulaning.');

      // 3) microphone permission + device
      try {
        this.#stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true } });
        const label = this.#stream.getAudioTracks()[0]?.label || 'mikrofon';
        this.#set('perm', '✅', `ruxsat bor · ${label}`, 'ok');
      } catch (e) {
        const denied = e?.name === 'NotAllowedError' || e?.name === 'SecurityError';
        fail('perm', denied ? 'ruxsat berilmagan' : 'mikrofon topilmadi',
          denied ? 'Manzil satridagi 🔒 → Mikrofon → «Ruxsat berish», keyin sahifani yangilang.'
                 : 'Mikrofonni ulang yoki Windows → Tovush → Kirish\'da yoqing.');
      }

      // 4 + 5) loudness meter and speech recognition, together, while the student speaks
      this.#set('level', '🎤', `gapiring: ${PHRASE[lang]}`);
      this.#set('stt', '🎤', 'tinglayapman…');
      const [peak, heard] = await Promise.all([this.#measure(), this.#recognise(lang, b.sttOk && navigator.onLine)]);
      if (!this.#stream) this.#set('level', '⚪', 'tekshirib bo\'lmadi (mikrofon yo\'q)', 'skip');
      else if (peak >= 0.08) this.#set('level', '✅', `yaxshi (eng baland: ${Math.round(peak * 100)}%)`, 'ok');
      else if (peak >= 0.025) this.#set('level', '⚠️', `past (${Math.round(peak * 100)}%) — balandroq gapiring`, 'warn');
      else fail('level', `ovoz deyarli yo'q (${Math.round(peak * 100)}%)`,
        "Windows → Sozlamalar → Tovush → Kirish: to'g'ri mikrofon tanlanganini va balandligi 80–100% ekanini tekshiring.");
      if (heard.text) this.#set('stt', '✅', `tanidi (${LANGS[lang]})`, 'ok');
      else if (heard.skipped) this.#set('stt', '⚪', 'tekshirilmadi (yuqoridagi muammo sababli)', 'skip');
      else { const [t, f] = STT_ERR[heard.error] ?? STT_ERR['no-speech']; fail('stt', t, f); }

      // 6) Teacher's own voice
      await loadVoices();
      const plan = planSpeech(lang === 'uz-UZ' ? 'Salom! Men sizni eshityapman.' : lang === 'ru-RU' ? 'Привет! Я тебя слышу.' : 'Hello! I can hear you.', lang);
      if (!plan.voice) this.#set('tts', '⚠️', 'brauzerda ovoz yo\'q — Teacher faqat yozuv bilan javob beradi', 'warn');
      else {
        const native = !plan.note;
        this.#set('tts', native ? '✅' : '⚠️', native ? `${plan.voice.name}` : `${plan.voice.name} (o'zbekcha ovoz yo'q — Edge'da tabiiy ovoz bor)`, native ? 'ok' : 'warn');
        try { const u = new SpeechSynthesisUtterance(plan.text); u.voice = plan.voice; u.lang = plan.voice.lang; speechSynthesis.cancel(); speechSynthesis.speak(u); } catch { /* ignore */ }
      }
    } finally {
      this.#cleanup();
      this.#running = false;
      startBtn.disabled = false; startBtn.textContent = '↻ Qayta sinash';
    }
    // verdict
    const v = this.#dlg.querySelector('.mt-verdict');
    v.hidden = false;
    if (problem) { v.classList.add('bad'); v.innerHTML = `<b>❌ Sababi:</b> ${esc(problem.text)}<br><b>Yechim:</b> ${esc(problem.fix)}`; }
    else { v.classList.add('ok'); v.innerHTML = '<b>✅ Hammasi ishlayapti!</b> <kbd>Space</kbd> ni bosib turib gapiring — Teacher eshitadi.'; }
    this.#report.unshift(`Teacher AI — mikrofon testi (${new Date().toLocaleString()}) · ${navigator.userAgent}`);
    this.#report.push(problem ? `NATIJA: ❌ ${problem.text} → ${problem.fix}` : 'NATIJA: ✅ hammasi ishlayapti');
    this.#dlg.querySelector('.mt-copy').hidden = false;
  }

  /** Live loudness for LISTEN_MS; resolves with the peak RMS (0..1). */
  #measure() {
    if (!this.#stream) return new Promise((r) => setTimeout(() => r(0), LISTEN_MS));
    this.#ctx = new AudioContext();
    const src = this.#ctx.createMediaStreamSource(this.#stream), an = this.#ctx.createAnalyser();
    an.fftSize = 1024; src.connect(an);
    const buf = new Float32Array(an.fftSize), meter = this.#steps.level.querySelector('meter');
    let peak = 0;
    const t0 = performance.now();
    return new Promise((resolve) => {
      const tick = () => {
        an.getFloatTimeDomainData(buf);
        let s = 0; for (const x of buf) s += x * x;
        const rms = Math.min(1, Math.sqrt(s / buf.length) * 4);
        peak = Math.max(peak, rms); meter.value = rms;
        if (performance.now() - t0 < LISTEN_MS) this.#raf = requestAnimationFrame(tick); else { meter.value = peak; resolve(peak); }
      };
      tick();
    });
  }

  /** Browser speech recognition for LISTEN_MS; resolves with { text, error, skipped }. */
  #recognise(lang, possible) {
    if (!SR || !possible) return new Promise((r) => setTimeout(() => r({ text: '', skipped: true }), LISTEN_MS));
    const heardEl = this.#steps.stt.querySelector('.mt-heard');
    return new Promise((resolve) => {
      let text = '', error = null, done = false;
      const finish = () => { if (done) return; done = true; try { this.#rec?.stop(); } catch { /* */ } resolve({ text: text.trim(), error }); };
      const rec = new SR();
      rec.lang = lang; rec.continuous = true; rec.interimResults = true;
      rec.onresult = (e) => {
        let all = '';
        for (let i = 0; i < e.results.length; i++) all += e.results[i][0].transcript + ' ';
        text = all; heardEl.textContent = all.trim();
      };
      rec.onerror = (e) => { if (e.error !== 'aborted') error = e.error; };
      rec.onend = () => setTimeout(finish, 50);
      this.#rec = rec;
      try { rec.start(); } catch { error = 'service-not-allowed'; }
      setTimeout(finish, LISTEN_MS);
    });
  }

  #cleanup() {
    cancelAnimationFrame(this.#raf);
    try { this.#rec?.abort(); } catch { /* */ }
    this.#rec = null;
    this.#stream?.getTracks().forEach((t) => t.stop());   // release the mic (the red dot in the tab goes away)
    this.#stream = null;
    this.#ctx?.close().catch(() => {});
    this.#ctx = null;
    if (!this.#dlg.open) { document.body.classList.remove('modal-open'); this.o.onClose?.(); }
  }

  async #copy() {
    const btn = this.#dlg.querySelector('.mt-copy');
    try { await navigator.clipboard.writeText(this.#report.join('\n')); btn.textContent = '✅ Nusxalandi'; }
    catch { btn.textContent = 'Nusxalab bo\'lmadi'; }
    setTimeout(() => { btn.textContent = '📋 Natijani nusxalash'; }, 2000);
  }
}

const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
