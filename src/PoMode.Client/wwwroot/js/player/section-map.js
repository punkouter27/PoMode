// "How were the sections found?": the song's bar-by-bar harmonic self-similarity matrix, drawn from
// exactly the numbers SongSectionBuilder cut the section ribbon from, with its method acted out on
// top of it. The checkerboard kernel slides down the diagonal once when the view opens — the same
// slide that produced the novelty curve drawn underneath — and after that it parks on the bar line
// the playhead is nearest, so pressing play walks the kernel through the song. Boundaries are lines
// across the matrix, sections are outlined in their mode's colour on the diagonal.
//
// Nothing here is computed about the music. The matrix, the curve, the boundary bars and the
// sections all arrive from the server; this module maps bars to pixels. The kernel is drawn with the
// same Gaussian taper the server weighted it with, so what is shown sliding is what was measured.
//
// WebGL2 when available: the matrix is one R8 texture and the whole picture is one fragment shader
// pass, which keeps a 256-bar song (65k cells, redrawn as the playhead moves) trivially cheap. Without
// WebGL2 the same picture is painted with 2D canvas calls.
//
// When the playhead walks into a section whose mode moves the key signature, key-caustics.js sends a
// ripple of light across the matrix from that boundary, with one ring per step around the circle of
// fifths, and the lead voice glides the scale degree that changed from its old pitch to its new one.
// Sections share the song's tonic (the server names a mode per section, not a key), so the distance
// is the one between the two modes' key signatures on that tonic.

import * as prefs from '../shell/prefs.js';
import { bus, lead } from './voices.js';
import { createCaustics } from './key-caustics.js';

const states = new Map();

/// Each mode's scale on its tonic, and where its key signature sits on the circle of fifths relative
/// to the major (Ionian) one on the same tonic: Lydian a sharp more, Locrian five flats more. A
/// pentatonic is placed with the scale it is cut from.
const MODES = {
    Lydian: { fifths: 1, steps: [0, 2, 4, 6, 7, 9, 11] },
    Ionian: { fifths: 0, steps: [0, 2, 4, 5, 7, 9, 11] },
    Mixolydian: { fifths: -1, steps: [0, 2, 4, 5, 7, 9, 10] },
    Dorian: { fifths: -2, steps: [0, 2, 3, 5, 7, 9, 10] },
    Aeolian: { fifths: -3, steps: [0, 2, 3, 5, 7, 8, 10] },
    Phrygian: { fifths: -4, steps: [0, 1, 3, 5, 7, 8, 10] },
    Locrian: { fifths: -5, steps: [0, 1, 3, 5, 6, 8, 10] },
};
MODES.MajorPentatonic = MODES.Ionian;
MODES.MinorPentatonic = MODES.Aeolian;

/// Steps around the circle of fifths between two sections' key signatures, 0..6, or null when either
/// section's mode was not named (there is then no key change to show).
export function fifthsBetween(fromMode, toMode) {
    const from = MODES[fromMode];
    const to = MODES[toMode];
    if (!from || !to) {
        return null;
    }
    const span = (((to.fifths - from.fifths) % 12) + 12) % 12;
    return Math.min(span, 12 - span);
}

const MAX_BOUNDARIES = 32;
const MAX_SECTIONS = 16;
const SWEEP_SECONDS = 2.6;
const MAX_DPR = 2;

const VERTEX_SOURCE = `#version 300 es
out vec2 vUv;
void main() {
    vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vUv = corner;
    gl_Position = vec4((corner * 2.0) - 1.0, 0.0, 1.0);
}`;

const FRAGMENT_SOURCE = `#version 300 es
precision highp float;
in vec2 vUv;
uniform sampler2D uSim;
uniform float uBars;
uniform float uLineBars;
uniform vec3 uGround;
uniform vec3 uInk;
uniform vec3 uPlus;
uniform vec3 uMinus;
uniform vec3 uLine;
uniform vec3 uPlayhead;
uniform float uKernel;
uniform float uHalf;
uniform float uPlay;
uniform float uBoundaries[${MAX_BOUNDARIES}];
uniform int uBoundaryCount;
uniform vec2 uSections[${MAX_SECTIONS}];
uniform vec3 uSectionColours[${MAX_SECTIONS}];
uniform int uSectionCount;
out vec4 outColour;

void main() {
    // Bar 0 top-left, as a self-similarity matrix is read.
    vec2 cell = vec2(vUv.x, 1.0 - vUv.y) * uBars;
    float similarity = texture(uSim, (floor(cell) + 0.5) / uBars).r;
    vec3 colour = mix(uGround, uInk, pow(similarity, 1.8));

    for (int i = 0; i < ${MAX_SECTIONS}; i++) {
        if (i >= uSectionCount) break;
        vec2 span = uSections[i];
        if (cell.x >= span.x && cell.x < span.y && cell.y >= span.x && cell.y < span.y) {
            float edge = min(min(cell.x - span.x, span.y - cell.x), min(cell.y - span.x, span.y - cell.y));
            if (edge < uLineBars * 1.6) {
                colour = uSectionColours[i];
            }
        }
    }

    if (uKernel >= 0.0) {
        vec2 offset = cell - vec2(uKernel);
        if (abs(offset.x) < uHalf && abs(offset.y) < uHalf) {
            // Same quadrant (before/before, after/after) scores +, across the line scores -, each
            // weighted by the server's Gaussian taper about the bar line.
            bool same = (offset.x < 0.0) == (offset.y < 0.0);
            vec2 g = offset / (uHalf * 0.5);
            float weight = exp(-0.5 * dot(g, g));
            colour = mix(colour, same ? uPlus : uMinus, 0.62 * weight);
        }
    }

    for (int i = 0; i < ${MAX_BOUNDARIES}; i++) {
        if (i >= uBoundaryCount) break;
        float b = uBoundaries[i];
        if (abs(cell.x - b) < uLineBars || abs(cell.y - b) < uLineBars) {
            colour = mix(colour, uLine, 0.75);
        }
    }

    if (uPlay >= 0.0 && (abs(cell.x - uPlay) < uLineBars || abs(cell.y - uPlay) < uLineBars)) {
        colour = uPlayhead;
    }
    outColour = vec4(colour, 1.0);
}`;

/// Any CSS colour the browser understands (hex, rgb(), the hsl() the key tokens resolve to) as
/// 0..1 RGB, by letting a 1x1 canvas parse it. Called on theme changes, never per frame.
const probe = (() => {
    let context = null;
    return (css, fallback) => {
        try {
            context ??= Object.assign(document.createElement('canvas'), { width: 1, height: 1 })
                .getContext('2d', { willReadFrequently: true });
            context.clearRect(0, 0, 1, 1);
            context.fillStyle = fallback;
            context.fillStyle = css || fallback;
            context.fillRect(0, 0, 1, 1);
            const [r, g, b] = context.getImageData(0, 0, 1, 1).data;
            return [r / 255, g / 255, b / 255];
        } catch {
            return [0.5, 0.5, 0.5];
        }
    };
})();

function readColours(state) {
    const style = getComputedStyle(state.matrix);
    const read = (name, fallback) => style.getPropertyValue(name).trim() || fallback;
    const css = {
        ground: read('--pm-lane-bg', '#fbfbfd'),
        ink: read('--pm-key', '#6750a4'),
        plus: read('--pm-info-edge', '#0284c7'),
        minus: read('--pm-hot-edge', '#db2777'),
        line: read('--pm-fg', '#1a1a1a'),
        muted: read('--pm-fg-muted', '#4c4c57'),
        playhead: read('--pm-playhead', '#e11d48'),
        border: read('--pm-border', '#d9d9e0'),
    };
    state.css = css;
    state.rgb = Object.fromEntries(Object.entries(css).map(([name, value]) => [name, probe(value, '#808080')]));
    // Each section's mode colour, a token name from the server resolved against the live theme.
    state.sectionCss = state.data.sections.map(section => (/^--pm-[a-z-]+$/.test(section.colourToken)
        ? read(section.colourToken, css.muted) : css.muted));
    state.sectionRgb = state.sectionCss.map(colour => probe(colour, css.muted));
}

function decodeMatrix(base64) {
    const binary = atob(base64);
    const cells = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        cells[i] = binary.charCodeAt(i);
    }
    return cells;
}

/// The bar a moment in the song falls in, as a fractional bar position for the crosshair.
function barAt(starts, seconds) {
    if (!(seconds > 0) || starts.length < 2) {
        return -1;
    }
    let low = 0;
    let high = starts.length - 1;
    while (low < high - 1) {
        const mid = (low + high) >> 1;
        if (starts[mid] <= seconds) {
            low = mid;
        } else {
            high = mid;
        }
    }
    const span = starts[low + 1] - starts[low];
    return span > 0 ? Math.min(low + ((seconds - starts[low]) / span), starts.length - 1) : low;
}

function sizeCanvas(canvas) {
    const ratio = Math.min(window.devicePixelRatio || 1, MAX_DPR);
    const width = Math.max(canvas.clientWidth, 1);
    const height = Math.max(canvas.clientHeight, 1);
    const w = Math.round(width * ratio);
    const h = Math.round(height * ratio);
    if (canvas.width !== w || canvas.height !== h) {
        canvas.width = w;
        canvas.height = h;
    }
    return { width, height, ratio };
}

// ---- WebGL2 ----

function compile(gl, type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
        const log = gl.getShaderInfoLog(shader);
        gl.deleteShader(shader);
        throw new Error(log ?? 'shader failed');
    }
    return shader;
}

function initGl(state) {
    const gl = state.matrix.getContext('webgl2', { antialias: false, premultipliedAlpha: false });
    if (!gl) {
        return false;
    }
    try {
        const program = gl.createProgram();
        gl.attachShader(program, compile(gl, gl.VERTEX_SHADER, VERTEX_SOURCE));
        gl.attachShader(program, compile(gl, gl.FRAGMENT_SHADER, FRAGMENT_SOURCE));
        gl.linkProgram(program);
        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
            return false;
        }
        const n = state.bars;
        const texture = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, n, n, 0, gl.RED, gl.UNSIGNED_BYTE, state.cells);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);

        const names = ['uSim', 'uBars', 'uLineBars', 'uGround', 'uInk', 'uPlus', 'uMinus', 'uLine', 'uPlayhead',
            'uKernel', 'uHalf', 'uPlay', 'uBoundaries', 'uBoundaryCount', 'uSections', 'uSectionColours', 'uSectionCount'];
        state.gl = { gl, program, texture, vao: gl.createVertexArray(), uniforms: Object.fromEntries(names.map(name => [name, gl.getUniformLocation(program, name)])) };
        return true;
    } catch {
        return false;
    }
}

function drawGl(state, kernel, play) {
    const { gl, program, texture, vao, uniforms: u } = state.gl;
    const { width } = sizeCanvas(state.matrix);
    gl.viewport(0, 0, state.matrix.width, state.matrix.height);
    gl.useProgram(program);
    gl.bindVertexArray(vao);
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.uniform1i(u.uSim, 0);
    gl.uniform1f(u.uBars, state.bars);
    // Lines stay about one CSS pixel wide at any bar count.
    gl.uniform1f(u.uLineBars, (state.bars / width) * 0.75);
    gl.uniform3fv(u.uGround, state.rgb.ground);
    gl.uniform3fv(u.uInk, state.rgb.ink);
    gl.uniform3fv(u.uPlus, state.rgb.plus);
    gl.uniform3fv(u.uMinus, state.rgb.minus);
    gl.uniform3fv(u.uLine, state.rgb.line);
    gl.uniform3fv(u.uPlayhead, state.rgb.playhead);
    gl.uniform1f(u.uKernel, kernel);
    gl.uniform1f(u.uHalf, state.data.kernelBars / 2);
    gl.uniform1f(u.uPlay, play);

    const boundaries = new Float32Array(MAX_BOUNDARIES);
    state.data.boundaries.slice(0, MAX_BOUNDARIES).forEach((bar, i) => { boundaries[i] = bar; });
    gl.uniform1fv(u.uBoundaries, boundaries);
    gl.uniform1i(u.uBoundaryCount, Math.min(state.data.boundaries.length, MAX_BOUNDARIES));

    const spans = new Float32Array(MAX_SECTIONS * 2);
    const colours = new Float32Array(MAX_SECTIONS * 3);
    state.sectionBars.slice(0, MAX_SECTIONS).forEach(([from, to], i) => {
        spans[i * 2] = from;
        spans[(i * 2) + 1] = to;
        colours.set(state.sectionRgb[i], i * 3);
    });
    gl.uniform2fv(u.uSections, spans);
    gl.uniform3fv(u.uSectionColours, colours);
    gl.uniform1i(u.uSectionCount, Math.min(state.sectionBars.length, MAX_SECTIONS));
    gl.drawArrays(gl.TRIANGLES, 0, 3);
}

// ---- 2D fallback ----

function draw2d(state, kernel, play) {
    const { width, height, ratio } = sizeCanvas(state.matrix);
    const ctx = state.matrix.getContext('2d');
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    const n = state.bars;
    // The matrix as an n×n image, scaled up with smoothing off so each bar stays a crisp square.
    if (!state.image) {
        const image = new ImageData(n, n);
        const [g0, g1, g2] = state.rgb.ground;
        const [i0, i1, i2] = state.rgb.ink;
        for (let cell = 0; cell < n * n; cell++) {
            const t = Math.pow(state.cells[cell] / 255, 1.8);
            image.data[cell * 4] = 255 * (g0 + ((i0 - g0) * t));
            image.data[(cell * 4) + 1] = 255 * (g1 + ((i1 - g1) * t));
            image.data[(cell * 4) + 2] = 255 * (g2 + ((i2 - g2) * t));
            image.data[(cell * 4) + 3] = 255;
        }
        state.image = Object.assign(document.createElement('canvas'), { width: n, height: n });
        state.image.getContext('2d').putImageData(image, 0, 0);
    }
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(state.image, 0, 0, state.matrix.width, state.matrix.height);
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    const scale = width / n;

    state.sectionBars.forEach(([from, to], i) => {
        ctx.strokeStyle = state.sectionCss[i];
        ctx.lineWidth = 2;
        ctx.strokeRect((from * scale) + 1, (from * scale) + 1, ((to - from) * scale) - 2, ((to - from) * scale) - 2);
    });
    if (kernel >= 0) {
        const half = state.data.kernelBars / 2;
        const quadrants = [[-1, -1, state.css.plus], [0, 0, state.css.plus], [-1, 0, state.css.minus], [0, -1, state.css.minus]];
        ctx.globalAlpha = 0.4;
        for (const [qx, qy, colour] of quadrants) {
            ctx.fillStyle = colour;
            ctx.fillRect((kernel + (qx * half)) * scale, (kernel + (qy * half)) * scale, half * scale, half * scale);
        }
        ctx.globalAlpha = 1;
    }
    ctx.strokeStyle = state.css.line;
    ctx.lineWidth = 1;
    for (const bar of state.data.boundaries) {
        ctx.beginPath();
        ctx.moveTo(bar * scale, 0);
        ctx.lineTo(bar * scale, height);
        ctx.moveTo(0, bar * scale);
        ctx.lineTo(width, bar * scale);
        ctx.stroke();
    }
    if (play >= 0) {
        ctx.strokeStyle = state.css.playhead;
        ctx.beginPath();
        ctx.moveTo(play * scale, 0);
        ctx.lineTo(play * scale, height);
        ctx.moveTo(0, play * scale);
        ctx.lineTo(width, play * scale);
        ctx.stroke();
    }
}

// ---- novelty strip ----

/// The novelty curve under the matrix, on the same bar axis: the score the kernel gave each bar
/// line, the chosen boundaries dotted on their peaks, the section letters beneath, and a marker
/// where the kernel is now.
function drawNovelty(state, kernel, play) {
    const canvas = state.novelty;
    if (!canvas) {
        return;
    }
    const { width, height, ratio } = sizeCanvas(canvas);
    const ctx = canvas.getContext('2d');
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    ctx.clearRect(0, 0, width, height);
    const n = state.bars;
    const x = bar => (bar / n) * width;
    const letterRow = 16;
    const plot = height - letterRow - 4;
    const peak = Math.max(...state.data.novelty, 1e-6);
    const y = value => 3 + (plot * (1 - (Math.max(value, 0) / peak)));

    ctx.strokeStyle = state.css.border;
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(0, y(0) + 0.5);
    ctx.lineTo(width, y(0) + 0.5);
    ctx.stroke();

    ctx.strokeStyle = state.css.ink;
    ctx.lineWidth = 2;
    ctx.lineJoin = 'round';
    ctx.beginPath();
    state.data.novelty.forEach((value, bar) => {
        if (bar === 0) {
            ctx.moveTo(x(bar), y(value));
        } else {
            ctx.lineTo(x(bar), y(value));
        }
    });
    ctx.stroke();

    ctx.fillStyle = state.css.line;
    for (const bar of state.data.boundaries) {
        ctx.beginPath();
        ctx.arc(x(bar), y(state.data.novelty[bar] ?? 0), 3.5, 0, Math.PI * 2);
        ctx.fill();
    }
    if (kernel >= 0) {
        ctx.strokeStyle = state.css.plus;
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.moveTo(x(kernel), 0);
        ctx.lineTo(x(kernel), plot + 3);
        ctx.stroke();
    }
    if (play >= 0) {
        ctx.strokeStyle = state.css.playhead;
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(x(play), 0);
        ctx.lineTo(x(play), height);
        ctx.stroke();
    }

    ctx.font = '12px system-ui, sans-serif';
    ctx.textBaseline = 'bottom';
    ctx.textAlign = 'center';
    state.sectionBars.forEach(([from, to], i) => {
        ctx.fillStyle = state.sectionCss[i];
        ctx.fillRect(x(from), height - 3, Math.max(x(to) - x(from) - 1, 1), 3);
        if (x(to) - x(from) >= 14) {
            ctx.fillStyle = state.css.line;
            ctx.fillText(state.data.sections[i].letter, (x(from) + x(to)) / 2, height - 4);
        }
    });
    ctx.textAlign = 'start';
}

// ---- key-change ripple ----

/// The section a moment falls in, or -1 before the first or after the last.
function sectionAt(sections, seconds) {
    return sections.findIndex(section => seconds >= section.startSec && seconds < section.endSec);
}

/// Plays the lead voice gliding the first scale degree the new mode moved, from its old pitch to its
/// new one, around the tonic above middle C. Nothing when sound is off or the tonic is unknown.
function glide(state, fromMode, toMode) {
    const from = MODES[fromMode];
    const to = MODES[toMode];
    if (!prefs.allowsSound() || state.tonic === null || !from || !to) {
        return;
    }
    const degree = from.steps.findIndex((step, i) => step !== to.steps[i]);
    if (degree < 0) {
        return;
    }
    try {
        state.audio ??= new AudioContext();
        const ctx = state.audio;
        ctx.resume?.();
        state.audioBus ??= bus(ctx, ctx.destination, 0.25);
        const base = 60 + state.tonic;
        const start = base + from.steps[degree];
        const end = base + to.steps[degree];
        const when = ctx.currentTime + 0.02;
        const [body, air] = lead(ctx, state.audioBus, start, when, 0.7, 0.12);
        // Hold the old pitch long enough to be heard, then bend to the new one.
        for (const [osc, ratio] of [[body, 1], [air, 2]]) {
            osc.frequency.setValueAtTime(440 * Math.pow(2, (start - 69) / 12) * ratio, when + 0.18);
            osc.frequency.exponentialRampToValueAtTime(440 * Math.pow(2, (end - 69) / 12) * ratio, when + 0.42);
        }
    } catch {
        // No audio hardware, or the browser refused the context: the ripple carries it alone.
    }
}

/// Watches the playhead walk from one section into the next; a move between key signatures starts
/// the ripple at that boundary. A seek is not a crossing: only the step into the following section,
/// within a second of it starting, counts.
function watchSections(state, seconds) {
    const sections = state.data.sections;
    const index = sectionAt(sections, seconds);
    const previous = state.sectionIndex;
    state.sectionIndex = index;
    if (index <= 0 || previous !== index - 1 || seconds - sections[index].startSec > 1) {
        return;
    }
    const steps = fifthsBetween(sections[index - 1].mode, sections[index].mode);
    if (steps === null || sections[index - 1].mode === sections[index].mode) {
        return;
    }
    const bar = state.sectionBars[index][0] / state.bars;
    if (state.caustics) {
        state.matrix.dataset.caustics = String(steps);
        state.caustics.play({ x: bar, y: bar, steps, rgb: state.sectionRgb[index] }, prefs.level(), () => {
            state.matrix.dataset.caustics = 'idle';
        });
    }
    glide(state, sections[index - 1].mode, sections[index].mode);
}

// ---- loop ----

function frame(state, now) {
    state.raf = requestAnimationFrame(next => frame(state, next));
    const source = state.playheadSource;
    const seconds = source ? Number(source.dataset.playhead) : 0;
    const play = barAt(state.data.barStarts, seconds);
    watchSections(state, seconds);

    // The opening slide, then the kernel follows the playhead to the nearest bar line — or, before
    // anything has played, rests on the strongest boundary so the still picture explains itself.
    let kernel;
    const elapsed = (now - state.openedAt) / 1000;
    if (state.animate && elapsed < SWEEP_SECONDS) {
        kernel = (elapsed / SWEEP_SECONDS) * state.bars;
    } else if (play >= 0) {
        kernel = Math.round(play);
    } else {
        kernel = state.restingBar;
    }

    const key = `${kernel.toFixed(2)}|${play.toFixed(2)}|${state.matrix.clientWidth}|${state.themeVersion}`;
    if (key === state.drawnKey) {
        return;
    }
    state.drawnKey = key;
    if (state.gl) {
        drawGl(state, kernel, play);
    } else {
        draw2d(state, kernel, play);
    }
    drawNovelty(state, kernel, play);
    state.matrix.dataset.kernelBar = String(Math.round(kernel));
}

/// Draws `data` (a SongStructureDto) into `matrix`, with the novelty strip in `novelty`.
/// `playheadSource` is the analysis canvas, whose data-playhead the kernel follows. `caustics` is
/// the canvas laid over the matrix for the key-change ripple, and `tonic` the song's tonic pitch
/// class (null when unknown, which silences the glide).
export function init(matrix, novelty, data, playheadSource, caustics, tonic) {
    dispose(matrix);
    const cells = decodeMatrix(data.similarity);
    const bars = data.novelty.length;
    if (bars === 0 || cells.length !== bars * bars) {
        matrix.dataset.renderer = 'none';
        return false;
    }
    const barOf = seconds => {
        const index = data.barStarts.findIndex(start => start >= seconds - 1e-6);
        return index < 0 ? bars : Math.min(index, bars);
    };
    const tallest = data.boundaries.reduce((best, bar) => (best < 0 || data.novelty[bar] > data.novelty[best] ? bar : best), -1);

    const state = {
        matrix,
        novelty,
        data,
        cells,
        bars,
        playheadSource: playheadSource ?? null,
        sectionBars: data.sections.map(section => [barOf(section.startSec), barOf(section.endSec)]),
        restingBar: tallest >= 0 ? tallest : bars / 2,
        animate: prefs.allowsMotion(),
        openedAt: performance.now(),
        drawnKey: null,
        themeVersion: 0,
        gl: null,
        image: null,
        raf: null,
        tonic: Number.isInteger(tonic) ? tonic : null,
        sectionIndex: -1,
        caustics: caustics ? createCaustics(caustics) : null,
        audio: null,
        audioBus: null,
    };
    matrix.dataset.caustics = 'idle';
    // The opening slide and the ripple both read the level as it stands, so a change applies in place.
    state.unsubscribe = prefs.subscribe(() => {
        state.animate = prefs.allowsMotion();
    });
    readColours(state);
    const webgl = initGl(state);
    matrix.dataset.renderer = webgl ? 'webgl2' : '2d';
    matrix.dataset.bars = String(bars);
    matrix.dataset.boundaries = data.boundaries.join(',');

    // Theme flips re-read every colour; the 2D path also rebuilds its cached image.
    state.onTheme = () => {
        readColours(state);
        state.image = null;
        state.themeVersion++;
    };
    state.scheme = window.matchMedia('(prefers-color-scheme: dark)');
    state.scheme.addEventListener('change', state.onTheme);
    state.themeObserver = new MutationObserver(state.onTheme);
    state.themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

    states.set(matrix, state);
    state.raf = requestAnimationFrame(now => frame(state, now));
    return true;
}

export function dispose(matrix) {
    const state = states.get(matrix);
    if (!state) {
        return;
    }
    cancelAnimationFrame(state.raf);
    state.scheme.removeEventListener('change', state.onTheme);
    state.themeObserver.disconnect();
    if (state.gl) {
        state.gl.gl.deleteTexture(state.gl.texture);
        state.gl.gl.deleteProgram(state.gl.program);
    }
    state.unsubscribe();
    state.caustics?.dispose();
    state.audio?.close().catch(() => { });
    states.delete(matrix);
}
