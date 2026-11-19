// Mentor emotion controller (contract v2): turns the structured reply into synchronized face + stage behaviour.
//
//   "mentor" header → emotion, rageLevel (flame aura, furrow, flush), shyLevel (blush, hearts, averted eyes),
//                     userMood (empathy override), pivotToTopic (topic pill), eyeAndFaceCues (scheduled per segment)
//   speech chunk    → per-segment emotion; its cues fire at `at × chunk duration` after the chunk becomes audible
//   interrupt       → pending cue timers cancelled

const SEG_PREFIX = { laugh_sfx: '😂', sigh_sfx: '😮‍💨', gasp_sfx: '😲', scoff_sfx: '😏' };
export const MOOD_LABEL = { neutral: '🙂 xotirjam', amused: '😄 kulmoqda', hurt: '😔 xafa', frustrated: '😤 asabiy', stressed: '😟 tashvishda' };
const EMPATHY = new Set(['hurt', 'stressed']);
const OFF_TOPIC_MOODS = new Set(['laughing', 'mocking', 'shy_flustered', 'angry_flame']);

export class MentorController {
  #avatar; #stage; #meter; #meterLabel;
  #header = null;
  #timers = [];
  onMood = (_mood) => {};          // UI hook (mood chip)

  constructor(avatar, stage, { meter, meterLabel }) {
    this.#avatar = avatar;
    this.#stage = stage;
    this.#meter = meter;
    this.#meterLabel = meterLabel;
  }

  /** "mentor" event: the reply's header arrived (before any audio). */
  onHeader(h) {
    this.#clearTimers();
    this.#header = h;
    this.#avatar.inspect(false);
    const empathy = EMPATHY.has(h.userMood) || h.emotion === 'hurt_apologetic';
    if (empathy) {
      // The face softens immediately and the fire goes out — before a single word is heard.
      this.setRage(Math.min(h.rageLevel, 2));
      this.#avatar.setEmotion('hurt_apologetic');
      this.#avatar.cue('apologetic_soft');
      this.#stage.setShy(0);
    } else {
      this.setRage(h.rageLevel);
      this.#avatar.setEmotion(h.emotion);
      this.#stage.setShy(h.shyLevel);
      this.#avatar.setShy(h.shyLevel);
      if (h.shyLevel >= 6) this.#avatar.cue('bashful_look');
    }
    // Topic pill: a mood swing (laugh / mock / fluster / flame) followed by a pivot gets the "back to topic" flash.
    if (h.pivotToTopic) this.#stage.setTopic(h.pivotToTopic, { announce: OFF_TOPIC_MOODS.has(h.emotion) });
    this.onMood(h.userMood || 'neutral');
  }

  /** Called by SpeechPlayer right before a chunk becomes audible. */
  onChunkStart(item) {
    const seg = item.segment;
    if (!seg) return;
    this.#avatar.setState('speaking');
    this.#avatar.setEmotion(seg.type === 'laugh_sfx' ? 'laughing' : seg.emotion);
    if (seg.type === 'laugh_sfx' && seg.intensity >= 3 && seg.firstOfSegment) this.#avatar.cue('laugh_hard');
    if (!seg.firstOfSegment || !this.#header) return;
    const dur = Math.max(300, item.durationMs || 800);
    for (const c of this.#header.eyeAndFaceCues) {
      if (c.segment !== seg.index) continue;
      const at = Math.min(1, Math.max(0, Number(c.at) || 0));
      this.#timers.push(setTimeout(() => this.#avatar.cue(c.cue), at * dur));
    }
  }

  /** Reply finished playing: bashfulness fades, rage stays (it cools on the server over hours). */
  onReplyEnd() {
    this.#stage.coolShy();
    setTimeout(() => this.#avatar.setShy(0), 2500);
  }

  setRage(level) {
    const a = Math.max(0, Math.min(10, Number(level) || 0));
    this.#avatar.setAnger(a);
    this.#stage.setRage(a);
    this.#meter.style.setProperty('--anger', a / 10);
    this.#meter.dataset.level = a >= 8 ? 'high' : a >= 4 ? 'mid' : 'low';
    this.#meterLabel.textContent = `${a}/10`;
  }
  /** v1 name kept for callers that still use it */
  setAnger(level) { this.setRage(level); }

  interrupt() { this.#clearTimers(); this.#avatar.clearCues(); }

  /** Transcript text for a chunk: vocalizations shown as "😂 Xa-xa-xa!" */
  static displayText(item) {
    const t = item.segment?.type;
    return t && t !== 'speech' ? `${SEG_PREFIX[t] ?? ''} ${item.text}`.trim() : item.text;
  }

  #clearTimers() { this.#timers.forEach(clearTimeout); this.#timers = []; }
}
