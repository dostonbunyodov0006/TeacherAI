// Microphone → 16 kHz mono PCM16 WAV, plus live voice analytics for the listening UI and cut-off detection.
//
//   • pre-roll: the mic graph stays warm after the first press and keeps the last ~250 ms in a ring buffer, so a
//     word started a hair before Space went down isn't clipped
//   • onLevel(rms, voiced): every audio block (~2.7 ms) → avatar micro-nods + the live mic ring
//   • stop() returns durationMs, voicedMs (time above an adaptive noise floor) and clippedEnd (still loud in the
//     last ~200 ms → Space was released mid-word)
const TARGET_RATE = 16000;
const MAX_SECONDS = 60;
const PREROLL_MS = 250;
const TAIL_MS = 200;

const workletSrc = `
class Tap extends AudioWorkletProcessor {
  process(inputs) { const ch = inputs[0][0]; if (ch) this.port.postMessage(ch.slice(0)); return true; }
}
registerProcessor('tap', Tap);`;

export class Recorder {
  #ctx; #stream; #node; #source;
  #chunks = []; #recording = false; #started = 0;
  #preroll = []; #prerollLen = 0;
  #floor = 0.01;                 // adaptive noise floor (RMS)
  #voicedSamples = 0; #tail = [];
  onLevel = (_rms, _voiced) => {};

  get ready() { return !!this.#ctx; }

  /** Ask for mic permission once and keep the graph warm, so pressing Space starts instantly. */
  async init() {
    if (this.#ctx) return;
    this.#stream = await navigator.mediaDevices.getUserMedia({
      audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
    });
    this.#ctx = new AudioContext({ latencyHint: 'interactive' });
    const url = URL.createObjectURL(new Blob([workletSrc], { type: 'application/javascript' }));
    await this.#ctx.audioWorklet.addModule(url);
    this.#source = this.#ctx.createMediaStreamSource(this.#stream);
    this.#node = new AudioWorkletNode(this.#ctx, 'tap');
    this.#node.port.onmessage = (e) => this.#onBlock(e.data);
    this.#source.connect(this.#node);
  }

  #onBlock(block) {
    let sum = 0; for (let i = 0; i < block.length; i++) sum += block[i] * block[i];
    const rms = Math.sqrt(sum / block.length);
    if (!this.#recording) {
      // idle: learn the room's noise floor + keep a short pre-roll
      this.#floor = this.#floor * 0.995 + Math.min(rms, 0.05) * 0.005;
      this.#preroll.push(block); this.#prerollLen += block.length;
      const max = this.#ctx.sampleRate * PREROLL_MS / 1000;
      while (this.#prerollLen > max) this.#prerollLen -= this.#preroll.shift().length;
      return;
    }
    this.#chunks.push(block);
    const voiced = rms > Math.max(0.012, this.#floor * 3.2);
    if (voiced) this.#voicedSamples += block.length;
    this.#tail.push(voiced ? 1 : 0);
    const tailBlocks = Math.ceil(this.#ctx.sampleRate * TAIL_MS / 1000 / block.length);
    while (this.#tail.length > tailBlocks) this.#tail.shift();
    this.onLevel(rms, voiced);
  }

  async start() {
    await this.init();
    if (this.#ctx.state === 'suspended') await this.#ctx.resume();
    this.#chunks = [...this.#preroll];               // include the pre-roll
    this.#voicedSamples = 0; this.#tail = [];
    this.#recording = true;
    this.#started = performance.now();
  }

  /** Returns { wavBase64, durationMs, voicedMs, clippedEnd } or null if nothing was captured. */
  stop() {
    if (!this.#recording) return null;
    this.#recording = false;
    const durationMs = Math.round(performance.now() - this.#started);
    const voicedMs = Math.round(this.#voicedSamples / this.#ctx.sampleRate * 1000);
    const clippedEnd = this.#tail.length > 0 && this.#tail.filter(Boolean).length / this.#tail.length > 0.6;
    this.onLevel(0, false);
    const length = this.#chunks.reduce((n, c) => n + c.length, 0);
    if (!length) return { wavBase64: null, durationMs, voicedMs, clippedEnd };
    const pcm = new Float32Array(length);
    let o = 0; for (const c of this.#chunks) { pcm.set(c, o); o += c.length; }
    const down = downsample(pcm, this.#ctx.sampleRate, TARGET_RATE).subarray(0, TARGET_RATE * MAX_SECONDS);
    return { wavBase64: toBase64(encodeWav(down, TARGET_RATE)), durationMs, voicedMs, clippedEnd };
  }
}

function downsample(input, from, to) {
  if (from === to) return input;
  const ratio = from / to, out = new Float32Array(Math.floor(input.length / ratio));
  for (let i = 0; i < out.length; i++) {           // box-filter average = cheap anti-alias
    const s = Math.floor(i * ratio), e = Math.min(Math.floor((i + 1) * ratio), input.length);
    let sum = 0; for (let j = s; j < e; j++) sum += input[j];
    out[i] = sum / (e - s || 1);
  }
  return out;
}

function encodeWav(samples, rate) {
  const buf = new ArrayBuffer(44 + samples.length * 2), v = new DataView(buf);
  const str = (o, s) => [...s].forEach((ch, i) => v.setUint8(o + i, ch.charCodeAt(0)));
  str(0, 'RIFF'); v.setUint32(4, 36 + samples.length * 2, true); str(8, 'WAVE');
  str(12, 'fmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
  v.setUint32(24, rate, true); v.setUint32(28, rate * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true);
  str(36, 'data'); v.setUint32(40, samples.length * 2, true);
  for (let i = 0; i < samples.length; i++) {
    const s = Math.max(-1, Math.min(1, samples[i]));
    v.setInt16(44 + i * 2, s < 0 ? s * 0x8000 : s * 0x7fff, true);
  }
  return new Uint8Array(buf);
}

function toBase64(bytes) {
  let bin = ''; const step = 0x8000;
  for (let i = 0; i < bytes.length; i += step) bin += String.fromCharCode.apply(null, bytes.subarray(i, i + step));
  return btoa(bin);
}
