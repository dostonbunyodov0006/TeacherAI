import * as auth from './api.js';
import { Avatar } from './avatar.js';
import { SpeechPlayer } from './lipsync.js';
import { Recorder } from './recorder.js';
import { bindPushToTalk } from './ptt.js';
import { Sidebar, longDate, shortDate, localISO } from './sidebar.js';
import { MentorController, MOOD_LABEL } from './mentor.js';
import { StageController } from './stage.js';
import { ListeningController } from './listen.js';
import { BodyDirector } from './body.js';
import { BrowserStt } from './stt-browser.js';
import { Board, lightbox } from './board.js';
import { Attachments } from './media.js';
import { renderVariations } from './imagefx.js';
import { QuizTimer } from './quiz.js';
import { NotebookPanel } from './notebook.js';
import { MicTest } from './mictest.js';

const $ = (id) => document.getElementById(id);
const STATUS = {
  idle: 'Gapirish uchun <kbd>Space</kbd> tugmasini bosib turing',
  listening: 'Tinglayapman… <kbd>Space</kbd>ni gapingiz tugagach qo\'yib yuboring',
  thinking: 'O\'ylayapman…',
  speaking: 'Teacher gapiryapti — to\'xtatish uchun <kbd>Space</kbd>',
};
const EMOTION_LABEL = { encouraging: 'ruhlantirish', explaining: 'tushuntirish', thinking: 'o\'ylash', laughing: 'kulish',
  angry: 'jahl', scolding: 'urishish', shocked: 'hayrat', skeptical: 'shubha',
  mocking: 'masxara', shy_flustered: 'uyalish', angry_flame: '🔥 jahl', hurt_apologetic: 'uzr', teaching: 'dars' };

let avatar, player, recorder, hub, sidebar, mentor, stage, board, quiz, attachments, language = 'uz-UZ';
let notebook;
let lastUserBubble = null;
let listener = null;
let turnStartedAt = 0;          // Space released → first audio (shown as ⚡ latency)
/** STT language for the student's voice: chat in Uzbek, but answer English drills in English. */
let micLang = (() => { try { return localStorage.getItem('ustoz.lang') || 'auto'; } catch { return 'auto'; } })();
let replyLang = null;          // the language Ustoz answers in this turn (from the reply header)
let pttHeld = false;
let body = null;               // whole-body director (sit / lie / tea / look)
let browserStt = null;         // free browser speech recognizer when the server has no STT key
let pendingBarrier = null;     // { segment, promise } — hold that speech segment until the body action finishes           // Space/mic is physically held — guards the async mic start (permission prompt)
let quizAnswer = null;      // { quizId, answerDelayMs } attached to the next turn
/** The conversation every new Space/typed turn is attached to: today's, or an older day being continued. */
let active = null;          // { id, date, isToday, title, messageCount, language }
let loadSeq = 0;            // ignores out-of-order loads when the user clicks days quickly
let activeStream = null;
let assistantBubble = null;

// ---------------- State controller ----------------
const PRESENCE = { idle: 'onlayn', listening: 'tinglayapti…', thinking: "o'ylayapti…", speaking: 'gapiryapti' };
function setState(s) {
  avatar.setState(s);
  $('avatar-wrap').dataset.state = s;
  $('avatar-wrap').dataset.emotion = s === 'speaking' ? avatar.emotion : '';
  $('status').innerHTML = STATUS[s] ?? '';
  $('presence').textContent = PRESENCE[s] ?? '';
}

// ---------------- Boot ----------------
async function boot() {
  const user = await auth.refresh().catch(() => null);
  if (user) return enterApp(user);
  $('login-view').hidden = false;
}

$('login-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const f = new FormData(e.target);
  $('login-error').textContent = '';
  try { enterApp(await auth.login(f.get('username'), f.get('password'))); }
  catch (err) { $('login-error').textContent = err.message; }
});

$('register-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  const f = new FormData(e.target);
  $('register-error').textContent = '';
  try { enterApp(await auth.register(f.get('name'), f.get('username'), f.get('password'))); }
  catch (err) { $('register-error').textContent = err.message; }
});
$('to-register').onclick = (e) => { e.preventDefault(); $('login-form').hidden = true; $('register-form').hidden = false; $('register-form').querySelector('[name=name]').focus(); };
$('to-login').onclick = (e) => { e.preventDefault(); $('register-form').hidden = true; $('login-form').hidden = false; };

// Getting-to-know talk: Teacher asks "o'g'il bolamisiz yoki qiz bola?" → on-screen answer buttons too.
let onboardingStep = 4;
function showGenderChips(step) {
  onboardingStep = step ?? 4;
  $('gender-chips').hidden = onboardingStep !== 2;
}
$('gender-chips').addEventListener('click', (e) => {
  const g = e.target.closest('button')?.dataset.g;
  if (!g) return;
  $('gender-chips').hidden = true;
  if (g !== 'skip') auth.api('/api/me/profile', { method: 'PUT', body: JSON.stringify({ gender: g }) }).catch(() => {});
  $('text-input').value = g === 'male' ? 'O\'g\'il bolaman' : g === 'female' ? 'Qiz bolaman' : 'Keyinroq';
  $('text-form').requestSubmit();
});

async function enterApp(user) {
  onboardingStep = user.onboardingStep ?? 4;
  $('login-view').hidden = true;
  $('app-view').hidden = false;
  language = user.language;
  $('lang-select').value = language;

  avatar = new Avatar($('avatar'));
  player = new SpeechPlayer(avatar);
  player.onVoiceNote = (note, name) => {
    try { if (localStorage.getItem('ustoz.voiceNote.' + note)) return; localStorage.setItem('ustoz.voiceNote.' + note, '1'); } catch { /* ignore */ }
    sysMsg({
    'uz-via-ru': `🔊 Bu brauzerda o'zbekcha ovoz yo'q, shuning uchun Teacher o'zbekchani ruscha ovoz bilan o'qiydi (${name}). ` +
                 "Haqiqiy o'zbek ovozi (Sardor) uchun saytni Microsoft Edge'da oching — bepul.",
    'uz-via-tr': `🔊 O'zbekcha ovoz yo'q — turkcha ovoz bilan o'qilyapti (${name}). Yaxshiroq ovoz uchun Microsoft Edge'da oching.`,
    'no-voices': "🔇 Bu brauzerda birorta ham ovoz topilmadi — Teacher faqat yozuv bilan javob beradi. Chrome yoki Edge'da oching.",
    'other-voice': `🔊 Bu til uchun ovoz yo'q — boshqa ovoz ishlatilyapti (${name}).`,
  }[note] ?? note);
  };
  recorder = new Recorder();                        // must exist before ListeningController wires onLevel
  stage = new StageController({ avatarWrap: $('avatar-wrap'), pill: $('topic-pill') });
  listener = new ListeningController({ avatar, recorder, avatarWrap: $('avatar-wrap'), clockEl: $('rec-clock'), waveCanvas: $('wave') });
  mentor = new MentorController(avatar, stage, { meter: $('anger-meter'), meterLabel: $('anger-label') });
  player.onChunkStart = (item) => {
    if (turnStartedAt) {                             // end-to-end: release → first audible sound
      turnStartedAt = 0;
    }
    mentor.onChunkStart(item);                       // per-segment emotion + timed facial cues
    $('avatar-wrap').dataset.state = 'speaking';
    $('avatar-wrap').dataset.emotion = avatar.emotion;
    $('status').innerHTML = STATUS.speaking;
    $('presence').textContent = PRESENCE.speaking;
    $('caption').textContent = MentorController.displayText(item);
    board.pulse(item.text);                          // highlight words light up as they are spoken
  };
  player.onIdle = () => {
    if (!activeStream) { setState('idle'); $('caption').textContent = ''; mentor.onReplyEnd(); }
    maybeStartQuiz();                                // countdown only once Ustoz has finished asking
  };
  board = new Board($('board'));
  mentor.onMood = (m) => {
    $('mood-chip').hidden = m === 'neutral';
    $('mood-chip').dataset.mood = m;
    $('mood-chip').textContent = MOOD_LABEL[m] ?? m;
  };
  attachments = new Attachments({
    tray: $('att-tray'), dropTarget: $('app-view'), fileInput: $('file-input'), button: $('attach-btn'),
    getSessionId: () => active?.id ?? null,
  });
  attachments.onAdded = () => { avatar.cue('curious_inspect'); };   // Ustoz leans in to look
  attachments.onError = (m) => sysMsg(m);
  body = new BodyDirector({ card: $('call-card'), avatarWrap: $('avatar-wrap'), avatar, player });
  quiz = new QuizTimer($('quiz'));
  renderMicLang();
  setState('idle');

  sidebar = new Sidebar({
    app: $('app-view'),
    fetchPage: (before) => auth.api(`/api/conversations${before ? `?before=${before}` : ''}`),
    onSelect: (id) => openConversation(id),
    onRename: (id, title) => auth.api(`/api/conversations/${id}`, { method: 'PATCH', body: JSON.stringify({ title }) }),
    onDelete: async (id) => {
      await auth.api(`/api/conversations/${id}`, { method: 'DELETE' });
      if (active?.id === id) await openToday();
    },
    fetchNotebooks: () => auth.api('/api/notebooks'),
    onOpenNotebook: (id) => notebook.open(id).then(() => sidebar.setActiveNotebook(id)),
  });
  notebook = new NotebookPanel($('notebook'));
  notebook.onSaved = () => sidebar.refreshNotebooks();
  notebook.onClosed = () => { sidebar.setActiveNotebook(null); };
  sidebar.refreshNotebooks();

  hub = new signalR.HubConnectionBuilder()
    .withUrl(`/hubs/ustoz?tz=${encodeURIComponent(auth.timeZone())}`, { accessTokenFactory: () => auth.token() })
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Warning)
    .build();
  hub.onreconnecting(() => sysMsg('Aloqa uzildi, qayta ulanmoqda…'));
  await hub.start();

  bindPushToTalk({ onStart: startListening, onStop: stopListening, button: $('ptt-button') });
  await openToday();
  loadMentor();
  fetch('/api/health').then((r) => r.json()).then((h) => {
    if (h.stt === 'none' && BrowserStt.supported) {
      browserStt = new BrowserStt((text) => { $('caption').textContent = text ? `🎙 ${text}` : ''; });
    } else if (h.stt === 'none') {
      sysMsg("Bu brauzerda bepul nutqni tanish yo'q — Chrome yoki Edge'ni ishlating (yoki yozib yuboring).");
    }
    console.info(`Teacher AI — STT: ${h.stt} · LLM: ${h.llm} · TTS: ${h.tts}`);   // diagnostics only (not shown in UI)
  });
}

// ---------------- Active conversation ----------------
/** Get-or-create today's thread (server decides "today" in the user's time zone). */
async function openToday() {
  interrupt();
  const seq = ++loadSeq;
  const detail = await auth.api('/api/conversations/today', { method: 'POST' });
  if (seq !== loadSeq) return;
  showConversation(detail);
  await sidebar.refresh();
  sidebar.setActive(active.id);
}

/** "Yangi chat": a fresh, empty conversation (Gemini-style). */
async function openNewChat() {
  interrupt();
  const seq = ++loadSeq;
  const detail = await auth.api('/api/conversations', { method: 'POST' });
  if (seq !== loadSeq) return;
  showConversation(detail);
  await sidebar.refresh();
  sidebar.setActive(active.id);
}

/** Open any past (or today's) day: load its full history and make it the active session. */
async function openConversation(id) {
  if (active?.id === id) return;
  interrupt();
  const seq = ++loadSeq;
  $('messages').replaceChildren(loadingRow());
  try {
    const detail = await auth.api(`/api/conversations/${id}/messages`);
    if (seq !== loadSeq) return;
    showConversation(detail);
    sidebar.setActive(id);
  } catch (e) {
    if (seq === loadSeq) { sysMsg('Suhbatni yuklab bo\'lmadi.'); }
  }
}

function showConversation({ conversation: c, messages }) {
  active = { ...c };
  $('messages').replaceChildren();
  for (const m of messages) bubble(m.role, m.content, m);
  renderWelcome();
  quiz.reset();
  const lastBoard = [...messages].reverse().map((m) => { try { return JSON.parse(m.meta || 'null')?.board; } catch { return null; } })
    .find(Boolean);
  if (lastBoard) board.show(lastBoard, { animate: false }); else board.hide();
  const lastTopic = [...messages].reverse().map((m) => { try { return JSON.parse(m.meta || 'null')?.pivotToTopic; } catch { return null; } })
    .find(Boolean);
  if (lastTopic) stage.setTopic(lastTopic); else stage.clearTopic();
  renderHeader();
  setState('idle');
  $('caption').textContent = '';
}

function renderHeader() {
  const today = sidebar.today ?? localISO();
  $('chat-date').textContent = longDate(active.date);
  $('resume-badge').hidden = active.isToday;
  $('day-title').textContent = active.isToday
    ? 'Bugungi dars'
    : `${shortDate(active.date, today)} suhbati — davom ettirilmoqda`;
}

/** Tab left open past midnight on "today" → move to the new day before the next turn. */
async function ensureFreshToday() {
  if (active?.isToday && active.date !== localISO()) {
    await openToday();
    sysMsg('Yangi kun boshlandi — bugungi suhbat ochildi.');
  }
}

// ---------------- Push-to-talk flow ----------------
async function startListening() {
  pttHeld = true;
  if (quiz.pending) quizAnswer = quiz.lockIn();    // answering the pop-quiz: freeze the clock now
  interrupt();                                     // barge-in: stop speaking / cancel pending turn
  player.unlock();                                 // inside the key gesture → audio output allowed everywhere
  try {
    const firstTime = !recorder.ready;
    await listener.start();                        // active_listening pose + live mic ring
    browserStt?.start(sttLang());                  // free browser STT runs alongside (text only is sent)
    if (!pttHeld) {                                // released while the browser was asking for mic permission
      listener.stop();
      browserStt?.abort();
      setState('idle');
      if (firstTime) $('status').innerHTML = 'Mikrofon tayyor ✅ Endi <kbd>Space</kbd> ni bosib turib gapiring';
      return;
    }
    setState('listening');
  } catch (e) {
    sysMsg('Mikrofonga ruxsat berilmadi. Brauzer sozlamalaridan ruxsat bering.');
    setState('idle');
  }
}

async function stopListening() {
  pttHeld = false;
  if (avatar.state !== 'listening') return;
  let { verdict, rec } = listener.stop();
  turnStartedAt = performance.now();
  await ensureFreshToday();

  // The browser's recogniser is the real judge: if it understood words, a quiet mic must not throw them away.
  let heard = '';
  if (browserStt) {
    heard = await browserStt.stop();
    $('caption').textContent = '';
    if (heard) verdict = 'ok';
  }
  if (verdict !== 'ok') {
    // "Chala gap": react THIS frame — annoyed face + scoff — while the server prepares the spoken snap.
    // The instant face must match the style the reply will be spoken in.
    const style = $('style-select')?.value || 'strict';
    const face = style === 'soft' ? 'teaching' : style === 'savage' ? 'angry_flame' : 'annoyed_cutoff';
    avatar.setState('speaking');
    avatar.setEmotion(face);
    avatar.cue(style === 'soft' ? 'slow_blink' : style === 'savage' ? 'brow_furrow' : 'exasperated_roll');
    $('avatar-wrap').dataset.emotion = face;
    $('status').innerHTML = verdict === 'silent' ? 'Hech narsa eshitilmadi…' : 'Juda qisqa — gapni oxirigacha ayting!';
    if (style !== 'soft') player.react('scoff', style === 'savage' ? 3 : 2);
    if (quizAnswer) quiz.reset();
    quizAnswer = null;
    runTurn('Cutoff', rec?.durationMs ?? 0, rec?.voicedMs ?? 0);
    return;
  }
  setState('thinking');                            // instant feedback: no frozen dead air
  if (browserStt) {
    const text = heard;
    if (!text) {                                   // nothing recognised → tell WHY instead of sending an empty turn
      sysMsg(sttProblem(browserStt.lastError));
      setState('idle');
      return;
    }
    await uploadsSettled();
    runTurn('AskTranscript', text, turnOptions(attachments.take(), rec));   // server applies the cut-off guard
    return;
  }
  await uploadsSettled();
  runTurn('Ask', rec.wavBase64, turnOptions(attachments.take(), rec));
}

/** Human explanation of a browser speech-recognition failure. */
function sttProblem(err) {
  return `${sttProblemText(err)} 👉 Aniq sababni bilish uchun chap paneldagi «🎙 Mikrofon testi» tugmasini bosing.`;
}
function sttProblemText(err) {
  const lang = { 'uz-UZ': "o'zbek", 'ru-RU': 'rus', 'en-US': 'ingliz' }[sttLang()] || sttLang();
  switch (err) {
    case 'not-allowed': case 'service-not-allowed':
      return "🎙 Brauzer mikrofonga ruxsat bermadi. Manzil satridagi 🔒 belgisini bosing → Mikrofon → Ruxsat berish, so'ng sahifani yangilang.";
    case 'network':
      return "🌐 Nutqni tanish internet orqali ishlaydi, lekin ulanib bo'lmadi. Internetni tekshiring. Brave, Opera, Yandex brauzerlarida bu ishlamaydi — Google Chrome yoki Microsoft Edge'da oching.";
    case 'language-not-supported':
      return `Bu brauzer ${lang} tilidagi nutqni tanimaydi. Tepada boshqa tilni (RU / EN) tanlang yoki Chrome / Edge'da oching.`;
    case 'audio-capture':
      return "🎙 Mikrofon topilmadi yoki boshqa dastur (Zoom, Telegram qo'ng'irog'i…) band qilgan. Uni yopib, qayta urinib ko'ring.";
    default:
      return `🎙 Ovozingiz eshitilmadi (${lang} tilida tinglayapman). Space'ni bosib turib, 1–2 soniya aniq gapiring, keyin qo'yib yuboring. ` +
             "Mikrofon to'g'ri tanlanganini tekshiring: Windows → Sozlamalar → Tovush → Kirish.";
  }
}

/** Browser STT needs a fixed language: the chosen one, or the interface language in "Avto" mode. */
function sttLang() { return micLang !== 'auto' ? micLang : (language || 'uz-UZ'); }

$('text-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  await uploadsSettled();
  const images = attachments.take();
  const text = $('text-input').value.trim();
  if (!text && !images.length) return;
  $('text-input').value = '';
  // keep the cursor in the field: the next message can be typed right away (Esc leaves it → Space = talk)
  if (quiz.pending) quizAnswer = quiz.lockIn();
  interrupt();
  player.unlock();
  turnStartedAt = performance.now();
  await ensureFreshToday();
  lastUserBubble = bubble('user', text || '(rasm)', { createdAt: new Date().toISOString(),
    attachments: images.map((i) => ({ url: i.localUrl })) });
  setState('thinking');
  runTurn('AskText', text, turnOptions(images));
});

function interrupt() {
  if (activeStream) { activeStream.dispose(); activeStream = null; }
  if (quiz?.running && !quizAnswer) quiz.reset();          // leaving mid-quiz (new day etc.) cancels it
  player?.stop();
  mentor?.interrupt();
}

/** Options for Ask/AskText: quiz answer timing + the language the student spoke in. */
function turnOptions(images = [], rec = null) {
  const o = {
    quizId: quizAnswer?.quizId ?? null, answerDelayMs: quizAnswer?.answerDelayMs ?? null,
    attachmentIds: images.map((i) => i.id),
    // client voice stats → server-side "chala gap" check
    clientDurationMs: rec?.durationMs ?? null, voicedMs: rec?.voicedMs ?? null, clippedEnd: !!rec?.clippedEnd,
    posture: body?.posture ?? 'stand',
    speechLanguage: browserStt ? sttLang() : micLang,
  };
  quizAnswer = null;
  if (images.length) { avatar.inspect(true); $('avatar-wrap').dataset.inspecting = 'true'; }   // examine while thinking
  return o;
}

/** Don't send a turn while an image is still uploading (max ~15 s). */
async function uploadsSettled() {
  for (let i = 0; attachments.busy && i < 60; i++) await new Promise((r) => setTimeout(r, 250));
}

/** Start a hub stream for the active session. args follow the session id (e.g. payload, options). */
function runTurn(method, ...args) {
  if (!active) return;
  const turnSession = active.id;       // pin: messages go to the session selected when Space was released
  assistantBubble = null;
  activeStream = hub.stream(method, turnSession, ...args).subscribe({
    next: (ev) => { if (active?.id === turnSession) onEvent(ev); },
    error: (err) => { activeStream = null; sysMsg(err.message || 'Xatolik'); setState('idle'); },
    complete: () => { activeStream = null; if (!player.busy) { setState('idle'); maybeStartQuiz(); } },
  });
}

function onEvent(ev) {
  switch (ev.type) {
    case 'state': setState(ev.state); break;
    case 'transcript': lastUserBubble = bubble('user', ev.text, { createdAt: new Date().toISOString() }); break;
    case 'cutoff':                                           // server judged the transcript a fragment
      avatar.setState('speaking'); avatar.setEmotion('annoyed_cutoff'); avatar.cue('exasperated_roll');
      if (lastUserBubble) lastUserBubble.classList.add('fragment');
      break;
    case 'attachments':                                       // server confirmed images for this turn (voice turn → add thumbs)
      if (lastUserBubble && !lastUserBubble.querySelector('.thumbs')) addThumbs(lastUserBubble, ev.images);
      break;
    case 'image_pending': board.showImagesPending(ev.text, ev.index); break;
    case 'images':
      board.showImages(ev.text, ev.images);
      addThumbs(assistantBubble ?? bubble('assistant', ''), ev.images);
      break;
    case 'image_filters':                                     // offline: render variations in the browser
      renderVariations(ev.sourceUrl, ev.presets).then((items) => {
        board.showImages(ev.text, items);
        addThumbs(assistantBubble ?? bubble('assistant', ''), items);
      }).catch(() => sysMsg('Variantlarni chizib bo\'lmadi.'));
      break;
    case 'policy':
      if (ev.policy.showSupportCard) $('support-card').hidden = false;
      break;
    case 'mentor':                                    // v2 header: emotion, rageLevel, shyLevel, userMood, replyLanguage, cues, pivotToTopic
      replyLang = ev.mentor.replyLanguage || null;
      pendingBarrier = null;
      if (ev.mentor.bodyAction?.action) {
        const done = body.perform(ev.mentor.bodyAction.action);
        if (ev.mentor.bodyAction.beforeSegment >= 0) pendingBarrier = { segment: ev.mentor.bodyAction.beforeSegment, promise: done };
      }
      $('avatar-wrap').dataset.inspecting = 'false';
      mentor.onHeader(ev.mentor);
      break;
    case 'speech': {
      const item = { text: ev.text, speech: ev.speech, segment: ev.segment };
      if (pendingBarrier && ev.segment?.index === pendingBarrier.segment && ev.segment.firstOfSegment) {
        player.enqueueBarrier(pendingBarrier.promise);   // e.g. sip the tea first, then speak
        pendingBarrier = null;
      }
      if (!assistantBubble) assistantBubble = bubble('assistant', '', { emotion: ev.segment?.emotion, createdAt: new Date().toISOString() });
      const body = assistantBubble.firstChild;
      if (body.childNodes.length) body.append(' ');
      if (ev.segment && ev.segment.type !== 'speech') {
        const v = document.createElement('span'); v.className = 'vocal'; v.textContent = MentorController.displayText(item);
        body.append(v);
      } else body.append(ev.text);
      scrollChat();
      player.enqueue(item, replyLang || active.language || language);
      break;
    }
    case 'board': board.show(ev.board); break;
    case 'quiz': quiz.arm(ev.quiz); break;                   // countdown starts when playback goes idle
    case 'quiz_result': quiz.result({ correct: ev.quizResult.correct, feedback: ev.quizResult.feedback }); break;
    case 'mistakes':
      for (const m of ev.mistakes) {
        const div = document.createElement('div');
        div.className = 'msg mistake';
        div.innerHTML = '<span>✗ </span>';
        const wrong = document.createElement('s'); wrong.textContent = m.userSaid;
        const right = document.createElement('strong'); right.textContent = m.correctForm;
        div.append(wrong, ' → ', right);
        $('messages').append(div);
      }
      scrollChat();
      break;
    case 'done':
      active.messageCount += 2;
      renderHeader();
      if (ev.rageLevel != null) mentor.setRage(ev.rageLevel);
      if (ev.latencyMs) console.info(`[Ustoz] first audio after ${ev.latencyMs} ms (server)`);
      sidebar.refresh().then(() => sidebar.setActive(active.id));
      loadMentor();
      showGenderChips(ev.onboarding);
      break;
    case 'error': sysMsg(ev.text, 'error'); setState('idle'); break;
  }
}

// ---------------- Mentor profile (real level, homework, anger) ----------------
async function loadMentor() {
  const p = await auth.api('/api/mentor').catch(() => null);
  if (!p) return;
  mentor.setRage(p.angerLevel);
  const en = p.progress.find((x) => x.subject === 'english');
  $('level-chip').textContent = `Daraja: ${en?.level ?? '—'}${en?.ieltsEstimate ? ` · IELTS ≈ ${en.ieltsEstimate}` : ''}`;
  $('level-select').value = en?.level ?? '';
  player.levelRate = LEVEL_RATE[en?.level] ?? 1;
  $('style-select').value = p.style;
  applyStyle(p.style, false);
  const pending = p.homework.filter((h) => h.status === 'Pending');
  $('hw-count').textContent = pending.length;
  $('hw-list').replaceChildren(...(p.homework.length ? p.homework : [null]).map((h) => {
    const li = document.createElement('li');
    if (!h) { li.textContent = 'Hali uy vazifasi yo\'q.'; return li; }
    li.className = h.status;
    const t = document.createElement('div'); t.className = 'hw-title'; t.textContent = h.title;
    const m = document.createElement('div'); m.className = 'hw-meta';
    m.textContent = [`muddat: ${h.dueDate}`, h.missedCheckins ? `${h.missedCheckins} marta qilinmagan` : '',
                     h.teacherFeedback ? `Teacher: ${h.teacherFeedback}` : ''].filter(Boolean).join(' · ');
    li.append(t, m);
    return li;
  }));
}
$('hw-toggle').onclick = () => {
  const open = $('hw-list').hidden;
  $('hw-list').hidden = !open;
  $('hw-toggle').setAttribute('aria-expanded', String(open));
};
$('level-select').onchange = async (e) => {
  player.levelRate = LEVEL_RATE[e.target.value] ?? 1;
  if (LEVEL_LINE[e.target.value]) {
    interrupt(); player.unlock(); avatar.setState('speaking'); avatar.setEmotion('teaching');
    player.enqueue({ text: LEVEL_LINE[e.target.value], speech: {}, segment: { index: 0, type: 'speech', emotion: 'teaching', intensity: 1, firstOfSegment: true } }, 'uz-UZ');
  }
  if (!e.target.value) return;
  await auth.api('/api/mentor/progress/english', { method: 'PUT', body: JSON.stringify({ level: e.target.value }) });
  loadMentor();
};
// ---------------- Style & level: Teacher really changes (face, room, voice, words) ----------------
const STYLE_LINE = {
  soft: ["Mayli, endi yumshoq va sabrli bo'laman. Shoshilmasdan, birga o'rganamiz.", 'teaching'],
  strict: ["Yaxshi. Endi qattiqqo'l ustozman — dangasalikka yo'l yo'q!", 'scolding'],
  savage: ["Olovli rejim yoqildi! Endi hech bir xatoni kechirmayman — tayyor tur!", 'angry_flame'],
};
function applyStyle(style, speak = true) {
  $('call-card').dataset.style = style;
  avatar.style = style;
  if (!speak) return;
  const [text, emotion] = STYLE_LINE[style] ?? STYLE_LINE.strict;
  interrupt(); player.unlock();
  avatar.setState('speaking'); avatar.setEmotion(emotion); $('avatar-wrap').dataset.emotion = emotion;
  player.enqueue({ text, speech: {}, segment: { index: 0, type: 'speech', emotion, intensity: 1, firstOfSegment: true } }, 'uz-UZ');
}
$('style-select').onchange = async (e) => {
  let style = e.target.value;
  const age = auth.getUser()?.age;
  if (style === 'savage' && age != null && age < 16) {
    sysMsg("16 yoshgacha bo'lgan o'quvchilar uchun «Olovli» uslub o'chirilgan — «Qattiqqo'l» qoldi.");
    style = 'strict'; e.target.value = style;
  }
  applyStyle(style);
  await auth.api('/api/mentor/style', { method: 'PUT', body: JSON.stringify({ style }) }).catch(() => {});
};
const LEVEL_LINE = {
  A1: "A1 — boshlang'ich daraja. Sekin va juda sodda gapiraman, har so'zni tarjima qilaman.",
  A2: "A2 daraja. Oddiy gaplar bilan, tarjimasi bilan o'rganamiz.",
  B1: "B1 — o'rta daraja. Endi ko'proq inglizcha gapiramiz.",
  B2: "B2 daraja. Erkin suhbat, murakkab grammatika — asosan inglizcha.",
  C1: "C1 — ilg'or daraja. Idiomalar va nozik ma'nolar, faqat inglizcha.",
  C2: "C2 — eng yuqori daraja. Ona tili darajasida, tez sur'atda gaplashamiz.",
};
/** Browser-voice speed by level: A1 slow and clear … C2 natural fast. */
const LEVEL_RATE = { A1: 0.82, A2: 0.9, B1: 1, B2: 1.05, C1: 1.1, C2: 1.15 };

// ---------------- Pop-quiz ----------------
function maybeStartQuiz() {
  if (!quiz.pending || quiz.running || activeStream || player.busy) return;
  const q = quiz.pending;
  hub.invoke('QuizStarted', q.id).catch(() => {});          // server starts its own clock too
  quiz.start(() => {                                         // time's up → Ustoz reacts
    if (avatar.state === 'listening') return;                // student is mid-answer; let them finish
    interrupt();
    setState('thinking');
    runTurn('QuizTimeout', q.id);
  });
}

// ---------------- Weekly report ----------------
$('weekly-report').onclick = async () => {
  sidebar.closeMobile();
  interrupt();
  const r = await auth.api('/api/analytics/weekly-report').catch(() => null);
  if (!r) { sysMsg('Hisobotni yuklab bo\'lmadi.'); return; }
  board.showReport(r);                                       // numbers on the board…
  setState('thinking');
  runTurn('WeeklyRoast');                                    // …and Ustoz roasts them out loud
};

// ---------------- Board / mic language ----------------
$('board-toggle').onclick = () => {
  if (board.visible) { board.hide(); return; }
  $('board').hidden = false;
  $('board').closest('.stage').classList.add('with-board');
};
function renderMicLang() {
  for (const btn of $('lang-seg').querySelectorAll('button'))
    btn.setAttribute('aria-checked', String(btn.dataset.lang === micLang));
}
$('lang-seg').addEventListener('click', (e) => {
  const btn = e.target.closest('button[data-lang]');
  if (!btn) return;
  micLang = btn.dataset.lang;                     // auto | uz-UZ | ru-RU | en-US — Ustoz answers in the language you speak
  try { localStorage.setItem('ustoz.lang', micLang); } catch { /* private mode */ }
  renderMicLang();
});

// ---------------- Welcome / onboarding (empty conversation) ----------------
const SUGGESTIONS = [
  'Teacher, choy iching ☕',
  'Present Perfect ni oddiy misollar bilan tushuntiring',
  'Как подготовиться к IELTS за 3 месяца?',
  'Help me make a study plan for this week',
  "Men IELTS 8.0 ni bemalol olaman, a? 😏",
];
function renderWelcome() {
  if ($('messages').querySelector('.msg')) return;
  const w = document.createElement('div');
  w.className = 'welcome';
  const meeting = onboardingStep < 4;
  w.innerHTML = meeting
    ? '<h4>Salom! 👋 Men — Teacher.</h4><p>Keling, avval tanishib olaylik — sizdan bir-ikki narsani so\'rayman. '
      + '<kbd>Space</kbd> ni bosib turib <b>"Salom"</b> deng yoki pastdagi tugmani bosing.</p>'
    : '<h4>Salom! 👋 Men — Teacher.</h4><p>O\'zbek, rus yoki ingliz tilida gapiring — o\'sha tilda javob beraman. '
      + '<kbd>Space</kbd> ni bosib turib gapiring yoki pastda yozing. Misol uchun:</p>';
  const row = document.createElement('div');
  row.className = 'suggest';
  for (const text of meeting ? ['Salom, Teacher!'] : SUGGESTIONS) {
    const b = document.createElement('button');
    b.type = 'button';
    b.textContent = text;
    b.onclick = () => { $('text-input').value = text; $('text-form').requestSubmit(); };
    row.append(b);
  }
  w.append(row);
  $('messages').append(w);
}

$('new-session').onclick = () => { sidebar.closeMobile(); openNewChat(); };
let micTest = null;
$('mic-test').onclick = () => { sidebar.closeMobile(); interrupt(); (micTest ??= new MicTest({ getLang: () => sttLang() })).open(); };
$('new-notebook').onclick = async () => {
  sidebar.closeMobile();
  const n = await notebook.create();
  await sidebar.refreshNotebooks();
  sidebar.setActiveNotebook(n.id);
};
$('logout').onclick = async () => { interrupt(); await hub?.stop(); await auth.logout(); location.reload(); };
$('lang-select').onchange = async (e) => {
  language = e.target.value;
  const voices = { 'uz-UZ': 'uz-UZ-SardorNeural', 'ru-RU': 'ru-RU-DmitryNeural', 'en-US': 'en-US-AndrewNeural' };
  await auth.api('/api/preferences', { method: 'PUT',
    body: JSON.stringify({ language, voice: voices[language], avatarId: 'ustoz-classic', speechRate: 1 }) });
  if (active && !active.messageCount) active.language = language;   // only empty threads switch language
};

function bubble(role, text, meta = {}) {
  $('messages').querySelector('.welcome')?.remove();
  const div = document.createElement('div');
  div.className = `msg ${role}`;
  const body = document.createElement('span');
  body.textContent = text;
  div.append(body);
  if (role !== 'system' && meta.createdAt) {
    const m = document.createElement('span');
    m.className = 'meta';
    const time = new Date(meta.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    m.textContent = [time, EMOTION_LABEL[meta.emotion], meta.angerLevel ? `jahl ${meta.angerLevel}/10` : '']
      .filter(Boolean).join(' · ');
    div.append(m);
  }
  if (meta.attachments?.length) addThumbs(div, meta.attachments);
  $('messages').append(div);
  scrollChat();
  return div;
}

function addThumbs(bubbleEl, images) {
  let wrap = bubbleEl.querySelector('.thumbs');
  if (!wrap) { wrap = document.createElement('div'); wrap.className = 'thumbs'; bubbleEl.append(wrap); }
  for (const im of images) {
    const img = document.createElement('img');
    img.src = im.url; img.alt = im.label || 'rasm'; img.loading = 'lazy';
    img.onclick = () => lightbox(im.url, img.alt);
    wrap.append(img);
  }
  scrollChat();
}
function loadingRow() { const d = document.createElement('div'); d.className = 'day-sep'; d.textContent = 'Yuklanmoqda…'; return d; }
/** System notice in the chat. kind 'info' (grey, ℹ) or 'error' (red). */
const sysMsg = (t, kind = 'info') => { const b = bubble('system', t); b?.classList?.add(kind); return b; };
const scrollChat = () => { const m = $('messages'); m.scrollTop = m.scrollHeight; };

boot();

// Esc in the text field → leave it, so Space works as push-to-talk again.
$('text-input').addEventListener('keydown', (e) => { if (e.key === 'Escape') e.target.blur(); });
