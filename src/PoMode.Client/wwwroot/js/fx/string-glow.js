// Living strings for the open chord jam: each string strum.js plucks vibrates in the chord box that
// shows it, for exactly as long as it sounds.
//
// The motion is the pluck, not a mood laid over it. strum.js announces every string it schedules with
// its start time on the AudioContext clock, and this reads that clock (corrected to what has reached
// the speakers) each frame, so a down-strum's 14 ms spread shows as the strings starting one after
// another, low to high, and an up-stroke as the top four going the other way. Each string is drawn as
// a standing wave between where its sounding length begins (the nut, or the fret wire under the
// finger: ChordBox's data-stop) and the bottom of the box. Its swing follows the same envelope the
// sound does: the Karplus-Strong loop's own decay (voices.js pluckBuffer, a low string rings longer
// than a high one) times pluck()'s gain hold and release, scaled by the level it was struck at, so an
// up-stroke swings less than a down-stroke and everything is still the moment the chord's notes stop.
// Muted strings are never plucked, so they never move.
//
// Its rate comes from the string's real pitch, slowed into what an eye can follow: 4 Hz for the low E,
// rising an octave for every two the pitch rises, so a higher string visibly shivers faster.
//
// One WebGL2 canvas covers the whole grid of boxes; a vertex shader bends one strip per sounding
// string, so a frame is a single instanced draw of at most a dozen strips. WebGPU is not used here
// although chroma-layer.js prefers it: its device arrives asynchronously and costs far more to stand
// up than a handful of strips costs to draw, and the strips must be ready by the first pluck. With
// effects off, or without WebGL2, the sounding strings are drawn lit but straight on each pluck: one
// still frame, no loop. The board's data-strings says ringing while anything moves, still otherwise.

import * as prefs from '../shell/prefs.js';
import { plucks } from '../player/strum.js';

const SEGMENTS = 24;        // per string strip; a sine over 24 steps reads as a curve at this size
const SWING = 3.2;          // CSS px either side at full level: under half the 16 px string gap
const WIDTH = 2.5;          // CSS px, the strip's soft width
const SILENT = 0.01;        // below this share of full swing a string counts as still
const FULL_LEVEL = 0.34;    // strum.js's down-strum level for the bass string: full swing
const MAX_STRINGS = 16;     // strips per draw: two chords' worth overlap at a change

let state = null;

/// Starts listening to strum.js and drawing onto board's canvas.jam-strings. One board at a time.
export function attach(board) {
    dispose();
    const canvas = board?.querySelector?.('canvas.jam-strings');
    if (!canvas) {
        return;
    }
    const s = {
        board, canvas, gl: null, g2: null, lost: false,
        plucks: [], ctx: null, frame: 0, last: 0, colour: [0.4, 0.31, 0.64], cssWidth: 0, cssHeight: 0,
        timers: new Set(), stillKey: null,
    };
    try {
        const gl = canvas.getContext('webgl2', { alpha: true, premultipliedAlpha: true, antialias: false });
        if (gl && setupGl(s, gl)) {
            s.gl = gl;
        }
    } catch { }
    if (!s.gl) {
        try {
            s.g2 = canvas.getContext('2d');
        } catch { }
    }
    s.onPluck = (event) => pluck(s, event.detail);
    s.onStop = (event) => cut(s, event.detail);
    // A page holds only so many WebGL contexts and the browser drops the oldest past that (Practice's
    // take list draws its own). A lost canvas still covers the board, drawn blank, so it is hidden until
    // the context comes back (preventDefault asks for that), when the strings are set up again.
    s.onLost = (event) => {
        event.preventDefault();
        s.lost = true;
        halt(s);
        s.board.dataset.stringsLost = 'yes';
    };
    s.onRestored = () => {
        if (state !== s || !s.gl || !setupGl(s, s.gl)) {
            return;
        }
        s.lost = false;
        delete s.board.dataset.stringsLost;
        size(s);
        restyle(s);
    };
    plucks.addEventListener('pluck', s.onPluck);
    plucks.addEventListener('stop', s.onStop);
    canvas.addEventListener('webglcontextlost', s.onLost);
    canvas.addEventListener('webglcontextrestored', s.onRestored);
    s.unsubscribe = prefs.subscribe(() => restyle(s));
    try {
        s.resize = new ResizeObserver(() => size(s));
        s.resize.observe(board);
    } catch { }
    state = s;
    size(s);
    mark(s, false);
}

export function dispose() {
    const s = state;
    if (!s) {
        return;
    }
    state = null;
    halt(s);
    for (const timer of s.timers) clearTimeout(timer);
    plucks.removeEventListener('pluck', s.onPluck);
    plucks.removeEventListener('stop', s.onStop);
    s.canvas.removeEventListener('webglcontextlost', s.onLost);
    s.canvas.removeEventListener('webglcontextrestored', s.onRestored);
    s.unsubscribe?.();
    s.resize?.disconnect();
    if (s.gl && !s.lost) {
        const gl = s.gl;
        gl.deleteBuffer(s.buffer);
        gl.deleteVertexArray(s.vao);
        gl.deleteProgram(s.program);
        // Hand the context back now rather than when the collector finds the canvas: browsers cap
        // live WebGL contexts per page, and Practice has other canvases.
        gl.getExtension('WEBGL_lose_context')?.loseContext();
    }
    try {
        delete s.board.dataset.strings;
        delete s.board.dataset.stringsLost;
    } catch { }
}

/// Moving strings need effects on and a working GL context; anything else gets the still frame.
function animated(s) {
    return !!s.gl && !s.lost && prefs.allowsMotion();
}

function pluck(s, detail) {
    if (!detail || !(detail.string >= 0)) {
        return;
    }
    s.ctx = detail.ctx;
    s.plucks.push(shape(detail, detail.ctx.sampleRate));
    if (s.plucks.length > 96) {
        s.plucks.splice(0, s.plucks.length - 96);
    }
    if (animated(s)) {
        start(s);
    } else {
        // Plucks arrive up to a quarter second before they sound; the still frame waits for the sound.
        const delay = Math.max(0, (detail.when - detail.ctx.currentTime) * 1000);
        const timer = setTimeout(() => {
            s.timers.delete(timer);
            if (state === s) {
                still(s);
            }
        }, delay);
        s.timers.add(timer);
    }
}

/// Everything about one pluck that does not change while it rings, worked out once.
function shape(detail, sampleRate) {
    const { midi, when, ringFor, level } = detail;
    const freq = 440 * Math.pow(2, (midi - 69) / 12);
    // voices.js pluckBuffer: the loop loses `loss` and passes a two-tap average (|H| = cos(pi f/fs) at
    // a partial of f) once per period, so a partial decays by that product about `freq` times a second.
    const loss = midi < 48 ? 0.998 : midi < 72 ? 0.996 : 0.992;
    const decay = (partial) => freq * Math.log(loss * Math.cos(Math.PI * partial * freq / sampleRate));
    // voices.js pluck(): the buffer is 2.2 s, the gain holds, then falls exponentially to 0.0001.
    const length = Math.min(Math.max(0.08, ringFor), 2.15);
    return {
        string: detail.string,
        chord: detail.chord,
        when,
        hold: Math.max(length - 0.04, 0.02),
        release: length + 0.08,
        end: when + length + 0.12,
        floor: 0.0001 / level,
        strength: Math.min(1, level / FULL_LEVEL),
        fundamental: decay(1),
        overtone: decay(2),
        rate: 2 * Math.PI * 4 * Math.pow(2, (midi - 40) / 24),
    };
}

/// How far through its sound a pluck is at audio time `now`: the gain envelope times the string's own
/// decay, as a share of full swing. Zero before it starts and after it stops.
function envelope(p, now) {
    const t = now - p.when;
    if (t < 0 || now >= p.end) {
        return 0;
    }
    const gain = t <= p.hold ? 1 : t >= p.release ? 0 : Math.pow(p.floor, (t - p.hold) / (p.release - p.hold));
    return p.strength * gain * Math.exp(t * p.fundamental);
}

/// strum.js stopped: every node it started ends at `at`, and so does every string.
function cut(s, detail) {
    const at = detail?.at ?? 0;
    for (const p of s.plucks) {
        p.end = Math.min(p.end, at);
    }
    for (const timer of s.timers) clearTimeout(timer);
    s.timers.clear();
    if (!animated(s)) {
        s.plucks = [];
        still(s);
    }
}

/// The audio clock as heard: the context time of the sample leaving the speakers now, which on
/// Bluetooth can trail currentTime by a tenth of a second or more.
function heard(ctx) {
    try {
        const stamp = ctx.getOutputTimestamp?.();
        if (stamp && stamp.contextTime > 0 && stamp.performanceTime > 0) {
            return Math.min(ctx.currentTime, stamp.contextTime + ((performance.now() - stamp.performanceTime) / 1000));
        }
    } catch { }
    return ctx.currentTime - (ctx.baseLatency || 0);
}

function start(s) {
    if (s.frame) {
        return;
    }
    s.colour = accent(s);
    s.frame = requestAnimationFrame((time) => tick(s, time));
}

function halt(s) {
    if (s.frame) {
        cancelAnimationFrame(s.frame);
        s.frame = 0;
    }
}

function tick(s, time) {
    s.frame = 0;
    if (state !== s || !animated(s) || !s.ctx) {
        return;
    }
    const subtle = prefs.level() === 'subtle';
    // Subtle runs at half rate with the fundamental only: the same facts, half the work.
    if (subtle && time - s.last < 30) {
        s.frame = requestAnimationFrame((next) => tick(s, next));
        return;
    }
    s.last = time;
    const now = heard(s.ctx);
    const swing = SWING * (subtle ? 0.5 : 1);
    const strings = new Map();
    let waiting = false;
    s.plucks = s.plucks.filter((p) => {
        if (now >= p.end) {
            return false;
        }
        if (now < p.when) {
            waiting = true;
            return true;
        }
        const level = envelope(p, now);
        const t = now - p.when;
        const key = (p.chord * 8) + p.string;
        const entry = strings.get(key) ?? { chord: p.chord, string: p.string, first: 0, second: 0, level: 0 };
        // A plucked string starts at full displacement, so each partial runs as a cosine from its start.
        entry.first += level * Math.cos(p.rate * t);
        if (!subtle) {
            entry.second += level * 0.25 * Math.exp(t * (p.overtone - p.fundamental)) * Math.cos(2 * p.rate * t);
        }
        entry.level = Math.max(entry.level, level);
        strings.set(key, entry);
        return true;
    });

    const strips = [];
    const boxes = new Map();
    for (const entry of strings.values()) {
        if (entry.level < SILENT) {
            continue;
        }
        const line = geometry(s, boxes, entry.chord)?.[entry.string];
        if (line) {
            strips.push({ ...line, first: entry.first * swing, second: entry.second * swing, alpha: 0.35 + (0.65 * Math.min(1, entry.level)) });
        }
    }
    draw(s, strips);
    mark(s, strips.length > 0);
    if (waiting || s.plucks.length) {
        s.frame = requestAnimationFrame((next) => tick(s, next));
    }
}

/// The one frame drawn when strings may not move: the strings sounding now, lit and straight.
function still(s) {
    const now = s.ctx ? s.ctx.currentTime : 0;
    s.plucks = s.plucks.filter((p) => now < p.end);
    const sounding = new Map();
    for (const p of s.plucks) {
        if (now >= p.when) {
            sounding.set((p.chord * 8) + p.string, p);
        }
    }
    // A strum re-plucks strings already lit; only a change in which strings sound is worth a redraw.
    const key = [...sounding.keys()].sort((a, b) => a - b).join(',');
    if (key === s.stillKey) {
        return;
    }
    s.stillKey = key;
    s.colour = accent(s);
    const boxes = new Map();
    const strips = [];
    for (const p of sounding.values()) {
        const line = geometry(s, boxes, p.chord)?.[p.string];
        if (line) {
            strips.push({ ...line, first: 0, second: 0, alpha: 0.8 });
        }
    }
    draw(s, strips);
    mark(s, false);
}

/// Effects were turned up or down while the jam plays: carry on in the new mode from where it is.
function restyle(s) {
    if (state !== s) {
        return;
    }
    s.stillKey = null;
    if (animated(s)) {
        if (s.plucks.length) {
            start(s);
        } else {
            draw(s, []);
        }
    } else {
        halt(s);
        still(s);
    }
}

/// Each string of chord `index`'s box in canvas CSS pixels: { x, top, bottom } with top where it
/// starts sounding, or null for a string drawn muted. Read from the SVG each frame it is needed, so
/// a new progression or a reflow is never drawn against stale positions.
function geometry(s, boxes, index) {
    if (boxes.has(index)) {
        return boxes.get(index);
    }
    let lines = null;
    const svg = s.board.querySelectorAll('.jam-chord')[index]?.querySelector('svg.shape-box');
    const box = svg?.viewBox?.baseVal;
    if (svg && box?.width) {
        const area = s.board.getBoundingClientRect();
        const rect = svg.getBoundingClientRect();
        const sx = rect.width / box.width;
        const sy = rect.height / box.height;
        lines = [...svg.querySelectorAll('line.shape-string')].map((line) => Number(line.dataset.fret) < 0 ? null : {
            x: rect.left - area.left + (line.x1.baseVal.value * sx),
            top: rect.top - area.top + (Number(line.dataset.stop) * sy),
            bottom: rect.top - area.top + (line.y2.baseVal.value * sy),
        });
    }
    boxes.set(index, lines);
    return lines;
}

function size(s) {
    const width = s.canvas.clientWidth;
    const height = s.canvas.clientHeight;
    const ratio = Math.min(window.devicePixelRatio || 1, 2);
    s.cssWidth = width;
    s.cssHeight = height;
    s.ratio = ratio;
    s.canvas.width = Math.max(1, Math.round(width * ratio));
    s.canvas.height = Math.max(1, Math.round(height * ratio));
    // Resizing a canvas clears it; a still frame has to be put back.
    if (!animated(s)) {
        s.stillKey = null;
        still(s);
    }
}

function mark(s, ringing) {
    const value = ringing ? 'ringing' : 'still';
    if (s.board.dataset.strings !== value) {
        s.board.dataset.strings = value;
    }
}

/// The theme's accent as linear 0-1 RGB, read when a run of plucks starts rather than per frame.
function accent(s) {
    try {
        const probe = document.createElement('canvas').getContext('2d');
        probe.fillStyle = getComputedStyle(s.board).getPropertyValue('--pm-accent').trim() || '#6750a4';
        const hex = probe.fillStyle;
        if (hex.startsWith('#')) {
            return [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255);
        }
        return (hex.match(/[\d.]+/g)?.map(Number) ?? [103, 80, 164]).slice(0, 3).map((v) => v / 255);
    } catch {
        return s.colour;
    }
}

function draw(s, strips) {
    if (s.gl && !s.lost) {
        drawGl(s, strips);
    } else if (s.g2) {
        draw2d(s, strips);
    }
}

const VERTEX = `#version 300 es
const float PI = 3.14159265;
const int SEGMENTS = ${SEGMENTS};
layout(location = 0) in vec4 aString;   // x, top, bottom, alpha: one per strip
layout(location = 1) in vec2 aSwing;    // the fundamental's and the second partial's swing, CSS px
uniform vec2 uSize;
uniform float uHalf;
out float vAcross;
out float vAlpha;
void main() {
    // Two vertices per step down the string, one either side of it, as a triangle strip.
    float u = float(gl_VertexID / 2) / float(SEGMENTS);
    float side = float(gl_VertexID % 2) * 2.0 - 1.0;
    float y = mix(aString.y, aString.z, u);
    // Both ends are nodes: the fret (or nut) above and the bridge end of the box below.
    float x = aString.x + (aSwing.x * sin(PI * u)) + (aSwing.y * sin(2.0 * PI * u)) + (side * uHalf);
    gl_Position = vec4((x / uSize.x) * 2.0 - 1.0, 1.0 - (y / uSize.y) * 2.0, 0.0, 1.0);
    vAcross = side;
    vAlpha = aString.w;
}`;

const FRAGMENT = `#version 300 es
precision mediump float;
uniform vec3 uColour;
in float vAcross;
in float vAlpha;
out vec4 colour;
void main() {
    // Soft edges stand in for antialiasing, which this context does not ask for.
    float a = vAlpha * (1.0 - smoothstep(0.3, 1.0, abs(vAcross)));
    colour = vec4(uColour * a, a);
}`;

function setupGl(s, gl) {
    const compile = (type, source) => {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        return shader;
    };
    const vertex = compile(gl.VERTEX_SHADER, VERTEX);
    const fragment = compile(gl.FRAGMENT_SHADER, FRAGMENT);
    const program = gl.createProgram();
    gl.attachShader(program, vertex);
    gl.attachShader(program, fragment);
    gl.linkProgram(program);
    gl.deleteShader(vertex);
    gl.deleteShader(fragment);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        gl.deleteProgram(program);
        return false;
    }
    s.program = program;
    s.uniforms = {
        size: gl.getUniformLocation(program, 'uSize'),
        half: gl.getUniformLocation(program, 'uHalf'),
        colour: gl.getUniformLocation(program, 'uColour'),
    };
    s.data = new Float32Array(MAX_STRINGS * 6);
    s.vao = gl.createVertexArray();
    gl.bindVertexArray(s.vao);
    s.buffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, s.buffer);
    gl.bufferData(gl.ARRAY_BUFFER, s.data.byteLength, gl.DYNAMIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 4, gl.FLOAT, false, 24, 0);
    gl.vertexAttribDivisor(0, 1);
    gl.enableVertexAttribArray(1);
    gl.vertexAttribPointer(1, 2, gl.FLOAT, false, 24, 16);
    gl.vertexAttribDivisor(1, 1);
    gl.bindVertexArray(null);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    return true;
}

function drawGl(s, strips) {
    const gl = s.gl;
    gl.viewport(0, 0, s.canvas.width, s.canvas.height);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    const count = Math.min(strips.length, MAX_STRINGS);
    if (!count || !s.cssWidth || !s.cssHeight) {
        return;
    }
    for (let i = 0; i < count; i++) {
        const strip = strips[i];
        s.data.set([strip.x, strip.top, strip.bottom, strip.alpha, strip.first, strip.second], i * 6);
    }
    gl.useProgram(s.program);
    gl.uniform2f(s.uniforms.size, s.cssWidth, s.cssHeight);
    gl.uniform1f(s.uniforms.half, (WIDTH / 2) + 0.5);
    gl.uniform3f(s.uniforms.colour, s.colour[0], s.colour[1], s.colour[2]);
    gl.bindVertexArray(s.vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, s.buffer);
    gl.bufferSubData(gl.ARRAY_BUFFER, 0, s.data, 0, count * 6);
    gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, (SEGMENTS + 1) * 2, count);
    gl.bindVertexArray(null);
}

/// The fallback path only ever draws still frames, but takes the same strips as the shader.
function draw2d(s, strips) {
    const g = s.g2;
    g.setTransform(1, 0, 0, 1, 0, 0);
    g.clearRect(0, 0, s.canvas.width, s.canvas.height);
    g.setTransform(s.ratio, 0, 0, s.ratio, 0, 0);
    g.lineWidth = WIDTH;
    g.lineCap = 'round';
    g.strokeStyle = `rgb(${s.colour.map((c) => Math.round(c * 255)).join(' ')})`;
    for (const strip of strips) {
        g.globalAlpha = strip.alpha;
        g.beginPath();
        for (let i = 0; i <= SEGMENTS; i++) {
            const u = i / SEGMENTS;
            const x = strip.x + (strip.first * Math.sin(Math.PI * u)) + (strip.second * Math.sin(2 * Math.PI * u));
            const y = strip.top + ((strip.bottom - strip.top) * u);
            if (i === 0) g.moveTo(x, y); else g.lineTo(x, y);
        }
        g.stroke();
    }
    g.globalAlpha = 1;
}
