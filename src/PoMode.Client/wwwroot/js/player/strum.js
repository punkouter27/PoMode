// The open chord jam's guitar: strums the exact voicings the chord boxes show, on the Karplus-Strong
// string from voices.js, looping the chords until stopped, or (once) playing them through and ending.
//
// Like the rest of the player, this module knows no music theory. It is handed each chord's MIDI notes
// (low string first, from the server's frets), its length in beats, the tempo, and an eighth-note
// pattern over a 4/4 bar: D strums down, U strums up the top strings, B plucks the bass alone, - rests.
// Notes ring until the chord changes, because the open strings ringing on is the whole sound.
//
// It shares modal-player.js's AudioContext, so the jam and Practice's own chords live on one clock.
//
// Every string it plucks is also announced on `plucks`, with its start time on that clock, so the
// chord box can set the same string vibrating (string-glow.js) in step with what is heard rather
// than with a setTimeout's guess at it.

import { bus, pluck } from './voices.js';
import { getSharedContext } from './modal-player.js';

// A strum is not a block chord: each string sounds a moment after the one before it.
const DOWN_SPREAD = 0.014;
const UP_SPREAD = 0.010;
const UP_STRINGS = 4;
const LOOKAHEAD = 0.25;
const TICK_MS = 50;

let state = null;

// When the last song started playing, on the shared clock: its bar one, whatever chord playback began
// from. Kept after the strumming stops so a take recorded over it can still be trimmed to bar one.
let lastStart = null;

/// Bar one of the last song played, in the shared AudioContext's seconds, or null before any.
export function songStart() {
    return lastStart;
}

/// Fires 'pluck' once per string struck, as it is scheduled (up to LOOKAHEAD early), with detail
/// { ctx, chord, string, midi, when, ringFor, level }: `chord` is the chord's box, `string` counts the box's strings from the
/// lowest, muted ones included, and `when`/`ringFor`/`level` are exactly what voices.js pluck() was
/// given. Fires 'stop' with { ctx, at } when the strumming is cut off.
export const plucks = new EventTarget();

function publish() {
    try {
        document.body.dataset.jam = state ? 'playing' : 'stopped';
        document.body.dataset.jamChord = state ? String(state.chord) : '';
    } catch { }
}

/// chords: [{ midi: number[], beats: number, frets?: number[], box?: number, pattern?: string }];
/// frets (low string first, -1 muted) only number the strings in the pluck events, and box is the
/// chord box that sounds it (its index among the board's boxes; the chord's own index if absent).
/// pattern: 8 characters, for any chord without its own. dotNet gets OnJamChord(index) as each chord
/// sounds. With once, the chords play through a single time and dotNet gets OnJamEnded when the last
/// one has rung; from starts at that chord rather than the first.
export function play(chords, bpm, pattern, dotNet, once = false, from = 0) {
    stop();
    if (!chords?.length || !(bpm > 0)) return;
    const ctx = getSharedContext();
    const out = ctx.createGain();
    out.gain.value = 0.85;
    out.connect(bus(ctx, ctx.destination, 0.24));

    const eighth = 30 / bpm;
    // The slot each chord starts on, in eighths from the top of the loop.
    const starts = [];
    let total = 0;
    for (const chord of chords) {
        starts.push(total);
        total += chord.beats * 2;
        // midi[] holds the sounding strings only; this maps each note back to its string on the box.
        chord.strings = chord.frets?.length
            ? chord.frets.map((fret, s) => (fret < 0 ? -1 : s)).filter(s => s >= 0)
            : chord.midi.map((_, i) => i);
    }
    const first = starts[Math.min(Math.max(0, from), chords.length - 1)];
    state = {
        ctx, out, chords, pattern, eighth, starts, total, dotNet, once, ending: false, chord: -1, slot: first,
        origin: ctx.currentTime + 0.12 - (first * eighth), voices: [], timer: 0, cues: [],
    };
    lastStart = state.origin;
    state.timer = setInterval(schedule, TICK_MS);
    schedule();
    publish();
}

export function stop() {
    if (!state) return;
    clearInterval(state.timer);
    for (const cue of state.cues) clearTimeout(cue);
    const now = state.ctx.currentTime;
    for (const nodes of state.voices) {
        for (const node of nodes) {
            try { node.stop(now + 0.02); } catch { }
        }
    }
    try { state.out.disconnect(); } catch { }
    announce('stop', { ctx: state.ctx, at: now + 0.02 });
    state = null;
    publish();
}

export function dispose() {
    stop();
}

function schedule() {
    const s = state;
    if (!s) return;
    const horizon = s.ctx.currentTime + LOOKAHEAD;
    while (s.origin + (s.slot * s.eighth) < horizon) {
        if (s.once && s.slot >= s.total) {
            finish(s);
            return;
        }
        const when = s.origin + (s.slot * s.eighth);
        const inLoop = s.slot % s.total;
        const index = chordAt(s.starts, inLoop);
        const chord = s.chords[index];
        const ringFor = ((s.starts[index] + (chord.beats * 2)) - inLoop) * s.eighth + 0.25;

        if (inLoop === s.starts[index]) {
            cue(s, index, when);
        }
        // The pattern counts from the top of each bar, whatever chord is under it.
        strike(s, (chord.pattern ?? s.pattern)[inLoop % 8], index, when, ringFor);
        s.slot++;
    }
}

function chordAt(starts, slot) {
    let index = 0;
    while (index + 1 < starts.length && starts[index + 1] <= slot) index++;
    return index;
}

function strike(s, kind, chord, when, ringFor) {
    const { midi, strings } = s.chords[chord];
    if (!midi.length) return;
    // note indexes midi[]; the string sounds and is announced with the same start and level.
    const play = (note, at, level) => {
        s.voices.push(pluck(s.ctx, s.out, midi[note], at, ringFor, level));
        if (s.voices.length > 160) s.voices.splice(0, s.voices.length - 160);
        announce('pluck', { ctx: s.ctx, chord: s.chords[chord].box ?? chord, string: strings[note], midi: midi[note], when: at, ringFor, level });
    };
    if (kind === 'B') {
        play(0, when, 0.42);
    } else if (kind === 'D') {
        midi.forEach((_, i) => play(i, when + (i * DOWN_SPREAD), i === 0 ? 0.34 : 0.27));
    } else if (kind === 'U') {
        // The top strings, highest first, as an up-stroke meets them.
        const count = Math.min(UP_STRINGS, midi.length);
        for (let i = 0; i < count; i++) {
            play(midi.length - 1 - i, when + (i * UP_SPREAD), 0.18);
        }
    }
}

/// Played through once: when the last chord has rung its length, the strumming stops and says so.
function finish(s) {
    if (s.ending) return;
    s.ending = true;
    const delay = Math.max(0, (s.origin + (s.total * s.eighth) + 0.3 - s.ctx.currentTime) * 1000);
    setTimeout(() => {
        if (state !== s) return;
        stop();
        try { s.dotNet?.invokeMethodAsync('OnJamEnded'); } catch { }
    }, delay);
}

function announce(type, detail) {
    try {
        plucks.dispatchEvent(new CustomEvent(type, { detail }));
    } catch { }
}

/// Lights the chord being played as it sounds, not when it is scheduled a quarter second early.
function cue(s, index, when) {
    const delay = Math.max(0, (when - s.ctx.currentTime) * 1000);
    s.cues.push(setTimeout(() => {
        if (state !== s) return;
        s.chord = index;
        publish();
        try { s.dotNet?.invokeMethodAsync('OnJamChord', index); } catch { }
    }, delay));
    if (s.cues.length > 32) s.cues.splice(0, s.cues.length - 32);
}
