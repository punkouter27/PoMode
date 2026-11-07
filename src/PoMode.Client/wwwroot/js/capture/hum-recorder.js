// Mode Lab hum-along capture: the microphone recorded against the backing player's own clock.
//
// A third capture path beside audio-recorder.js and live-session.js, and for a third reason. Those
// two differ over whether the browser's cleanup helps or hurts the thing downstream; this one is
// the only path where the page is playing audio out of the speakers while the microphone is open,
// so it wants that cleanup badly — without echo cancellation every take comes back with the piano
// bleeding through it, and the pitch tracker faithfully transcribes the piano.
//
// The other job here is alignment. The server tiles the progression the user sang over across the
// take starting at sample zero, so sample zero has to be bar one: the count-in and the gap between
// "microphone is live" and "loop starts" are trimmed off the front. A take that kept them would
// slide every phrase the user sang onto the wrong chord.

import { encodeWav } from './wav.js';
import * as review from '../player/take-plot.js';
import * as sfx from '../shell/sfx.js';
import { detectPitch } from './live-pitch.js';
import * as player from '../player/modal-player.js';
import { scheduleBeats } from '../player/click-track.js';

const FRAME_SIZE = 4096;

let mediaStream = null;
let inputNode = null;
let processorNode = null;
let silentSink = null;
let chunks = [];
let sampleRate = 44100;
let captureStartTime = null;
let isCapturing = false;

// The encoded take is kept after finishCapture so it can be played back before the singer commits
// to saving it — hearing the take against the chords is the only way to judge it, and re-recording
// is cheaper than analyzing something you have not heard.
let lastTake = null;
let lastTakeBuffer = null;
// The take before trimming (its chunks, the downbeat and capture start, the latency measured), so
// moving the voice's timing re-cuts it rather than asking for another take.
let lastRaw = null;
let reviewSource = null;

// Mirrored onto the document like mixer.js/canvas.js/modal-player.js do, so Playwright can assert
// on the recorder without reaching into module internals.
function publishState() {
    try {
        const el = document.body;
        if (!el) return;
        el.dataset.humRecorder = isCapturing ? 'recording' : (reviewSource ? 'reviewing' : 'idle');
        el.dataset.humHasTake = lastTake && lastTake.length > 0 ? 'yes' : 'no';
        el.dataset.humSeconds = capturedSeconds().toFixed(2);
    } catch { }
}

function frameCount() {
    let frames = 0;
    for (const chunk of chunks) frames += chunk.length;
    return frames;
}

/// Seconds of audio captured so far. Polled by the page for its running take timer and its own
/// length cap, so the cap lives in one place rather than being half-enforced here.
export function capturedSeconds() {
    return frameCount() / sampleRate;
}

/// Opens the microphone and starts accumulating. Resolves once audio is actually being captured,
/// so the caller can start the backing loop knowing the take has already begun.
export async function beginCapture(raw = false) {
    if (isCapturing) return true;

    const ctx = player.getSharedContext();
    if (!ctx) return false;
    if (ctx.state === 'suspended') {
        await ctx.resume().catch(() => { });
    }

    mediaStream = await navigator.mediaDevices.getUserMedia({
        audio: {
            channelCount: 1,
            // Deliberately the opposite of the Home recorder's constraints. The backing chords are
            // coming out of these speakers and into this microphone; a take with the piano in it
            // gets analyzed as the piano, which is the one result this feature must not produce.
            // (raw: the delay measurement, where the claps are the signal and noise suppression
            // would flatten the very transients it times.)
            echoCancellation: !raw,
            autoGainControl: !raw,
            noiseSuppression: !raw,
        },
    });

    chunks = [];
    captureStartTime = null;
    sampleRate = ctx.sampleRate;

    inputNode = ctx.createMediaStreamSource(mediaStream);
    processorNode = ctx.createScriptProcessor(FRAME_SIZE, 1, 1);
    processorNode.onaudioprocess = (event) => {
        if (!isCapturing) return;
        const frame = event.inputBuffer.getChannelData(0);
        if (captureStartTime === null) {
            // The callback fires once the frame has already been captured, so the audio it carries
            // began one frame-length ago. That is as precise as a ScriptProcessor gets; what is
            // left is a few milliseconds, far under a sixteenth note at any tempo the lab offers.
            captureStartTime = ctx.currentTime - frame.length / sampleRate;
        }
        chunks.push(new Float32Array(frame));

        // The live plot, when a page has asked for one. Timed from the loop's downbeat rather than
        // from the first captured frame, so the drawn line sits under the chord it was actually sung
        // against — the same origin finishCapture trims the take to. Before the downbeat (the
        // count-in) there is nothing to draw, and pushLive is a no-op if no plot is live.
        const downbeat = player.getPlaybackStartTime();
        if (downbeat !== null) {
            const elapsed = ctx.currentTime - downbeat;
            if (elapsed >= 0) {
                review.pushLive(elapsed, detectPitch(frame, sampleRate), frame, sampleRate);
            }
        }
    };

    // A muted sink: some browsers will not run a ScriptProcessor whose output reaches no
    // destination, and routing the microphone to the speakers instead would be a feedback loop.
    silentSink = ctx.createGain();
    silentSink.gain.value = 0;
    inputNode.connect(processorNode);
    processorNode.connect(silentSink);
    silentSink.connect(ctx.destination);

    isCapturing = true;
    publishState();
    return true;
}

/// Four clicks at the given tempo, resolving when the last one has sounded. Gives the singer the
/// pulse before the loop starts; it lands before bar one, so the trim takes it back off the take.
export function countIn(bpm, beats) {
    sfx.tap();
    const ctx = player.getSharedContext();
    if (!ctx) return Promise.resolve();

    const count = beats > 0 ? beats : 4;
    const endsAt = scheduleBeats(ctx, ctx.currentTime + 0.12, bpm, count);
    return new Promise((resolve) => setTimeout(resolve, Math.max(0, (endsAt - ctx.currentTime) * 1000)));
}

/// Shorter than this after trimming and there is no take, only the tail of a count-in. Encoding it
/// anyway would upload a valid-but-empty WAV and queue a job with nothing in it to analyze.
const MIN_TAKE_SECONDS = 0.5;

/// Closes the microphone and returns the take as 16-bit mono PCM WAV bytes, trimmed so sample zero
/// is the loop's downbeat (or downbeatAt, on the same clock, when something other than the modal
/// player played the backing: the guitar jam passes its song's bar one). Returns an empty array —
/// never null, which does not survive the byte-array marshalling — when nothing usable was captured.
export function finishCapture(downbeatAt, nudgeSeconds = 0) {
    // Felt rather than seen: by the end of a take the singer is usually looking anywhere but at
    // the Stop button, and a phone in the hand can say "got it" without a sound over the recording.
    sfx.thud();
    // Read the downbeat before anything is torn down — the caller stops the player afterwards, and
    // a stopped player no longer knows when its pass through the loop began.
    const playbackStart = typeof downbeatAt === 'number' ? downbeatAt : player.getPlaybackStartTime();
    // Read while the track is still live: its reported input latency goes with it.
    const latency = roundTripLatency();
    const captureStart = captureStartTime;
    isCapturing = false;
    teardown();

    // The raw take is kept, so the timing can be moved after hearing it back without singing again.
    lastRaw = { chunks, frames: frameCount(), playbackStart, captureStart, latency, sampleRate };
    chunks = [];
    return cut(nudgeSeconds);
}

/// The singer sings to the chords as heard, which reach the ear late by the output path (and far later
/// through Bluetooth headphones), and the voice reaches the recorder late again by the input path. A
/// take trimmed only to the scheduled downbeat therefore sits late against the chords by both. The
/// part the browser reports is taken off here; what it does not know (a Bluetooth codec's buffer,
/// usually) is the singer's own nudge.
function roundTripLatency() {
    const ctx = player.getSharedContext();
    let input = 0;
    try { input = mediaStream?.getAudioTracks()[0]?.getSettings().latency || 0; } catch { }
    return (ctx?.baseLatency || 0) + (ctx?.outputLatency || 0) + input;
}

/// Measures this device's whole round trip, the part the browser does not report included: `listen`
/// clicks to catch the pulse, then `claps` more to clap (or tap the laptop) on, through whatever the
/// singer listens on. Each clap is found in the raw recording on the same clock as the clicks; the
/// median of how late they land is the delay a take carries. Resolves with { totalMs, reportedMs,
/// nudgeMs, heard } (nudgeMs is what is left after the reported part, which finishCapture already
/// takes off), or null when no microphone could be opened. heard counts the claps found; under half
/// of them and the result should not be trusted.
export async function measureDelay(bpm = 100, listen = 4, claps = 8) {
    if (isCapturing) return null;
    if (!await beginCapture(true)) return null;
    const ctx = player.getSharedContext();
    const waitedFrom = performance.now();
    while (captureStartTime === null && performance.now() - waitedFrom < 2000) {
        await new Promise((resolve) => setTimeout(resolve, 20));
    }
    const beat = 60 / bpm;
    const start = ctx.currentTime + 0.3;
    const endsAt = scheduleBeats(ctx, start, bpm, listen + claps);
    await new Promise((resolve) => setTimeout(resolve, Math.max(0, (endsAt - ctx.currentTime + 0.7) * 1000)));

    const latency = roundTripLatency();
    const captureStart = captureStartTime;
    const rate = sampleRate;
    const recorded = chunks;
    isCapturing = false;
    teardown();
    chunks = [];
    publishState();
    if (captureStart === null) return { totalMs: 0, reportedMs: 0, nudgeMs: 0, heard: 0 };

    // Energy every 5 ms, and the moments it jumps well above the room's level: the claps.
    const hop = Math.max(1, Math.round(rate * 0.005));
    const energy = [];
    let sum = 0, n = 0;
    for (const chunk of recorded) {
        for (let i = 0; i < chunk.length; i++) {
            sum += chunk[i] * chunk[i];
            if (++n === hop) { energy.push(sum / hop); sum = 0; n = 0; }
        }
    }
    const sorted = [...energy].sort((a, b) => a - b);
    const floor = sorted[Math.floor(sorted.length / 2)] || 0;
    const peak = sorted[sorted.length - 1] || 0;
    const threshold = Math.max(floor * 12, peak * 0.08, 1e-7);
    const onsets = [];
    let quietUntil = -1;
    for (let i = 1; i < energy.length; i++) {
        if (i >= quietUntil && energy[i] > threshold && energy[i - 1] <= threshold) {
            onsets.push(captureStart + (i * hop / rate));
            quietUntil = i + Math.round(0.15 / 0.005); // one clap rings ~150 ms
        }
    }

    const lags = [];
    for (let k = listen; k < listen + claps; k++) {
        const clickAt = start + (k * beat);
        const hit = onsets.find((t) => t >= clickAt - 0.15 && t <= clickAt + 0.6);
        if (hit !== undefined) lags.push(hit - clickAt);
    }
    if (lags.length === 0) return { totalMs: 0, reportedMs: Math.round(latency * 1000), nudgeMs: 0, heard: 0 };
    lags.sort((a, b) => a - b);
    const median = lags[Math.floor(lags.length / 2)];
    return {
        totalMs: Math.round(median * 1000),
        reportedMs: Math.round(latency * 1000),
        nudgeMs: Math.round((median - latency) * 1000),
        heard: lags.length,
    };
}

/// Re-cuts the last take from its raw recording with the voice moved `nudgeSeconds` earlier (later
/// when negative) on top of the measured latency, and makes that the take. Returns the WAV bytes, or
/// an empty array when there is nothing (or too little) left.
export function retrim(nudgeSeconds = 0) {
    stopReview();
    return cut(nudgeSeconds);
}

function cut(nudgeSeconds) {
    const raw = lastRaw;
    if (!raw) return new Uint8Array(0);
    let skip = 0;
    if (raw.playbackStart !== null && raw.captureStart !== null && raw.playbackStart > raw.captureStart) {
        skip = Math.round((raw.playbackStart - raw.captureStart + raw.latency + (Number(nudgeSeconds) || 0)) * raw.sampleRate);
    }
    skip = Math.min(Math.max(0, skip), raw.frames);

    const kept = raw.frames - skip;
    if (kept < MIN_TAKE_SECONDS * raw.sampleRate) {
        // Nothing usable at this timing; the raw take stays, so a smaller nudge can still recover it.
        lastTake = null;
        lastTakeBuffer = null;
        publishState();
        return new Uint8Array(0);
    }

    const bytes = encodeWav(trimLeading(raw.chunks, skip), kept, raw.sampleRate);
    lastTake = bytes;
    lastTakeBuffer = null;
    publishState();
    return bytes;
}

/// Plays the take back with the chords underneath it, both scheduled off one instant so they stay in
/// step. Hearing the voice alone says nothing about whether the phrase landed — the whole question is
/// how it sits against the harmony. Resolves with the take's length in seconds, or 0 if there is none.
export async function reviewTake(backingNotes, loopDuration, dotNetHelper) {
    const ctx = player.getSharedContext();
    if (!ctx || !lastTake || lastTake.length === 0) return 0;
    if (ctx.state === 'suspended') {
        await ctx.resume().catch(() => { });
    }

    if (!lastTakeBuffer) {
        try {
            // decodeAudioData detaches the buffer it is handed, which would leave lastTake empty and
            // unsaveable — so it decodes a copy.
            lastTakeBuffer = await ctx.decodeAudioData(lastTake.slice().buffer);
        } catch {
            return 0;
        }
    }

    stopReview();

    // Far enough ahead that both the recording and the first scheduled chord are comfortably in the
    // future when the audio thread next looks.
    const startAt = ctx.currentTime + 0.2;

    // The panel analyses the buffer that was just decoded rather than decoding its own: one decode
    // per take, and the two readings of the take are guaranteed to be of the same audio.
    review.prepare(lastTakeBuffer, backingNotes, loopDuration);
    // The playhead reads the same clock the audio is scheduled on, so it cannot drift from what is
    // being heard — the same reason the recorder shares the player's AudioContext in the first place.
    review.play(() => ctx.currentTime - startAt);

    reviewSource = ctx.createBufferSource();
    reviewSource.buffer = lastTakeBuffer;
    reviewSource.connect(ctx.destination);
    reviewSource.onended = () => {
        reviewSource = null;
        review.stop();
        // The chords loop indefinitely; the take does not, so the recording's end is what ends the
        // review. Without this the backing would keep playing over silence.
        try { player.stop(); } catch { }
        publishState();
        if (dotNetHelper) {
            try { dotNetHelper.invokeMethodAsync('OnHumReviewEnded'); } catch { }
        }
    };
    reviewSource.start(startAt);

    // Chords only, from the same instant — the take already carries the melody.
    player.play([], backingNotes || [], loopDuration, dotNetHelper, startAt);

    publishState();
    return lastTakeBuffer.duration;
}

/// The guitar jam's playback: the take alone, landing on `startAt` (the song's bar one on the shared
/// clock, which strum.js is already strumming from), so the voice sits on the chords it was sung over.
/// The decode can outlast the song's short lead-in, so a late start skips into the take by the same
/// amount rather than falling behind the chords. dotNet gets OnJamTakeEnded when the take runs out.
/// Resolves with the take's length in seconds, or 0 if it would not decode.
export async function playTake(bytes, startAt, dotNetHelper) {
    const ctx = player.getSharedContext();
    if (!ctx || !bytes || bytes.length === 0) return 0;
    if (ctx.state === 'suspended') {
        await ctx.resume().catch(() => { });
    }
    let buffer;
    try {
        buffer = await ctx.decodeAudioData(bytes.slice().buffer);
    } catch {
        return 0;
    }
    stopReview();
    const now = ctx.currentTime + 0.02;
    const at = typeof startAt === 'number' ? startAt : now;
    reviewSource = ctx.createBufferSource();
    reviewSource.buffer = buffer;
    reviewSource.connect(ctx.destination);
    reviewSource.onended = () => {
        reviewSource = null;
        publishState();
        if (dotNetHelper) {
            try { dotNetHelper.invokeMethodAsync('OnJamTakeEnded'); } catch { }
        }
    };
    if (at >= now) {
        reviewSource.start(at);
    } else {
        reviewSource.start(now, Math.min(now - at, buffer.duration));
    }
    publishState();
    return buffer.duration;
}

export function stopReview() {
    if (reviewSource) {
        // Drop the handler first: this is a deliberate stop, and letting onended fire would report
        // it to Blazor as the take running to its end.
        reviewSource.onended = null;
        try { reviewSource.stop(); } catch { }
        try { reviewSource.disconnect(); } catch { }
        reviewSource = null;
    }
    review.stop();
    try { player.stop(); } catch { }
    publishState();
}

/// Forgets the take — a redo, or a take that has been saved.
export function clearTake() {
    stopReview();
    review.clear();
    lastTake = null;
    lastTakeBuffer = null;
    lastRaw = null;
    publishState();
}

/// Drops the take without encoding it — a redo, or leaving the page mid-record.
export function cancelCapture() {
    isCapturing = false;
    teardown();
    chunks = [];
    publishState();
}

function trimLeading(all, skip) {
    const kept = [];
    let remaining = skip;
    for (const chunk of all) {
        if (remaining >= chunk.length) {
            remaining -= chunk.length;
            continue;
        }
        kept.push(remaining > 0 ? chunk.subarray(remaining) : chunk);
        remaining = 0;
    }
    return kept;
}

function teardown() {
    if (processorNode) {
        processorNode.onaudioprocess = null;
        try { processorNode.disconnect(); } catch { }
        processorNode = null;
    }
    if (inputNode) { try { inputNode.disconnect(); } catch { } inputNode = null; }
    if (silentSink) { try { silentSink.disconnect(); } catch { } silentSink = null; }
    if (mediaStream) {
        // Stopping the tracks is what turns the browser's recording indicator off; leaving them
        // live would keep the microphone open for the rest of the session.
        mediaStream.getTracks().forEach((track) => track.stop());
        mediaStream = null;
    }
    captureStartTime = null;
}

export function dispose() {
    cancelCapture();
    clearTake();
}
