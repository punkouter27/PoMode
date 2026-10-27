// The sound search as a map. The words typed are the centre; each song the server ranked is a glass
// disc, and how far it sits from the centre is how far its CLAP score falls below the best one, over
// the same 0.1 window the server cuts the list at (SoundSearchEndpoints.Spread). The best match sits
// nearest, the last one kept sits on the rim. The angle is the song's key, 30 degrees a semitone
// round from C at twelve o'clock, the same angle its SongTile stripes are drawn at; a song with no key
// takes an angle from its job id, which says nothing and is drawn no differently. The frost (a softer
// edge and grain over the disc) grows as the score drops, so a weak match looks like one.
//
// Nothing here is decoration: the table under the map is the same list in the same order, and
// pointing at a disc or a row lights both. While one is lit for a moment, the song itself is heard -
// a few seconds of its mix, from a third of the way in, panned to where its disc sits.
//
// The arrival is the one motion: the discs drift out from the centre to their scores when an answer
// lands. With effects off they are drawn where they end, once. Everything is redrawn only when
// something changes; there is no frame loop at rest.

import * as prefs from '../shell/prefs.js';

const NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];
const FLATS = ['C', 'Db', 'D', 'Eb', 'E', 'F', 'Gb', 'G', 'Ab', 'A', 'Bb', 'B'];

/// The server keeps songs within this much of the best score (SoundSearchEndpoints.Spread).
const SPREAD = 0.1;

/// Disc radius in CSS pixels, and the gap kept between the outermost disc and the canvas edge.
const DISC = 13;
const MARGIN = 6;

/// How long a disc or row is pointed at before its song is heard, so sweeping across the table
/// does not start eight auditions.
const DWELL_MS = 350;
const AUDITION_SECONDS = 3;

const states = new Map();

const VERTEX = `#version 300 es
layout(location = 0) in vec2 a_corner;
layout(location = 1) in vec2 a_center;
layout(location = 2) in float a_radius;
layout(location = 3) in float a_frost;
layout(location = 4) in vec3 a_color;
layout(location = 5) in float a_lit;
uniform vec2 u_size;
out vec2 v_local;
out float v_frost;
out vec3 v_color;
out float v_lit;
void main() {
    // The quad reaches past the rim far enough for the widest frost and the lit halo.
    float pad = max(1.25, 1.0 + a_frost * 0.6);
    vec2 px = a_center + a_corner * a_radius * pad;
    v_local = a_corner * pad;
    v_frost = a_frost;
    v_color = a_color;
    v_lit = a_lit;
    vec2 clip = px / u_size * 2.0 - 1.0;
    gl_Position = vec4(clip.x, -clip.y, 0.0, 1.0);
}`;

const FRAGMENT = `#version 300 es
precision mediump float;
in vec2 v_local;
in float v_frost;
in vec3 v_color;
in float v_lit;
out vec4 o_color;
float hash(vec2 p) {
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}
void main() {
    float d = length(v_local);
    // The edge softens as the match weakens: a near-certain match is a crisp disc.
    float soft = 0.04 + v_frost * 0.55;
    float body = 1.0 - smoothstep(1.0 - soft, 1.0 + soft, d);
    float grain = (hash(floor(gl_FragCoord.xy)) - 0.5) * v_frost * 0.4;
    float rim = smoothstep(0.72, 0.97, d) * body;
    float spec = pow(max(0.0, 1.0 - length(v_local - vec2(-0.35, -0.42)) / 0.6), 3.0) * (1.0 - v_frost);
    vec3 color = v_color * (0.6 + 0.3 * (1.0 - d)) + vec3(rim * 0.3 + spec * 0.55 + grain);
    float alpha = body * (0.4 + 0.5 * (1.0 - v_frost));
    alpha = max(alpha, rim * 0.85);
    color = mix(color, vec3(1.0), v_lit * 0.2);
    float halo = v_lit * (smoothstep(1.04, 1.1, d) - smoothstep(1.14, 1.22, d));
    color = mix(color, v_color, halo);
    alpha = max(alpha, halo);
    o_color = vec4(clamp(color, 0.0, 1.0) * alpha, alpha);
}`;

function keyIndex(name) {
    const sharp = NAMES.indexOf(name ?? '');
    return sharp >= 0 ? sharp : FLATS.indexOf(name ?? '');
}

/// A stable angle from the job id, for a song with no key. It carries no meaning.
function hashAngle(text) {
    let hash = 2166136261;
    for (let i = 0; i < text.length; i++) {
        hash = Math.imul(hash ^ text.charCodeAt(i), 16777619);
    }
    return ((hash >>> 0) % 360) * Math.PI / 180;
}

/// Places every item on the unit map: `dist` 0..1 from the centre, `angle` from twelve o'clock.
function layout(items) {
    const best = Math.max(...items.map((item) => item.score));
    const perKey = new Map();
    return items.map((item) => {
        const behind = Math.min(1, Math.max(0, (best - item.score) / SPREAD));
        const key = keyIndex(item.tonic);
        let angle = key >= 0 ? key * Math.PI / 6 : hashAngle(item.jobId);
        if (key >= 0) {
            // Two songs in one key would sit on one spoke; the second and later fan out beside it.
            const seen = perKey.get(key) ?? 0;
            perKey.set(key, seen + 1);
            angle += (seen % 2 === 0 ? 1 : -1) * Math.ceil(seen / 2) * 0.16;
        }
        return { ...item, dist: 0.25 + (0.75 * behind), frost: behind, angle };
    });
}

function parseColor(text) {
    const value = (text ?? '').trim();
    const hex = /^#([0-9a-f]{6})$/i.exec(value);
    if (hex) {
        const n = parseInt(hex[1], 16);
        return [(n >> 16) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
    }
    const rgb = /^rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)/i.exec(value);
    return rgb ? [rgb[1] / 255, rgb[2] / 255, rgb[3] / 255] : null;
}

/// The mode's own colour token, so a disc reads like its row's tile; the pentatonics have no token
/// and take the accent.
function colorOf(style, mode) {
    return parseColor(style.getPropertyValue(`--pm-mode-${mode}`))
        ?? parseColor(style.getPropertyValue('--pm-accent'))
        ?? [0.4, 0.31, 0.64];
}

function compile(gl, type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
        gl.deleteShader(shader);
        return null;
    }
    return shader;
}

function createGl(canvas) {
    let gl = null;
    try {
        gl = canvas.getContext('webgl2', { premultipliedAlpha: true, antialias: true });
    } catch {
        return null;
    }
    if (!gl) {
        return null;
    }
    const vs = compile(gl, gl.VERTEX_SHADER, VERTEX);
    const fs = compile(gl, gl.FRAGMENT_SHADER, FRAGMENT);
    if (!vs || !fs) {
        return null;
    }
    const program = gl.createProgram();
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.linkProgram(program);
    gl.deleteShader(vs);
    gl.deleteShader(fs);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        return null;
    }
    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);
    const quad = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, quad);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);

    // One interleaved instance record: centre xy, radius, frost, rgb, lit = 8 floats.
    const instances = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, instances);
    const stride = 8 * 4;
    const attribute = (location, size, offset) => {
        gl.enableVertexAttribArray(location);
        gl.vertexAttribPointer(location, size, gl.FLOAT, false, stride, offset * 4);
        gl.vertexAttribDivisor(location, 1);
    };
    attribute(1, 2, 0);
    attribute(2, 1, 2);
    attribute(3, 1, 3);
    attribute(4, 3, 4);
    attribute(5, 1, 7);
    gl.bindVertexArray(null);

    gl.enable(gl.BLEND);
    gl.blendFunc(gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
    return { gl, program, vao, quad, instances, sizeLocation: gl.getUniformLocation(program, 'u_size') };
}

/// The map's geometry in CSS pixels for the canvas's current size.
function geometry(state) {
    const width = state.canvas.clientWidth;
    const height = state.canvas.clientHeight;
    const radius = Math.max(10, (Math.min(width, height) / 2) - DISC - MARGIN);
    return { width, height, cx: width / 2, cy: height / 2, radius };
}

function pointOf(geo, item, t) {
    const dist = item.dist * geo.radius * t;
    return { x: geo.cx + (Math.sin(item.angle) * dist), y: geo.cy - (Math.cos(item.angle) * dist) };
}

/// Moves the focusable discs to where their glass is drawn; they carry the labels and the keyboard.
function placeAnchors(state) {
    const geo = geometry(state);
    for (const item of state.items) {
        const anchor = state.anchors.get(item.jobId);
        if (!anchor) {
            continue;
        }
        const { x, y } = pointOf(geo, item, 1);
        anchor.style.setProperty('--pm-x', `${x.toFixed(1)}px`);
        anchor.style.setProperty('--pm-y', `${y.toFixed(1)}px`);
        item.pan = Math.max(-1, Math.min(1, (x - geo.cx) / geo.radius));
        anchor.dataset.pan = item.pan.toFixed(2);
    }
}

function draw(state, t = 1) {
    const view = state.gl;
    if (!view) {
        return;
    }
    const { gl } = view;
    const geo = geometry(state);
    const dpr = window.devicePixelRatio || 1;
    const width = Math.max(1, Math.round(geo.width * dpr));
    const height = Math.max(1, Math.round(geo.height * dpr));
    if (state.canvas.width !== width || state.canvas.height !== height) {
        state.canvas.width = width;
        state.canvas.height = height;
    }
    const style = getComputedStyle(state.root);
    const muted = parseColor(style.getPropertyValue('--pm-fg-muted')) ?? [0.5, 0.5, 0.5];

    // The query first, as a small crisp mark at the centre, then the songs.
    const data = new Float32Array((state.items.length + 1) * 8);
    data.set([geo.cx * dpr, geo.cy * dpr, 4 * dpr, 0, ...muted, 0], 0);
    state.items.forEach((item, i) => {
        const { x, y } = pointOf(geo, item, t);
        const color = colorOf(style, item.mode);
        data.set([x * dpr, y * dpr, DISC * dpr, item.frost, ...color, item.jobId === state.lit ? 1 : 0], (i + 1) * 8);
    });

    gl.viewport(0, 0, width, height);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.useProgram(view.program);
    gl.uniform2f(view.sizeLocation, width, height);
    gl.bindBuffer(gl.ARRAY_BUFFER, view.instances);
    gl.bufferData(gl.ARRAY_BUFFER, data, gl.DYNAMIC_DRAW);
    gl.bindVertexArray(view.vao);
    gl.drawArraysInstanced(gl.TRIANGLE_STRIP, 0, 4, state.items.length + 1);
    gl.bindVertexArray(null);
}

/// The drift: discs travel out from the query to their scores, overshooting a touch and settling,
/// once per answer. Subtle effects make it shorter; off draws the end state.
function drift(state) {
    cancelAnimationFrame(state.frame);
    const level = prefs.level();
    if (level === 'off' || !state.gl) {
        state.root.dataset.soundDrift = 'done';
        draw(state, 1);
        return;
    }
    const duration = level === 'subtle' ? 380 : 750;
    const start = performance.now();
    state.root.dataset.soundDrift = 'running';
    const step = (now) => {
        const p = Math.min(1, (now - start) / duration);
        // A damped spring's step response: past 1 once, then home.
        const t = 1 - (Math.exp(-6 * p) * Math.cos(9 * p));
        draw(state, p >= 1 ? 1 : t);
        if (p < 1) {
            state.frame = requestAnimationFrame(step);
        } else {
            state.root.dataset.soundDrift = 'done';
        }
    };
    state.frame = requestAnimationFrame(step);
}

function audioContext(state) {
    try {
        if (!state.audio) {
            const Ctor = window.AudioContext ?? window.webkitAudioContext;
            if (!Ctor) {
                return null;
            }
            state.audio = new Ctor();
        }
        if (state.audio.state === 'suspended') {
            state.audio.resume().catch(() => { });
        }
        return state.audio.state === 'closed' ? null : state.audio;
    } catch {
        return null;
    }
}

function stopAudition(state) {
    clearTimeout(state.dwell);
    const playing = state.audition;
    state.audition = null;
    delete state.root.dataset.audition;
    if (!playing) {
        return;
    }
    clearTimeout(playing.timer);
    try {
        const now = playing.context.currentTime;
        playing.gain.gain.cancelScheduledValues(now);
        playing.gain.gain.setTargetAtTime(0, now, 0.05);
    } catch {
        // A closed context has nothing left to fade.
    }
    setTimeout(() => {
        playing.element.pause();
        playing.element.removeAttribute('src');
        playing.element.load();
        playing.source.disconnect();
        playing.gain.disconnect();
    }, 250);
}

/// A few seconds of the song itself, from a third of the way in, panned to its disc.
function audition(state, item) {
    stopAudition(state);
    if (!prefs.allowsSound()) {
        return;
    }
    const context = audioContext(state);
    if (!context) {
        return;
    }
    try {
        const element = new Audio();
        element.preload = 'auto';
        element.src = `api/analysis/${encodeURIComponent(item.jobId)}/stems/mix`;
        const source = context.createMediaElementSource(element);
        const panner = context.createStereoPanner();
        panner.pan.value = item.pan ?? 0;
        const gain = context.createGain();
        gain.gain.value = 0;
        source.connect(panner).connect(gain).connect(context.destination);
        const playing = { context, element, source, gain, timer: 0 };
        state.audition = playing;
        state.root.dataset.audition = item.jobId;
        element.addEventListener('loadedmetadata', () => {
            if (state.audition !== playing) {
                return;
            }
            if (Number.isFinite(element.duration) && element.duration > AUDITION_SECONDS * 2) {
                element.currentTime = element.duration / 3;
            }
            element.play().then(() => {
                const now = context.currentTime;
                gain.gain.setTargetAtTime(0.5, now, 0.08);
                playing.timer = setTimeout(() => {
                    if (state.audition === playing) {
                        stopAudition(state);
                    }
                }, AUDITION_SECONDS * 1000);
            }).catch(() => {
                if (state.audition === playing) {
                    stopAudition(state);
                }
            });
        }, { once: true });
        element.addEventListener('error', () => {
            if (state.audition === playing) {
                stopAudition(state);
            }
        }, { once: true });
    } catch {
        stopAudition(state);
    }
}

/// Lights one song on the map and in the table together, or none with null.
function light(state, jobId) {
    if (state.lit === jobId) {
        return;
    }
    const rows = state.root.parentElement;
    rows?.querySelectorAll('tr[data-sound-lit]').forEach((row) => row.removeAttribute('data-sound-lit'));
    state.anchors.forEach((anchor) => anchor.removeAttribute('data-sound-lit'));
    state.lit = jobId;
    stopAudition(state);
    if (jobId) {
        state.root.dataset.soundLit = jobId;
        state.anchors.get(jobId)?.setAttribute('data-sound-lit', '');
        rows?.querySelector(`tr[data-job="${CSS.escape(jobId)}"]`)?.setAttribute('data-sound-lit', '');
        const item = state.items.find((candidate) => candidate.jobId === jobId);
        if (item) {
            state.dwell = setTimeout(() => audition(state, item), DWELL_MS);
        }
    } else {
        delete state.root.dataset.soundLit;
    }
    if (state.root.dataset.soundDrift !== 'running') {
        draw(state);
    }
}

/// The song a pointer or focus event is about: a disc on the map or a ranked row of the table.
function jobOf(state, target) {
    const element = target instanceof Element ? target : null;
    const disc = element?.closest('.sound-disc');
    if (disc && state.root.contains(disc)) {
        return disc.dataset.job ?? null;
    }
    const row = element?.closest('tr[data-job]');
    const id = row?.dataset.job ?? null;
    return id && state.anchors.has(id) ? id : null;
}

/// Draws the map for these ranked songs into `root`, which holds a canvas and one `.sound-disc`
/// link per song (data-job). The table beside it is found as root's parent.
export function show(root, items) {
    if (!root) {
        return;
    }
    hide(root);
    const canvas = root.querySelector('canvas');
    if (!canvas || !Array.isArray(items) || items.length === 0) {
        return;
    }
    const state = {
        root,
        canvas,
        items: layout(items),
        anchors: new Map([...root.querySelectorAll('.sound-disc')].map((anchor) => [anchor.dataset.job, anchor])),
        gl: createGl(canvas),
        lit: null,
        frame: 0,
        dwell: 0,
        audio: null,
        audition: null,
        listeners: [],
    };
    states.set(root, state);
    root.dataset.gl = state.gl ? 'webgl2' : 'none';
    placeAnchors(state);

    const scope = root.parentElement ?? root;
    const on = (target, type, handler) => {
        target.addEventListener(type, handler);
        state.listeners.push(() => target.removeEventListener(type, handler));
    };
    on(scope, 'pointerover', (event) => light(state, jobOf(state, event.target)));
    on(scope, 'pointerleave', () => light(state, null));
    on(scope, 'focusin', (event) => light(state, jobOf(state, event.target)));
    on(scope, 'focusout', (event) => {
        if (!scope.contains(event.relatedTarget)) {
            light(state, null);
        }
    });

    // The colours are theme tokens: a theme switch redraws in the new ones.
    const theme = new MutationObserver(() => draw(state));
    theme.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    state.listeners.push(() => theme.disconnect());
    try {
        const dark = window.matchMedia('(prefers-color-scheme: dark)');
        const redraw = () => draw(state);
        dark.addEventListener('change', redraw);
        state.listeners.push(() => dark.removeEventListener('change', redraw));
    } catch {
        // No matchMedia: the theme stamp above still covers the header toggle.
    }

    if (typeof ResizeObserver !== 'undefined') {
        const resize = new ResizeObserver(() => {
            placeAnchors(state);
            if (root.dataset.soundDrift !== 'running') {
                draw(state);
            }
        });
        resize.observe(canvas);
        state.listeners.push(() => resize.disconnect());
    }

    // Effects switched off mid-drift land the discs at once; a muted user hears no more auditions.
    state.listeners.push(prefs.subscribe(() => {
        if (!prefs.allowsMotion() && root.dataset.soundDrift === 'running') {
            cancelAnimationFrame(state.frame);
            root.dataset.soundDrift = 'done';
            draw(state);
        }
        if (!prefs.allowsSound()) {
            stopAudition(state);
        }
    }));

    drift(state);
}

/// Tears the map down: no frame, no audio, no listeners, and the GPU context given back. With no
/// root (the map's element is already gone from the page) every map is torn down.
export function hide(root) {
    const targets = root && states.has(root) ? [states.get(root)] : root ? [] : [...states.values()];
    for (const state of targets) {
        cancelAnimationFrame(state.frame);
        light(state, null);
        stopAudition(state);
        for (const remove of state.listeners) {
            remove();
        }
        if (state.audio) {
            state.audio.close().catch(() => { });
        }
        if (state.gl) {
            const { gl } = state.gl;
            gl.deleteBuffer(state.gl.quad);
            gl.deleteBuffer(state.gl.instances);
            gl.deleteVertexArray(state.gl.vao);
            gl.deleteProgram(state.gl.program);
            gl.getExtension('WEBGL_lose_context')?.loseContext();
        }
        delete state.root.dataset.soundDrift;
        states.delete(state.root);
    }
}
