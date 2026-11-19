// Push-to-talk: hold Space (outside text fields) or the on-screen mic button.
const isTyping = (el) =>
  el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable);

/**
 * @param {{ onStart: () => void, onStop: () => void, button?: HTMLElement }} handlers
 * onStart fires once per press (key auto-repeat ignored); onStop fires on release, window blur or tab hide,
 * so a lost keyup can never leave the mic stuck open.
 */
export function bindPushToTalk({ onStart, onStop, button }) {
  let held = false;
  const start = () => { if (held) return; held = true; button?.classList.add('active'); onStart(); };
  const stop = () => { if (!held) return; held = false; button?.classList.remove('active'); onStop(); };

  window.addEventListener('keydown', (e) => {
    if (e.code !== 'Space' || isTyping(document.activeElement) || document.body.classList.contains('modal-open')) return;
    e.preventDefault();                 // no page scroll / button "click"
    if (!e.repeat) start();
  });
  window.addEventListener('keyup', (e) => {
    if (e.code !== 'Space' || !held) return;
    e.preventDefault();
    stop();
  });
  window.addEventListener('blur', stop);
  document.addEventListener('visibilitychange', () => document.hidden && stop());

  if (button) {
    button.addEventListener('pointerdown', (e) => { e.preventDefault(); button.setPointerCapture(e.pointerId); start(); });
    button.addEventListener('pointerup', stop);
    button.addEventListener('pointercancel', stop);
  }
}
