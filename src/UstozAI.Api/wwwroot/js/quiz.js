// Pop-quiz countdown: ring timer over the stage. Starts after Ustoz finishes asking; pressing Space (or sending
// text) "locks in" the answer time; reaching zero fires onExpire. Shows the verdict when it comes back.

const R = 44, C = 2 * Math.PI * R;

export class QuizTimer {
  #root; #ring; #num; #q; #hint;
  #quiz = null; #t0 = 0; #raf = 0; #onExpire = null; #locked = false; #hideTimer = 0;

  constructor(root) {
    this.#root = root;
    root.innerHTML = `
      <div class="quiz-card" role="timer" aria-live="polite">
        <svg viewBox="0 0 100 100" class="quiz-ring" aria-hidden="true">
          <circle cx="50" cy="50" r="${R}" class="track"/>
          <circle cx="50" cy="50" r="${R}" class="prog" stroke-dasharray="${C}" stroke-dashoffset="0"/>
        </svg>
        <div class="quiz-num">10</div>
        <div class="quiz-q"></div>
        <div class="quiz-hint">Javob uchun <kbd>Space</kbd> ni bosib turing</div>
      </div>`;
    this.#ring = root.querySelector('.prog');
    this.#num = root.querySelector('.quiz-num');
    this.#q = root.querySelector('.quiz-q');
    this.#hint = root.querySelector('.quiz-hint');
  }

  /** Quiz announced but countdown not started yet (Ustoz is still speaking). */
  get pending() { return this.#quiz; }
  get running() { return !!this.#quiz && this.#t0 > 0 && !this.#locked; }

  arm(quiz) { this.reset(); this.#quiz = quiz; }

  /** Begin the countdown (call when playback is idle). */
  start(onExpire) {
    if (!this.#quiz || this.#t0) return;
    clearTimeout(this.#hideTimer);
    this.#onExpire = onExpire;
    this.#locked = false;
    this.#t0 = performance.now();
    this.#root.hidden = false;
    this.#root.dataset.state = 'running';
    this.#q.textContent = this.#quiz.question;
    this.#hint.innerHTML = 'Javob uchun <kbd>Space</kbd> ni bosib turing';
    const limit = this.#quiz.timeLimitSeconds * 1000;
    const frame = () => {
      const left = Math.max(0, limit - (performance.now() - this.#t0));
      this.#ring.setAttribute('stroke-dashoffset', String(C * (1 - left / limit)));
      this.#num.textContent = String(Math.ceil(left / 1000));
      this.#root.dataset.urgent = left <= 3000 ? 'true' : 'false';
      if (this.#locked) return;
      if (left <= 0) { const cb = this.#onExpire; this.#root.dataset.state = 'expired'; this.#num.textContent = '0'; cb?.(this.#quiz); return; }
      this.#raf = requestAnimationFrame(frame);
    };
    this.#raf = requestAnimationFrame(frame);
  }

  /** Student started answering → freeze the clock. Returns { quizId, answerDelayMs } for the turn. */
  lockIn() {
    if (!this.#quiz) return null;
    this.#locked = true;
    cancelAnimationFrame(this.#raf);
    const delay = this.#t0 ? Math.round(performance.now() - this.#t0) : 0;   // answered before countdown → 0
    this.#root.dataset.state = 'answering';
    this.#hint.textContent = 'Javob qabul qilinmoqda…';
    return { quizId: this.#quiz.id, answerDelayMs: delay };
  }

  /** Verdict from the server. */
  result({ correct, feedback }) {
    if (this.#root.hidden && !this.#quiz) return;
    this.#root.hidden = false;
    this.#root.dataset.state = correct ? 'correct' : 'wrong';
    this.#num.textContent = correct ? '✓' : '✗';
    this.#hint.textContent = feedback || (correct ? "To'g'ri!" : "Noto'g'ri");
    this.#quiz = null;
    this.#hideTimer = setTimeout(() => this.reset(), 3500);
  }

  reset() {
    cancelAnimationFrame(this.#raf);
    this.#quiz = null; this.#t0 = 0; this.#locked = false;
    this.#root.hidden = true;
    this.#root.dataset.state = '';
  }
}
