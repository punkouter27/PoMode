// The sound of the upload's waveform landing on the wait preview: a short granular "pour" cut from
// the song itself.
//
// The browser holds the uploaded audio only for the moment it is sent, so IntakeCard hands the same
// source to `hold` before the upload starts and names the job with `bind` once the server has one.
// The audio is decoded in the background into a short mono buffer; when the preview's slices land,
// `pour` plays one grain per landing body, cut from the stretch of the song that slice sketches, at
// the moment it lands and as loud as the slice is tall. The sound is the waveform arriving, heard.
//
// A job opened from the library, or one whose audio could not be decoded here, has nothing held for
// it; the grains are then filtered noise whose brightness follows each slice's level, so a loud
// slice still lands brighter than a quiet one. Every grain is gated on fx-prefs through sfx.js.

import * as prefs from './prefs.js';
import { liveContext } from './sfx.js';

/// Decoded at a low rate and mono: these are 40 ms grains under a UI, not playback.
const RATE = 22050;
/// The most of the song kept, from its start. A grain past the end maps proportionally into it.
const KEEP_SEC = 90;
/// A compressed upload is decoded from its first bytes only — an MP3 decoder resyncs on any frame,
/// and this many bytes is a minute and a half even at 320 kbit/s.
const HEAD_BYTES = 4 * 1024 * 1024;
/// Above this a whole-file retry (for a WAV whose head alone would not decode) is not worth it.
const WHOLE_MAX_BYTES = 40 * 1024 * 1024;

const GRAIN_SEC = 0.04;
const PEAK = 0.1;

/// { jobId, samples, durationSec } for the one upload this page made, or null.
let held = null;
let pending = null;

function toBlob(source) {
    if (typeof HTMLInputElement !== 'undefined' && source instanceof HTMLInputElement) {
        return (source.files && source.files[0]) || null;
    }
    if (source instanceof Blob) {
        return source;
    }
    if (source instanceof Uint8Array) {
        return new Blob([source]);
    }
    return null;
}

async function decode(blob) {
    const Offline = window.OfflineAudioContext ?? window.webkitOfflineAudioContext;
    if (!Offline) {
        return null;
    }
    const attempt = async part => {
        try {
            return await new Offline(1, 1, RATE).decodeAudioData(await part.arrayBuffer());
        } catch {
            return null;
        }
    };
    const buffer = await attempt(blob.size > HEAD_BYTES ? blob.slice(0, HEAD_BYTES) : blob)
        ?? (blob.size > HEAD_BYTES && blob.size <= WHOLE_MAX_BYTES ? await attempt(blob) : null);
    if (!buffer || buffer.length === 0) {
        return null;
    }
    const length = Math.min(buffer.length, Math.floor(KEEP_SEC * buffer.sampleRate));
    const samples = new Float32Array(length);
    for (let channel = 0; channel < buffer.numberOfChannels; channel++) {
        const data = buffer.getChannelData(channel);
        for (let i = 0; i < length; i++) {
            samples[i] += data[i] / buffer.numberOfChannels;
        }
    }
    return { samples, rate: buffer.sampleRate, durationSec: length / buffer.sampleRate };
}

/// Called with the upload's source (file input, Blob or recording bytes) before it is sent — the
/// uploader clears a file input. Skipped entirely when sounds are off: nothing would be played.
export function hold(source) {
    held = null;
    pending = null;
    if (!prefs.allowsSound()) {
        return;
    }
    const blob = toBlob(source);
    if (!blob || blob.size === 0) {
        return;
    }
    const entry = { jobId: null, audio: null };
    pending = entry;
    decode(blob).then(audio => {
        if (pending === entry && audio) {
            entry.audio = audio;
            held = entry;
        }
    }).catch(() => { });
}

/// Names the job the held audio became, so a different job's preview never pours this song.
export function bind(jobId) {
    if (pending) {
        pending.jobId = jobId;
    }
}

/// One grain of the held song, Hann-windowed, starting `at` seconds into the context's timeline.
function songGrain(ctx, out, audio, share, level, at) {
    const span = Math.floor(GRAIN_SEC * audio.rate);
    if (audio.samples.length < span) {
        return;
    }
    const start = Math.max(0, Math.min(audio.samples.length - span, Math.floor(share * audio.samples.length)));
    const buffer = ctx.createBuffer(1, span, audio.rate);
    const data = buffer.getChannelData(0);
    let energy = 0;
    for (let i = 0; i < span; i++) {
        const hann = 0.5 - (0.5 * Math.cos((2 * Math.PI * i) / (span - 1)));
        data[i] = audio.samples[start + i] * hann;
        energy += data[i] * data[i];
    }
    // Normalised, then scaled by the slice's height: a near-silent stretch of the song still lands
    // as quietly as its slice is short, not as an inaudible click or a sudden loud one.
    const rms = Math.sqrt(energy / span) || 1;
    const gain = ctx.createGain();
    gain.gain.value = Math.min(PEAK, (PEAK * level * 0.25) / rms);
    const node = ctx.createBufferSource();
    node.buffer = buffer;
    node.connect(gain);
    gain.connect(out);
    node.onended = () => gain.disconnect();
    node.start(at);
}

/// Without the song: a noise grain through a lowpass that opens with the slice's level.
function noiseGrain(ctx, out, level, at, seed) {
    const span = Math.floor(GRAIN_SEC * ctx.sampleRate);
    const buffer = ctx.createBuffer(1, span, ctx.sampleRate);
    const data = buffer.getChannelData(0);
    let state = seed | 1;
    for (let i = 0; i < span; i++) {
        state = (state * 1103515245 + 12345) & 0x7fffffff;
        data[i] = ((state / 0x3fffffff) - 1) * (0.5 - (0.5 * Math.cos((2 * Math.PI * i) / (span - 1))));
    }
    const filter = ctx.createBiquadFilter();
    filter.type = 'lowpass';
    filter.frequency.value = 500 + (5500 * level);
    const gain = ctx.createGain();
    gain.gain.value = PEAK * 0.5 * (0.2 + (0.8 * level));
    const node = ctx.createBufferSource();
    node.buffer = buffer;
    node.connect(filter);
    filter.connect(gain);
    gain.connect(out);
    node.onended = () => gain.disconnect();
    node.start(at);
}

/// Plays the pour for `jobId`. `landings` are { delaySec, share, level }: when a slice lands from
/// now, where in the song it sits (0..1), and how tall it is (0..1). Returns 'song', 'synth' or
/// null (no sound), for the preview to mirror.
export function pour(jobId, landings) {
    if (!prefs.allowsSound() || !Array.isArray(landings) || landings.length === 0) {
        return null;
    }
    const ctx = liveContext();
    if (!ctx) {
        return null;
    }
    try {
        const audio = held && held.jobId === jobId ? held.audio : null;
        const now = ctx.currentTime + 0.01;
        for (let i = 0; i < landings.length; i++) {
            const { delaySec, share, level } = landings[i];
            const at = now + Math.max(0, delaySec);
            if (audio) {
                songGrain(ctx, ctx.destination, audio, share, level, at);
            } else {
                noiseGrain(ctx, ctx.destination, level, at, 7919 * (i + 1));
            }
        }
        return audio ? 'song' : 'synth';
    } catch {
        return null;
    }
}
