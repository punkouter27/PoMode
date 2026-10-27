// The analysis wait, filled in with what the pipeline has actually produced so far.
//
// An analysis takes minutes, and each stage leaves an artifact behind the moment it finishes. This
// draws those artifacts as they land, one layer per stage, so the progress card shows the result
// assembling rather than a spinner:
//
//   from the start       the upload's waveform, in the muted colour
//   Separating done      the separated vocal's waveform replaces it, in the key colour
//   PitchTracking done   the transcribed notes, as plain capsules
//   ChordDetecting done  the chord blocks along the bottom, with their symbols
//
// Every layer is a real artifact (the /peaks sketch, notes.json, chords.json), fetched once when the
// SignalR push says its stage completed — never polled. Nothing is coloured by role, because note
// roles do not exist until the modal analysis has run; the finished page's reveal takes it from
// there. The waveform pours in (see "The pour" below); the notes and chords sweep in left to right
// when they arrive. Unless effects are off, which draws each at rest.

import * as prefs from '../shell/prefs.js';
import * as grains from '../shell/grains.js';

const states = new Map();

const SWEEP_MS = 900;
const CHORD_LANE_PX = 22;

function readColours(canvas) {
    const style = getComputedStyle(canvas);
    const read = (name, fallback) => style.getPropertyValue(name).trim() || fallback;
    return {
        lane: read('--pm-lane-bg', '#fbfbfd'),
        border: read('--pm-border', '#d9d9e0'),
        muted: read('--pm-fg-muted', '#4c4c57'),
        text: read('--pm-fg', '#1a1a1a'),
        key: read('--pm-key', '#6750a4'),
        note: read('--pm-accent', '#6750a4'),
        chord: read('--pm-chord-block', '#e4e4ed'),
    };
}

function invalidate(state) {
    if (state.frame === null) {
        state.frame = requestAnimationFrame(() => {
            state.frame = null;
            draw(state);
        });
    }
}

/// 0..1 share of the width a layer that arrived at `at` has swept across.
function sweep(state, at) {
    if (!state.animate || at === 0) {
        return 1;
    }
    return Math.min(1, (performance.now() - at) / SWEEP_MS);
}

// ---- The pour ----
//
// The waveform does not sweep in: its 512 slices drop from above the canvas as rigid bars, fall
// under gravity, bounce off their own true position with a little restitution and come to rest in
// the sketch's exact shape, left to right, in about a second. Each bar is one slice of the /peaks
// sketch — its height is that slice's min/max and never changes in flight — so what lands is the
// waveform, nothing added. When the vocal's sketch replaces the upload's, the same bars spring from
// where they lie into the new shape instead of dropping again. Units are the sketch's own (-1..1),
// so a resize mid-fall changes nothing but the scale.
//
// 'subtle' drops the slices in groups of four (128 bodies) over a shorter spread; with effects off
// the bars are simply drawn at rest.

/// Fall height above the bar's resting place, in sketch units; anything over ~1.1 starts off-canvas.
const DROP = 2.4;
const RESTITUTION = 0.32;
/// Below this rebound speed a bar is at rest.
const REST_SPEED = 0.6;
const SPRING_K = 300;
const SPRING_DAMP = 2 * 0.6 * Math.sqrt(SPRING_K);
/// The most grains in one pour: one per landing body, thinned to this.
const GRAINS = { full: 40, subtle: 14 };

/// A repeatable 0..1 jitter per body, so a redraw of the same sketch falls the same way.
function jitter(index) {
    const x = Math.sin((index + 1) * 12.9898) * 43758.5453;
    return x - Math.floor(x);
}

function pourTiming(level) {
    const subtle = level === 'subtle';
    return {
        group: subtle ? 4 : 1,
        spread: subtle ? 0.22 : 0.35,
        // Chosen so the full drop takes about 0.32 s (0.26 s subtle): g = 2d / t².
        gravity: (2 * DROP) / (subtle ? 0.26 * 0.26 : 0.32 * 0.32),
    };
}

function targetsOf(peaks) {
    const columns = peaks.length / 2;
    const centre = new Float32Array(columns);
    const half = new Float32Array(columns);
    for (let column = 0; column < columns; column++) {
        const low = peaks[column * 2];
        const high = peaks[(column * 2) + 1];
        centre[column] = (high + low) / 2;
        half[column] = Math.max((high - low) / 2, 0);
    }
    return { columns, centre, half };
}

/// Starts the pour for freshly arrived peaks: a drop the first time, a re-settle when bars are
/// already on screen. Returns the landings the first drop makes (for the grains), else null.
function startPour(state, peaks) {
    const target = targetsOf(peaks);
    const previous = state.pour;
    const level = prefs.level();
    if (!state.animate) {
        state.pour = { ...target, targetCentre: target.centre, targetHalf: target.half, mode: 'rest' };
        state.pour.centre = target.centre.slice();
        state.pour.half = target.half.slice();
        state.canvas.dataset.peaksPour = 'settled';
        return null;
    }
    if (previous && previous.columns === target.columns) {
        Object.assign(previous, {
            targetCentre: target.centre,
            targetHalf: target.half,
            mode: 'spring',
            last: performance.now(),
        });
        previous.velocity.fill(0);
        previous.halfVelocity.fill(0);
        state.canvas.dataset.peaksPour = 'falling';
        return null;
    }
    const timing = pourTiming(level);
    const columns = target.columns;
    const pour = {
        columns,
        targetCentre: target.centre,
        targetHalf: target.half,
        centre: new Float32Array(columns),
        half: target.half.slice(),
        velocity: new Float32Array(columns),
        halfVelocity: new Float32Array(columns),
        delay: new Float32Array(columns),
        gravity: timing.gravity,
        mode: 'fall',
        started: performance.now(),
        last: performance.now(),
    };
    const landings = [];
    const groups = Math.ceil(columns / timing.group);
    let tallest = 0;
    for (let column = 0; column < columns; column++) {
        tallest = Math.max(tallest, target.half[column]);
    }
    const every = Math.max(1, Math.round(groups / (GRAINS[level] ?? GRAINS.full)));
    for (let group = 0; group < groups; group++) {
        const share = group / groups;
        const delay = (share * timing.spread) + (jitter(group) * 0.08);
        const lift = DROP + (jitter(group + 977) * 0.6);
        let height = 0;
        for (let k = 0; k < timing.group && (group * timing.group) + k < columns; k++) {
            const column = (group * timing.group) + k;
            pour.delay[column] = delay;
            pour.centre[column] = target.centre[column] + lift;
            height = Math.max(height, target.half[column]);
        }
        if (group % every === 0) {
            landings.push({
                delaySec: delay + Math.sqrt((2 * lift) / timing.gravity),
                share,
                level: tallest > 0 ? height / tallest : 0,
            });
        }
    }
    state.pour = pour;
    state.canvas.dataset.peaksPour = 'falling';
    return landings;
}

/// Puts every bar at rest in its true place: the still final frame.
function settlePour(state) {
    const pour = state.pour;
    if (!pour) {
        return;
    }
    pour.centre.set(pour.targetCentre);
    pour.half.set(pour.targetHalf);
    pour.mode = 'rest';
    state.canvas.dataset.peaksPour = 'settled';
}

/// Advances the pour to now. True while any bar is still moving.
function stepPour(state) {
    const pour = state.pour;
    if (pour.mode === 'rest') {
        return false;
    }
    if (!state.animate) {
        settlePour(state);
        return false;
    }
    const now = performance.now();
    const dt = Math.min((now - pour.last) / 1000, 1 / 30);
    pour.last = now;
    const elapsed = (now - pour.started) / 1000;
    let moving = false;
    for (let column = 0; column < pour.columns; column++) {
        const target = pour.targetCentre[column];
        if (pour.mode === 'fall') {
            if (elapsed < pour.delay[column]) {
                moving = true;
                continue;
            }
            if (pour.centre[column] === target && pour.velocity[column] === 0) {
                continue;
            }
            // Semi-implicit Euler: gravity pulls the bar down (towards smaller sketch values), and
            // its resting place is the floor it bounces off.
            pour.velocity[column] -= pour.gravity * dt;
            pour.centre[column] += pour.velocity[column] * dt;
            if (pour.centre[column] <= target) {
                pour.centre[column] = target;
                const rebound = -pour.velocity[column] * RESTITUTION;
                pour.velocity[column] = rebound < REST_SPEED ? 0 : rebound;
            }
            moving ||= pour.velocity[column] !== 0 || pour.centre[column] !== target;
        } else {
            const halfTarget = pour.targetHalf[column];
            pour.velocity[column] += ((-SPRING_K * (pour.centre[column] - target)) - (SPRING_DAMP * pour.velocity[column])) * dt;
            pour.centre[column] += pour.velocity[column] * dt;
            pour.halfVelocity[column] += ((-SPRING_K * (pour.half[column] - halfTarget)) - (SPRING_DAMP * pour.halfVelocity[column])) * dt;
            pour.half[column] += pour.halfVelocity[column] * dt;
            moving ||= Math.abs(pour.centre[column] - target) > 0.002 || Math.abs(pour.velocity[column]) > 0.02
                || Math.abs(pour.half[column] - halfTarget) > 0.002 || Math.abs(pour.halfVelocity[column]) > 0.02;
        }
    }
    if (!moving) {
        settlePour(state);
    }
    return moving;
}

function draw(state) {
    const canvas = state.canvas;
    const ratio = window.devicePixelRatio || 1;
    const width = Math.max(canvas.clientWidth, 1);
    const height = Math.max(canvas.clientHeight, 1);
    if (canvas.width !== Math.round(width * ratio) || canvas.height !== Math.round(height * ratio)) {
        canvas.width = Math.round(width * ratio);
        canvas.height = Math.round(height * ratio);
    }
    const ctx = canvas.getContext('2d');
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    const colours = state.colours;
    ctx.clearRect(0, 0, width, height);
    ctx.fillStyle = colours.lane;
    ctx.fillRect(0, 0, width, height);

    const duration = state.duration;
    const hasChords = state.chords.length > 0;
    const bodyHeight = height - (hasChords ? CHORD_LANE_PX + 4 : 0);
    const x = seconds => (duration > 0 ? (seconds / duration) * width : 0);
    let animating = false;

    const clipTo = share => {
        ctx.save();
        ctx.beginPath();
        ctx.rect(0, 0, width * share, height);
        ctx.clip();
    };

    if (state.peaks && state.pour) {
        animating ||= stepPour(state);
        const pour = state.pour;
        const middle = bodyHeight / 2;
        const amp = bodyHeight * 0.46;
        ctx.save();
        ctx.beginPath();
        ctx.rect(0, 0, width, bodyHeight);
        ctx.clip();
        ctx.globalAlpha = state.source === 'vocals' ? 0.35 : 0.22;
        ctx.fillStyle = state.source === 'vocals' ? colours.key : colours.muted;
        ctx.beginPath();
        for (let column = 0; column < pour.columns; column++) {
            const left = (column / pour.columns) * width;
            const half = Math.max(pour.half[column], 0);
            ctx.rect(left, middle - ((pour.centre[column] + half) * amp), Math.max(width / pour.columns, 1), Math.max(half * 2 * amp, 1));
        }
        ctx.fill();
        ctx.globalAlpha = 1;
        ctx.restore();
    }

    if (state.notes.length > 0 && duration > 0) {
        const share = sweep(state, state.notesAt);
        animating ||= share < 1;
        clipTo(share);
        const span = Math.max(state.maxPitch - state.minPitch + 1, 12);
        const row = (bodyHeight - 8) / span;
        ctx.fillStyle = colours.note;
        for (const note of state.notes) {
            const left = x(note.startSec);
            const top = 4 + ((state.maxPitch - note.midiPitch) * row);
            ctx.fillRect(left, top, Math.max(x(note.startSec + note.durationSec) - left, 1.5), Math.max(row - 1, 2));
        }
        ctx.restore();
    }

    if (hasChords && duration > 0) {
        const share = sweep(state, state.chordsAt);
        animating ||= share < 1;
        clipTo(share);
        const top = height - CHORD_LANE_PX;
        ctx.font = '12px system-ui, sans-serif';
        ctx.textBaseline = 'middle';
        for (const chord of state.chords) {
            const left = x(chord.startSec);
            const blockWidth = Math.max(x(chord.endSec) - left, 2);
            ctx.fillStyle = colours.chord;
            ctx.fillRect(left + 1, top, Math.max(blockWidth - 2, 1), CHORD_LANE_PX);
            if (blockWidth >= 28) {
                ctx.fillStyle = colours.text;
                ctx.fillText(chord.symbol, left + 5, top + (CHORD_LANE_PX / 2));
            }
        }
        ctx.restore();
    }

    ctx.strokeStyle = colours.border;
    ctx.lineWidth = 1;
    ctx.strokeRect(0.5, 0.5, width - 1, height - 1);

    if (animating) {
        invalidate(state);
    }
}

async function fetchJson(url) {
    try {
        const response = await fetch(url);
        return response.ok ? await response.json() : null;
    } catch {
        return null;
    }
}

/// `dotNet` hears which waveform is on screen ('mix' or 'vocals'), so the caption can say so.
export function init(canvas, dotNet) {
    dispose(canvas);
    const state = {
        canvas,
        dotNet: dotNet ?? null,
        vocalsAsked: false,
        colours: readColours(canvas),
        animate: prefs.allowsMotion(),
        frame: null,
        jobId: null,
        duration: 0,
        peaks: null,
        source: null,
        pour: null,
        notes: [],
        notesAt: 0,
        minPitch: 60,
        maxPitch: 72,
        chords: [],
        chordsAt: 0,
        pending: new Set(),
    };
    // A change of effects level mid-pour: off settles the bars where they belong at once.
    state.unsubscribe = prefs.subscribe(() => {
        state.animate = prefs.allowsMotion();
        invalidate(state);
    });
    state.resize = new ResizeObserver(() => invalidate(state));
    state.resize.observe(canvas);
    state.onTheme = () => {
        state.colours = readColours(canvas);
        invalidate(state);
    };
    state.scheme = window.matchMedia('(prefers-color-scheme: dark)');
    state.scheme.addEventListener('change', state.onTheme);
    state.themeObserver = new MutationObserver(state.onTheme);
    state.themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    states.set(canvas, state);
    invalidate(state);
}

/// Fetches whatever the completed stages have made available and not yet been drawn. Called on each
/// status push; every layer is fetched once (the waveform twice: the upload's, then the vocal's).
export async function refresh(canvas, jobId, completedStages) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    if (state.jobId !== jobId) {
        Object.assign(state, { jobId, peaks: null, source: null, vocalsAsked: false, notes: [], chords: [], duration: 0, pour: null });
        delete canvas.dataset.peaksPour;
    }
    const completed = new Set(completedStages ?? []);
    const base = `api/analysis/${jobId}`;
    const once = async (key, work) => {
        if (state.pending.has(key)) {
            return;
        }
        state.pending.add(key);
        try {
            await work();
        } finally {
            state.pending.delete(key);
        }
    };

    // Asked for once more after separation: the answer is the vocal stem when one was written, and
    // the same upload sketch when the separator wrote none (a dry take, a failed model).
    const askVocals = completed.has('Separating') && !state.vocalsAsked;
    const jobs = [];
    if (!state.peaks || askVocals) {
        state.vocalsAsked ||= askVocals;
        jobs.push(once('peaks', async () => {
            const peaks = await fetchJson(`${base}/peaks`);
            // Only a change of source redraws: the upload's sketch never replaces the vocal's, and
            // the same sketch fetched twice must not sweep in again.
            if (peaks && state.jobId === jobId && peaks.source !== state.source && state.source !== 'vocals') {
                state.peaks = peaks.peaks;
                state.source = peaks.source;
                state.duration = Math.max(state.duration, peaks.durationSec);
                // The first sketch drops and is heard landing, grain by grain of the song itself;
                // the vocal's re-settles the same bars silently.
                const landings = startPour(state, peaks.peaks);
                if (landings) {
                    canvas.dataset.peaksPourSound = grains.pour(jobId, landings) ?? 'none';
                }
                canvas.dataset.waveSource = peaks.source;
                state.dotNet?.invokeMethodAsync('OnPreviewSource', peaks.source);
            }
        }));
    }
    if (completed.has('PitchTracking') && state.notes.length === 0) {
        jobs.push(once('notes', async () => {
            const notes = await fetchJson(`${base}/notes`);
            if (Array.isArray(notes) && notes.length > 0 && state.jobId === jobId) {
                state.notes = notes;
                state.minPitch = Math.min(...notes.map(note => note.midiPitch));
                state.maxPitch = Math.max(...notes.map(note => note.midiPitch));
                state.duration = Math.max(state.duration, ...notes.map(note => note.startSec + note.durationSec));
                state.notesAt = performance.now();
            }
            canvas.dataset.noteCount = String(state.notes.length);
        }));
    }
    if (completed.has('ChordDetecting') && state.chords.length === 0) {
        jobs.push(once('chords', async () => {
            const chords = await fetchJson(`${base}/chords`);
            if (Array.isArray(chords) && chords.length > 0 && state.jobId === jobId) {
                state.chords = chords;
                state.duration = Math.max(state.duration, ...chords.map(chord => chord.endSec));
                state.chordsAt = performance.now();
            }
            canvas.dataset.chordCount = String(state.chords.length);
        }));
    }
    await Promise.all(jobs);
    invalidate(state);
}

// ---- Stage shimmer ----
//
// A glass highlight sweeps once across a stage's row when that stage completes, taking as long as
// the stage really took: log-mapped, so half a second of chord detection is a 0.3 s glint and ten
// minutes of separation a slow 1.5 s pass. The duration is the fact; the row is where it is read.

const SHIMMER_MIN_MS = 300;
const SHIMMER_MAX_MS = 1500;
const SHIMMER_SHORT_SEC = 0.5;
const SHIMMER_LONG_SEC = 600;

/// Sweeps the row `[data-stage=stage]` inside `list`. `seconds` is the stage's measured duration,
/// null when the history has none (the middle of the range is used). Mirrors data-shimmer
/// (running → done) and data-shimmer-ms onto the row.
export function shimmer(list, stage, seconds) {
    const row = list?.querySelector(`[data-stage="${CSS.escape(stage)}"]`);
    if (!row) {
        return;
    }
    const share = Number.isFinite(seconds) && seconds > 0
        ? Math.log(seconds / SHIMMER_SHORT_SEC) / Math.log(SHIMMER_LONG_SEC / SHIMMER_SHORT_SEC)
        : 0.5;
    let ms = SHIMMER_MIN_MS + ((SHIMMER_MAX_MS - SHIMMER_MIN_MS) * Math.min(1, Math.max(0, share)));
    if (prefs.level() === 'subtle') {
        ms *= 0.6;
    }
    row.dataset.shimmerMs = String(Math.round(ms));
    if (!prefs.allowsMotion()) {
        row.dataset.shimmer = 'done';
        return;
    }
    row.style.setProperty('--pm-shimmer-ms', `${Math.round(ms)}ms`);
    row.dataset.shimmer = 'running';
    // A timer rather than animationend: the row may be re-rendered or removed mid-sweep, and a
    // timer on a detached row is harmless where a listener would be left dangling.
    setTimeout(() => {
        row.dataset.shimmer = 'done';
    }, ms + 50);
}

export function dispose(canvas) {
    const state = states.get(canvas);
    if (!state) {
        return;
    }
    if (state.frame !== null) {
        cancelAnimationFrame(state.frame);
    }
    state.resize.disconnect();
    state.scheme.removeEventListener('change', state.onTheme);
    state.themeObserver.disconnect();
    state.unsubscribe();
    states.delete(canvas);
}
