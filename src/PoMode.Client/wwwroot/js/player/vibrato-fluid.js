// The voice's vibrato as moving fluid under the take plot, measured rather than imagined.
//
// The comet in take-plot.js already says where the voice is. What it cannot say is how the voice is
// holding the note: a straight tone and a wide, fast vibrato on the same pitch draw the same dot,
// because a 2D polyline at the capture rate cannot resolve a 6 Hz wobble. So the wobble is measured
// here — rate in Hz and depth in cents, from the last half second of a fine pitch track — and fed
// into a small stable-fluids simulation as the force it injects at the voice's leading edge. A steady
// note pushes a smooth laminar ribbon; a wide vibrato swings the push up and down and spins it, and
// the dye curls into eddies. Nothing here is drawn for its own sake: with no measured motion the
// fluid does nothing but carry the ribbon, and with no take running it stops and keeps its last frame.
//
// The colour is the playhead's, the same as the comet's — it marks where the voice is, never how it
// sits against the chord, which stays the agree/pass pair's job and the analyzer's verdict.
//
// This one does earn a GPU context, unlike the plot it sits under: an incompressible solve is a
// divergence, a few dozen Jacobi sweeps and two advections over every cell, every frame, which a 2D
// canvas cannot do. WebGPU compute where available, WebGL2 fragment passes otherwise, and nothing at
// all on a browser with neither — the plot above is complete without it.

import { detectPitch } from '../capture/live-pitch.js';
import * as prefs from '../shell/prefs.js';

let fluid = null;

/// Grid width in cells and pressure sweeps per frame, by effect level. 'subtle' halves the grid and
/// runs fewer sweeps: the ribbon is softer and the eddies less tight, but it still reads the voice.
const QUALITY = {
    full: { cells: 256, iterations: 24 },
    subtle: { cells: 128, iterations: 10 },
};

/// The fine pitch track is taken at this rate, independent of the capture's own frame size. The
/// recorder hands over ~11 frames a second, and vibrato sits at 5-7 Hz: sampled that sparsely it
/// aliases into nonsense, so each frame is re-read in short windows to get ~125 readings a second.
const FINE_RATE = 16000;
const FINE_WINDOW = 512;    // 32 ms: two periods of the detector's 60 Hz floor
const FINE_HOP = 128;       // 8 ms

/// How much track the measurement looks at. Half a second holds three cycles of a 6 Hz vibrato,
/// which is the fewest that tell a regular wobble from a scoop into the note.
const MEASURE_SECONDS = 0.5;
/// Below this the voice is called steady and the rate is reported as 0 — a few cents of wander is
/// the detector and the breath, not a vibrato.
const STEADY_CENTS = 8;
/// Vibrato lives in this band; a "rate" outside it is a slide or detector jitter, not a wobble.
const MIN_RATE = 3;
const MAX_RATE = 10;

/// Seconds for the dye and the flow to fall to ~37%. A few seconds of trail, then gone.
const DYE_FADE = 1.6;
const FLOW_FADE = 1.0;

/// With no reading for this long the take is over, and the sim stops on its last frame.
const IDLE_MS = 400;
const FIRST_PUSH_MS = 2000;

/// Measures vibrato over a run of (t seconds, midi) readings, all voiced and contiguous.
/// Answers { rate, depth, slope } — rate in Hz (0 when steady, null when the track is too sparse to
/// resolve one), depth in cents (amplitude, not peak-to-peak), slope in cents a second at the end —
/// or null when there is too little to measure.
export function measureVibrato(points, resolvesRate = true) {
    if (!points || points.length < 6) {
        return null;
    }
    const t0 = points[0].t;
    if (points[points.length - 1].t - t0 < 0.2) {
        return null;
    }

    // Octave slips from the detector would read as a 1200-cent vibrato; anything that far off the
    // median is dropped rather than averaged in.
    const sorted = points.map((p) => p.midi).sort((a, b) => a - b);
    const median = sorted[sorted.length >> 1];
    const kept = points.filter((p) => Math.abs(p.midi - median) < 2.5);
    if (kept.length < 6) {
        return null;
    }

    // Linear detrend, so a slow glide into the note is not counted as depth.
    let st = 0;
    let sc = 0;
    let stt = 0;
    let stc = 0;
    const n = kept.length;
    for (const p of kept) {
        const t = p.t - t0;
        const c = (p.midi - median) * 100;
        st += t;
        sc += c;
        stt += t * t;
        stc += t * c;
    }
    const denominator = (n * stt) - (st * st);
    const slope = denominator > 1e-9 ? ((n * stc) - (st * sc)) / denominator : 0;
    const intercept = (sc - (slope * st)) / n;

    // Residuals, lightly smoothed: the detector's frame-to-frame jitter is a few cents, and left in
    // it adds zero crossings that are not there.
    const raw = kept.map((p) => ((p.midi - median) * 100) - (intercept + (slope * (p.t - t0))));
    const residual = raw.map((value, i) => ((raw[i - 1] ?? value) + (2 * value) + (raw[i + 1] ?? value)) / 4);

    let energy = 0;
    for (const r of residual) {
        energy += r * r;
    }
    const rms = Math.sqrt(energy / n);
    const depth = Math.SQRT2 * rms;

    // The end-of-window slope the push follows, trend included so a voice sliding up pushes the
    // fluid up. Taken across the last ~40 ms rather than one hop, which is mostly detector jitter.
    const last = kept[n - 1];
    const before = kept[Math.max(n - 6, 0)];
    const dt = Math.max(last.t - before.t, 1e-3);
    const endSlope = ((last.midi - before.midi) * 100) / dt;

    if (!resolvesRate) {
        return { rate: null, depth, slope: endSlope, phase: Math.sign(residual[n - 1]) };
    }
    if (depth < STEADY_CENTS) {
        return { rate: 0, depth, slope: endSlope, phase: 0 };
    }

    // Zero crossings with hysteresis: the residual has to clear a third of its own RMS on the far
    // side before a crossing counts, so noise riding on the axis cannot double the rate.
    const threshold = 0.3 * rms;
    let side = 0;
    const crossings = [];
    for (let i = 0; i < n; i++) {
        const r = residual[i];
        const now = r > threshold ? 1 : (r < -threshold ? -1 : 0);
        if (now === 0) {
            continue;
        }
        if (side !== 0 && now !== side) {
            crossings.push(kept[i].t);
        }
        side = now;
    }
    let rate = 0;
    if (crossings.length >= 2) {
        const span = crossings[crossings.length - 1] - crossings[0];
        rate = span > 0 ? (crossings.length - 1) / (2 * span) : 0;
    }
    if (rate < MIN_RATE || rate > MAX_RATE) {
        rate = 0;
    }
    return { rate, depth, slope: endSlope, phase: rate > 0 ? Math.sign(residual[n - 1]) : 0 };
}

/// Re-reads one capture frame as a run of short windows at FINE_RATE. `endSeconds` is when the
/// frame ended on the take's clock, which is what the recorder passes as the reading's time.
function fineReadings(frame, sampleRate, endSeconds) {
    const ratio = sampleRate / FINE_RATE;
    const length = Math.floor(frame.length / ratio);
    if (length < FINE_WINDOW) {
        return [];
    }
    const mono = fluid.resampled.length >= length ? fluid.resampled : (fluid.resampled = new Float32Array(length));
    for (let i = 0; i < length; i++) {
        const position = i * ratio;
        const index = Math.floor(position);
        const a = frame[index] ?? 0;
        const b = frame[index + 1] ?? a;
        mono[i] = a + ((b - a) * (position - index));
    }
    const out = [];
    // Aligned so the last window ends on the frame's last sample.
    const first = (length - FINE_WINDOW) % FINE_HOP;
    for (let offset = first; offset + FINE_WINDOW <= length; offset += FINE_HOP) {
        const midi = detectPitch(mono.subarray(offset, offset + FINE_WINDOW), FINE_RATE);
        out.push({ t: endSeconds - ((length - offset - (FINE_WINDOW / 2)) / FINE_RATE), midi });
    }
    return out;
}

/// Readings inside one capture frame are 8 ms apart, but there is a ~37 ms seam between frames
/// (the windows do not straddle frames), so the fine gap allowance sits just above that seam.
///
/// The latest unbroken voiced run in the track, at most MEASURE_SECONDS long (twice that for the
/// coarse fallback, which needs more readings to say anything). A gap ends the run: a vibrato is
/// measured within one held note, never across a breath.
function latestRun(track, seconds, maxGap) {
    const run = [];
    for (let i = track.length - 1; i >= 0; i--) {
        const reading = track[i];
        if (reading.midi === null) {
            break;
        }
        if (run.length && run[0].t - reading.t > maxGap) {
            break;
        }
        if (track[track.length - 1].t - reading.t > seconds) {
            break;
        }
        run.unshift(reading);
    }
    return run;
}

function readColour(element, name, fallback) {
    const raw = getComputedStyle(element).getPropertyValue(name).trim();
    const probe = document.createElement('canvas').getContext('2d');
    probe.fillStyle = raw || fallback;
    const hex = probe.fillStyle;
    if (hex.startsWith('#')) {
        return [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255);
    }
    const parts = hex.match(/[\d.]+/g)?.map(Number) ?? [225, 29, 72];
    return parts.slice(0, 3).map((value) => value / 255);
}

function setState(value) {
    if (!fluid) {
        return;
    }
    fluid.canvas.dataset.vibratoFluid = value;
}

function publishMeasure(measure) {
    const canvas = fluid.canvas;
    if (!measure) {
        return;
    }
    if (measure.rate === null) {
        delete canvas.dataset.vibratoRate;
    } else {
        canvas.dataset.vibratoRate = measure.rate.toFixed(1);
    }
    canvas.dataset.vibratoDepth = String(Math.round(measure.depth));

    // A few times a second, not per reading: a number that changes eleven times a second cannot be
    // read, it can only flicker.
    const now = performance.now();
    if (fluid.readout && now - fluid.readoutAt > 250) {
        fluid.readoutAt = now;
        const depth = Math.round(measure.depth);
        fluid.readout.textContent = measure.rate > 0
            ? `vibrato ${measure.rate.toFixed(1)} Hz · ±${depth}¢`
            : `steady · ±${depth}¢`;
    }
}

// ── The simulation, shared parameters ─────────────────────────────────────────────────────────────

/// The per-frame force at the leading edge, in grid cells. Built from the measurement alone:
/// `push` follows the voice's own pitch motion, `curl` is the vibrato's depth times its rate with the
/// sign of where in the cycle the voice is, so a wider or faster wobble spins harder.
function frameParams(f, dt) {
    const { width, height } = f.grid;
    const params = {
        dt,
        velDecay: Math.exp(-dt / FLOW_FADE),
        dyeDecay: Math.exp(-dt / DYE_FADE),
        radius2: Math.max((height * 0.045) ** 2, 1.5),
        blend: Math.min(1, dt * 12),
        dye: 0.35,
        point: [0, 0],
        push: [0, 0],
        curl: 0,
        active: 0,
    };
    const head = f.head;
    if (!head || !f.map) {
        return params;
    }
    const x = f.map.x(head.seconds);
    const y = f.map.y(head.midi);
    if (!(x >= 0 && x <= 1 && y >= 0 && y <= 1)) {
        return params;
    }
    // Cells per semitone, so the vertical push moves the dye as far as the pitch moved.
    const semitone = height * Math.abs(f.map.y(head.midi + 1) - y);
    const m = f.measure;
    params.point = [x * width, y * height];
    // A wake behind the edge, the same for every note: that alone is the laminar ribbon.
    const wake = -width * 0.03;
    const rise = m ? Math.max(Math.min(-(m.slope / 100) * semitone, height * 0.6), -height * 0.6) : 0;
    params.push = [wake, rise];
    if (m && m.rate > 0) {
        params.curl = m.phase * Math.min((m.depth / 100) * m.rate * 4, 18);
    }
    params.active = 1;
    return params;
}

// ── WebGPU ────────────────────────────────────────────────────────────────────────────────────────

const WGSL_PARAMS = /* wgsl */ `
struct P {
    size: vec2f, dt: f32, velDecay: f32,
    dyeDecay: f32, radius2: f32, dye: f32, blend: f32,
    point: vec2f, push: vec2f,
    curl: f32, active: f32, pad: vec2f,
};
@group(0) @binding(0) var<uniform> p: P;

fn inside(id: vec3u) -> bool { return f32(id.x) < p.size.x && f32(id.y) < p.size.y; }
fn splat(cell: vec2f) -> f32 {
    let d = cell - p.point;
    return p.active * exp(-dot(d, d) / p.radius2);
}`;

const WGSL_ADVECT_VELOCITY = WGSL_PARAMS + /* wgsl */ `
@group(0) @binding(1) var smp: sampler;
@group(0) @binding(2) var velIn: texture_2d<f32>;
@group(0) @binding(3) var velOut: texture_storage_2d<rgba16float, write>;
@compute @workgroup_size(8, 8) fn main(@builtin(global_invocation_id) id: vec3u) {
    if (!inside(id)) { return; }
    let cell = vec2f(id.xy) + 0.5;
    let v = textureLoad(velIn, vec2i(id.xy), 0).xy;
    var nv = textureSampleLevel(velIn, smp, (cell - p.dt * v) / p.size, 0.0).xy * p.velDecay;
    let g = splat(cell);
    let d = cell - p.point;
    nv += g * p.blend * ((p.push - nv) + vec2f(-d.y, d.x) * p.curl);
    textureStore(velOut, vec2i(id.xy), vec4f(nv, 0.0, 1.0));
}`;

const WGSL_NEIGHBOURS = /* wgsl */ `
fn at(t: texture_2d<f32>, c: vec2i) -> vec4f {
    return textureLoad(t, clamp(c, vec2i(0), vec2i(p.size) - 1), 0);
}`;

const WGSL_DIVERGENCE = WGSL_PARAMS + WGSL_NEIGHBOURS + /* wgsl */ `
@group(0) @binding(1) var vel: texture_2d<f32>;
@group(0) @binding(2) var divOut: texture_storage_2d<rgba16float, write>;
@compute @workgroup_size(8, 8) fn main(@builtin(global_invocation_id) id: vec3u) {
    if (!inside(id)) { return; }
    let c = vec2i(id.xy);
    let div = 0.5 * ((at(vel, c + vec2i(1, 0)).x - at(vel, c - vec2i(1, 0)).x)
                   + (at(vel, c + vec2i(0, 1)).y - at(vel, c - vec2i(0, 1)).y));
    textureStore(divOut, c, vec4f(div, 0.0, 0.0, 1.0));
}`;

const WGSL_JACOBI = WGSL_PARAMS + WGSL_NEIGHBOURS + /* wgsl */ `
@group(0) @binding(1) var pIn: texture_2d<f32>;
@group(0) @binding(2) var div: texture_2d<f32>;
@group(0) @binding(3) var pOut: texture_storage_2d<rgba16float, write>;
@compute @workgroup_size(8, 8) fn main(@builtin(global_invocation_id) id: vec3u) {
    if (!inside(id)) { return; }
    let c = vec2i(id.xy);
    let sum = at(pIn, c + vec2i(1, 0)).x + at(pIn, c - vec2i(1, 0)).x
            + at(pIn, c + vec2i(0, 1)).x + at(pIn, c - vec2i(0, 1)).x;
    textureStore(pOut, c, vec4f((sum - at(div, c).x) * 0.25, 0.0, 0.0, 1.0));
}`;

const WGSL_PROJECT = WGSL_PARAMS + WGSL_NEIGHBOURS + /* wgsl */ `
@group(0) @binding(1) var velIn: texture_2d<f32>;
@group(0) @binding(2) var pressure: texture_2d<f32>;
@group(0) @binding(3) var velOut: texture_storage_2d<rgba16float, write>;
@compute @workgroup_size(8, 8) fn main(@builtin(global_invocation_id) id: vec3u) {
    if (!inside(id)) { return; }
    let c = vec2i(id.xy);
    let gradient = 0.5 * vec2f(at(pressure, c + vec2i(1, 0)).x - at(pressure, c - vec2i(1, 0)).x,
                               at(pressure, c + vec2i(0, 1)).x - at(pressure, c - vec2i(0, 1)).x);
    textureStore(velOut, c, vec4f(at(velIn, c).xy - gradient, 0.0, 1.0));
}`;

const WGSL_ADVECT_DYE = WGSL_PARAMS + /* wgsl */ `
@group(0) @binding(1) var smp: sampler;
@group(0) @binding(2) var vel: texture_2d<f32>;
@group(0) @binding(3) var dyeIn: texture_2d<f32>;
@group(0) @binding(4) var dyeOut: texture_storage_2d<rgba16float, write>;
@compute @workgroup_size(8, 8) fn main(@builtin(global_invocation_id) id: vec3u) {
    if (!inside(id)) { return; }
    let cell = vec2f(id.xy) + 0.5;
    let v = textureLoad(vel, vec2i(id.xy), 0).xy;
    var d = textureSampleLevel(dyeIn, smp, (cell - p.dt * v) / p.size, 0.0).x * p.dyeDecay;
    d = min(d + splat(cell) * p.dye, 1.0);
    textureStore(dyeOut, vec2i(id.xy), vec4f(d, 0.0, 0.0, 1.0));
}`;

const WGSL_DISPLAY = /* wgsl */ `
struct Out { @builtin(position) position: vec4f, @location(0) uv: vec2f };
@vertex fn vs(@builtin(vertex_index) i: u32) -> Out {
    var corners = array<vec2f, 6>(vec2f(-1, -1), vec2f(1, -1), vec2f(-1, 1), vec2f(-1, 1), vec2f(1, -1), vec2f(1, 1));
    var out: Out;
    out.position = vec4f(corners[i], 0, 1);
    out.uv = vec2f((corners[i].x + 1) * 0.5, (1 - corners[i].y) * 0.5);
    return out;
}
@group(0) @binding(0) var dye: texture_2d<f32>;
@group(0) @binding(1) var smp: sampler;
@group(0) @binding(2) var<uniform> fill: vec4f;
@fragment fn fs(input: Out) -> @location(0) vec4f {
    let a = clamp(textureSample(dye, smp, input.uv).x, 0.0, 1.0) * fill.a;
    return vec4f(fill.rgb * a, a);
}`;

async function createGpu(canvas, grid, iterations, colour) {
    if (!navigator.gpu) {
        return null;
    }
    const adapter = await navigator.gpu.requestAdapter();
    if (!adapter) {
        return null;
    }
    const device = await adapter.requestDevice();
    const context = canvas.getContext('webgpu');
    if (!context) {
        device.destroy();
        return null;
    }
    // Validation errors arrive asynchronously, not as throws. Any one of them means the sim is
    // drawing nothing, and the state attribute must not keep saying "running" over a blank canvas.
    let failed = false;
    device.addEventListener('uncapturederror', () => { failed = true; });
    device.lost.then(() => { failed = true; });
    const format = navigator.gpu.getPreferredCanvasFormat();
    context.configure({ device, format, alphaMode: 'premultiplied' });

    const { width, height } = grid;
    const usage = GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.STORAGE_BINDING
        | GPUTextureUsage.COPY_DST | GPUTextureUsage.COPY_SRC;
    const texture = () => device.createTexture({ size: [width, height], format: 'rgba16float', usage });
    const t = {
        velA: texture(), velB: texture(),
        pA: texture(), pB: texture(),
        div: texture(),
        dyeA: texture(), dyeB: texture(),
    };
    const params = device.createBuffer({ size: 64, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    const fill = device.createBuffer({ size: 16, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
    device.queue.writeBuffer(fill, 0, new Float32Array([...colour, 0.55]));
    const sampler = device.createSampler({
        magFilter: 'linear', minFilter: 'linear', addressModeU: 'clamp-to-edge', addressModeV: 'clamp-to-edge',
    });

    const compute = (code) => device.createComputePipeline({
        layout: 'auto',
        compute: { module: device.createShaderModule({ code }), entryPoint: 'main' },
    });
    const pipelines = {
        advectVelocity: compute(WGSL_ADVECT_VELOCITY),
        divergence: compute(WGSL_DIVERGENCE),
        jacobi: compute(WGSL_JACOBI),
        project: compute(WGSL_PROJECT),
        advectDye: compute(WGSL_ADVECT_DYE),
    };
    const displayModule = device.createShaderModule({ code: WGSL_DISPLAY });
    const display = device.createRenderPipeline({
        layout: 'auto',
        vertex: { module: displayModule, entryPoint: 'vs' },
        fragment: { module: displayModule, entryPoint: 'fs', targets: [{ format }] },
        primitive: { topology: 'triangle-list' },
    });

    // Every binding is fixed for the life of the sim — the ping-pong is in the order of the passes,
    // not in swapped bind groups — so all of them are built once here.
    const view = (tex) => tex.createView();
    const group = (pipeline, resources) => device.createBindGroup({
        layout: pipeline.getBindGroupLayout(0),
        entries: resources.map((resource, binding) => ({ binding, resource })),
    });
    const buffer = { buffer: params };
    const groups = {
        advectVelocity: group(pipelines.advectVelocity, [buffer, sampler, view(t.velA), view(t.velB)]),
        divergence: group(pipelines.divergence, [buffer, view(t.velB), view(t.div)]),
        jacobiAB: group(pipelines.jacobi, [buffer, view(t.pA), view(t.div), view(t.pB)]),
        jacobiBA: group(pipelines.jacobi, [buffer, view(t.pB), view(t.div), view(t.pA)]),
        project: group(pipelines.project, [buffer, view(t.velB), view(t.pA), view(t.velA)]),
        advectDye: group(pipelines.advectDye, [buffer, sampler, view(t.velA), view(t.dyeA), view(t.dyeB)]),
        display: group(display, [view(t.dyeA), sampler, { buffer: fill }]),
    };
    const workgroups = [Math.ceil(width / 8), Math.ceil(height / 8)];
    const zeros = new Uint16Array(width * height * 4);
    const block = new Float32Array(16);

    return {
        reset() {
            for (const tex of Object.values(t)) {
                device.queue.writeTexture({ texture: tex }, zeros, { bytesPerRow: width * 8 }, [width, height]);
            }
        },
        step(f) {
            block.set([width, height, f.dt, f.velDecay, f.dyeDecay, f.radius2, f.dye, f.blend,
                f.point[0], f.point[1], f.push[0], f.push[1], f.curl, f.active, 0, 0]);
            device.queue.writeBuffer(params, 0, block);

            const encoder = device.createCommandEncoder();
            const pass = encoder.beginComputePass();
            const run = (name, bind) => {
                pass.setPipeline(pipelines[name]);
                pass.setBindGroup(0, groups[bind ?? name]);
                pass.dispatchWorkgroups(workgroups[0], workgroups[1]);
            };
            // Velocity: advect A->B with the voice's force, divergence of B, pressure (ending in A,
            // warm-started from last frame's), projected B->A. Dye: carried by A, A->B.
            run('advectVelocity');
            run('divergence');
            for (let i = 0; i < iterations; i += 2) {
                run('jacobi', 'jacobiAB');
                run('jacobi', 'jacobiBA');
            }
            run('project');
            run('advectDye');
            pass.end();
            encoder.copyTextureToTexture({ texture: t.dyeB }, { texture: t.dyeA }, [width, height]);

            const draw = encoder.beginRenderPass({
                colorAttachments: [{
                    view: context.getCurrentTexture().createView(),
                    clearValue: { r: 0, g: 0, b: 0, a: 0 }, loadOp: 'clear', storeOp: 'store',
                }],
            });
            draw.setPipeline(display);
            draw.setBindGroup(0, groups.display);
            draw.draw(6);
            draw.end();
            device.queue.submit([encoder.finish()]);
        },
        // Top-down rows, the same as the plot above it.
        flipY: false,
        get failed() {
            return failed;
        },
        destroy() {
            for (const tex of Object.values(t)) {
                tex.destroy();
            }
            params.destroy();
            fill.destroy();
            try {
                context.unconfigure();
            } catch {
                // Already gone with the canvas.
            }
            device.destroy();
        },
    };
}

// ── WebGL2 ────────────────────────────────────────────────────────────────────────────────────────

const GLSL_VERTEX = `#version 300 es
in vec2 corner;
void main() { gl_Position = vec4(corner, 0.0, 1.0); }`;

const GLSL_HEADER = `#version 300 es
precision highp float;
uniform vec2 size;
uniform float dt, velDecay, dyeDecay, radius2, dye, blend, curl, active;
uniform vec2 point, push;
out vec4 result;
vec4 at(sampler2D t, ivec2 c) { return texelFetch(t, clamp(c, ivec2(0), ivec2(size) - 1), 0); }
float splat(vec2 cell) { vec2 d = cell - point; return active * exp(-dot(d, d) / radius2); }
`;

const GLSL = {
    advectVelocity: `uniform sampler2D velIn;
void main() {
    vec2 cell = gl_FragCoord.xy;
    vec2 v = texelFetch(velIn, ivec2(cell), 0).xy;
    vec2 nv = texture(velIn, (cell - dt * v) / size).xy * velDecay;
    vec2 d = cell - point;
    nv += splat(cell) * blend * ((push - nv) + vec2(-d.y, d.x) * curl);
    result = vec4(nv, 0.0, 1.0);
}`,
    divergence: `uniform sampler2D vel;
void main() {
    ivec2 c = ivec2(gl_FragCoord.xy);
    float div = 0.5 * ((at(vel, c + ivec2(1, 0)).x - at(vel, c - ivec2(1, 0)).x)
                     + (at(vel, c + ivec2(0, 1)).y - at(vel, c - ivec2(0, 1)).y));
    result = vec4(div, 0.0, 0.0, 1.0);
}`,
    jacobi: `uniform sampler2D pIn; uniform sampler2D div;
void main() {
    ivec2 c = ivec2(gl_FragCoord.xy);
    float sum = at(pIn, c + ivec2(1, 0)).x + at(pIn, c - ivec2(1, 0)).x
              + at(pIn, c + ivec2(0, 1)).x + at(pIn, c - ivec2(0, 1)).x;
    result = vec4((sum - at(div, c).x) * 0.25, 0.0, 0.0, 1.0);
}`,
    project: `uniform sampler2D velIn; uniform sampler2D pressure;
void main() {
    ivec2 c = ivec2(gl_FragCoord.xy);
    vec2 gradient = 0.5 * vec2(at(pressure, c + ivec2(1, 0)).x - at(pressure, c - ivec2(1, 0)).x,
                               at(pressure, c + ivec2(0, 1)).x - at(pressure, c - ivec2(0, 1)).x);
    result = vec4(at(velIn, c).xy - gradient, 0.0, 1.0);
}`,
    advectDye: `uniform sampler2D vel; uniform sampler2D dyeIn;
void main() {
    vec2 cell = gl_FragCoord.xy;
    vec2 v = texelFetch(vel, ivec2(cell), 0).xy;
    float d = texture(dyeIn, (cell - dt * v) / size).x * dyeDecay;
    result = vec4(min(d + splat(cell) * dye, 1.0), 0.0, 0.0, 1.0);
}`,
    display: `uniform sampler2D dyeTex; uniform vec4 fill; uniform vec2 canvasSize;
void main() {
    float a = clamp(texture(dyeTex, gl_FragCoord.xy / canvasSize).x, 0.0, 1.0) * fill.a;
    result = vec4(fill.rgb * a, a);
}`,
};

function createGl(canvas, grid, iterations, colour) {
    const gl = canvas.getContext('webgl2', { premultipliedAlpha: true, alpha: true, antialias: false });
    if (!gl) {
        return null;
    }
    // Rendering into half-float targets is an extension even in WebGL2; without it there is nowhere
    // to keep a velocity field, so there is no fluid.
    if (!gl.getExtension('EXT_color_buffer_float') && !gl.getExtension('EXT_color_buffer_half_float')) {
        return null;
    }
    const { width, height } = grid;

    const compile = (type, source) => {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        return shader;
    };
    const vertex = compile(gl.VERTEX_SHADER, GLSL_VERTEX);
    const programs = {};
    for (const [name, body] of Object.entries(GLSL)) {
        const program = gl.createProgram();
        const fragment = compile(gl.FRAGMENT_SHADER, GLSL_HEADER + body);
        gl.attachShader(program, vertex);
        gl.attachShader(program, fragment);
        gl.bindAttribLocation(program, 0, 'corner');
        gl.linkProgram(program);
        gl.deleteShader(fragment);
        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
            gl.deleteShader(vertex);
            for (const made of Object.values(programs)) {
                gl.deleteProgram(made);
            }
            gl.deleteProgram(program);
            return null;
        }
        programs[name] = program;
    }
    gl.deleteShader(vertex);

    const quad = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, quad);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);

    const target = () => {
        const texture = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA16F, width, height, 0, gl.RGBA, gl.HALF_FLOAT, null);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        const framebuffer = gl.createFramebuffer();
        gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
        return { texture, framebuffer };
    };
    let velocity = [target(), target()];
    let pressure = [target(), target()];
    let dyes = [target(), target()];
    const divergence = target();
    if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) {
        return null;
    }

    const all = () => [...velocity, ...pressure, ...dyes, divergence];
    let f = null;
    const pass = (name, inputs, output) => {
        const program = programs[name];
        gl.useProgram(program);
        gl.uniform2f(gl.getUniformLocation(program, 'size'), width, height);
        for (const key of ['dt', 'velDecay', 'dyeDecay', 'radius2', 'dye', 'blend', 'curl', 'active']) {
            gl.uniform1f(gl.getUniformLocation(program, key), f[key]);
        }
        gl.uniform2f(gl.getUniformLocation(program, 'point'), f.point[0], f.point[1]);
        gl.uniform2f(gl.getUniformLocation(program, 'push'), f.push[0], f.push[1]);
        Object.entries(inputs).forEach(([uniform, input], unit) => {
            gl.activeTexture(gl.TEXTURE0 + unit);
            gl.bindTexture(gl.TEXTURE_2D, input.texture);
            gl.uniform1i(gl.getUniformLocation(program, uniform), unit);
        });
        gl.bindFramebuffer(gl.FRAMEBUFFER, output ? output.framebuffer : null);
        gl.drawArrays(gl.TRIANGLES, 0, 3);
    };

    return {
        reset() {
            gl.clearColor(0, 0, 0, 0);
            for (const { framebuffer } of all()) {
                gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
                gl.clear(gl.COLOR_BUFFER_BIT);
            }
        },
        step(params) {
            f = params;
            gl.disable(gl.BLEND);
            gl.viewport(0, 0, width, height);
            pass('advectVelocity', { velIn: velocity[0] }, velocity[1]);
            velocity = [velocity[1], velocity[0]];
            pass('divergence', { vel: velocity[0] }, divergence);
            for (let i = 0; i < iterations; i++) {
                pass('jacobi', { pIn: pressure[0], div: divergence }, pressure[1]);
                pressure = [pressure[1], pressure[0]];
            }
            pass('project', { velIn: velocity[0], pressure: pressure[0] }, velocity[1]);
            velocity = [velocity[1], velocity[0]];
            pass('advectDye', { vel: velocity[0], dyeIn: dyes[0] }, dyes[1]);
            dyes = [dyes[1], dyes[0]];

            gl.viewport(0, 0, canvas.width, canvas.height);
            gl.useProgram(programs.display);
            gl.uniform4f(gl.getUniformLocation(programs.display, 'fill'), colour[0], colour[1], colour[2], 0.55);
            gl.uniform2f(gl.getUniformLocation(programs.display, 'canvasSize'), canvas.width, canvas.height);
            gl.clearColor(0, 0, 0, 0);
            gl.bindFramebuffer(gl.FRAMEBUFFER, null);
            gl.clear(gl.COLOR_BUFFER_BIT);
            pass('display', { dyeTex: dyes[0] }, null);
        },
        // GL's rows run bottom-up, so the force's y is flipped on the way in.
        flipY: true,
        get failed() {
            return gl.isContextLost();
        },
        destroy() {
            for (const { texture, framebuffer } of all()) {
                gl.deleteTexture(texture);
                gl.deleteFramebuffer(framebuffer);
            }
            for (const program of Object.values(programs)) {
                gl.deleteProgram(program);
            }
            gl.deleteBuffer(quad);
            // The context itself is left to the canvas: losing it here would leave a rebuild at the
            // other effect level with a dead context, since a canvas never hands out a second one.
        },
    };
}

// ── Lifecycle ─────────────────────────────────────────────────────────────────────────────────────

function gridFor(canvas) {
    const quality = QUALITY[prefs.level()] ?? QUALITY.subtle;
    const aspect = Math.max(canvas.clientHeight, 1) / Math.max(canvas.clientWidth, 1);
    return {
        width: quality.cells,
        height: Math.max(Math.round(quality.cells * aspect), 16),
        iterations: quality.iterations,
    };
}

/// Builds (or rebuilds) the backend for the canvas's current shape and the current effect level.
/// A generation counter makes a dispose during the await win: the late backend is destroyed unused.
async function build() {
    const f = fluid;
    const generation = ++f.generation;
    f.backend?.destroy();
    f.backend = null;
    if (!prefs.allowsMotion()) {
        setState('off');
        return;
    }
    const grid = gridFor(f.canvas);
    // A field this soft gains nothing from a device-pixel-sized canvas; the display pass's filter
    // spreads the grid over whatever size it is shown at.
    f.canvas.width = Math.max(f.canvas.clientWidth, 1);
    f.canvas.height = Math.max(f.canvas.clientHeight, 1);
    let backend = null;
    try {
        backend = await createGpu(f.canvas, grid, grid.iterations, f.colour);
    } catch {
        backend = null;
    }
    // A canvas that took a WebGPU context cannot hand out a WebGL2 one, and the reverse; whichever
    // the first build got is the one every rebuild gets.
    if (!backend) {
        try {
            backend = createGl(f.canvas, grid, grid.iterations, f.colour);
        } catch {
            backend = null;
        }
    }
    if (fluid !== f || generation !== f.generation) {
        backend?.destroy();
        return;
    }
    if (!backend) {
        setState('off');
        return;
    }
    f.backend = backend;
    f.grid = grid;
    backend.reset();
    if (f.running) {
        loop();
    } else {
        setState('still');
    }
}

function loop() {
    const f = fluid;
    if (!f || !f.backend || f.raf !== null) {
        return;
    }
    setState('running');
    let last = performance.now();
    const frame = (now) => {
        if (fluid !== f || !f.backend) {
            return;
        }
        if (f.backend.failed) {
            f.raf = null;
            f.backend.destroy();
            f.backend = null;
            setState('off');
            return;
        }
        const quiet = now - f.lastPushAt;
        if (quiet > (f.pushed ? IDLE_MS : FIRST_PUSH_MS)) {
            halt();
            return;
        }
        f.raf = requestAnimationFrame(frame);
        const dt = Math.min(Math.max((now - last) / 1000, 0.001), 1 / 30);
        last = now;
        const params = frameParams(f, dt);
        if (f.backend.flipY) {
            params.point = [params.point[0], f.grid.height - params.point[1]];
            params.push = [params.push[0], -params.push[1]];
        }
        f.backend.step(params);
    };
    f.raf = requestAnimationFrame(frame);
}

/// Stops the sim on its last frame. The canvas keeps showing what was presented.
export function halt() {
    if (!fluid) {
        return;
    }
    fluid.running = false;
    if (fluid.raf !== null) {
        cancelAnimationFrame(fluid.raf);
        fluid.raf = null;
    }
    if (fluid.backend) {
        setState('still');
    }
}

/// Binds to `canvas` (absolutely positioned under the take plot). Answers false with effects off;
/// the GPU comes up asynchronously and data-vibrato-fluid says whether it did.
export function init(canvas) {
    dispose();
    if (!canvas) {
        return false;
    }
    fluid = {
        canvas,
        readout: canvas.parentElement?.querySelector('[data-vibrato-readout]') ?? null,
        readoutAt: 0,
        colour: readColour(canvas, '--pm-playhead', '#e11d48'),
        backend: null,
        grid: null,
        generation: 0,
        running: false,
        raf: null,
        map: null,
        head: null,
        track: [],
        fine: false,
        measure: null,
        pushed: false,
        lastPushAt: 0,
        resampled: new Float32Array(0),
        level: prefs.level(),
        unsubscribe: null,
    };
    fluid.unsubscribe = prefs.subscribe((next) => {
        if (!fluid || next === fluid.level) {
            return;
        }
        fluid.level = next;
        if (fluid.raf !== null) {
            cancelAnimationFrame(fluid.raf);
            fluid.raf = null;
        }
        // Off stops it outright; full <-> subtle rebuilds at the other resolution in place.
        build();
    });
    if (!prefs.allowsMotion()) {
        setState('off');
        return false;
    }
    setState('off');
    build();
    return true;
}

/// A take is starting. `map.x(seconds)` and `map.y(midi)` give the plot's own 0..1 position (y down),
/// read live so a range the plot widens mid-take moves the injection point with it.
export function begin(map) {
    if (!fluid) {
        return;
    }
    fluid.map = map;
    fluid.track = [];
    fluid.head = null;
    fluid.measure = null;
    fluid.pushed = false;
    fluid.fine = false;
    fluid.running = true;
    fluid.lastPushAt = performance.now();
    if (fluid.readout) {
        fluid.readout.textContent = '';
    }
    delete fluid.canvas.dataset.vibratoRate;
    delete fluid.canvas.dataset.vibratoDepth;
    if (!fluid.backend) {
        return; // build() starts the loop when it lands
    }
    // A canvas that changed shape since the last take gets a grid of the new shape.
    const grid = gridFor(fluid.canvas);
    if (grid.height !== fluid.grid.height || grid.width !== fluid.grid.width) {
        build();
        return;
    }
    fluid.backend.reset();
    loop();
}

/// One capture frame's reading. `frame` and `sampleRate`, when the recorder passes them, let the
/// vibrato be measured from a fine track; without them only the depth can be, from the coarse one.
export function push(seconds, midi, frame, sampleRate) {
    const f = fluid;
    if (!f || !f.running) {
        return;
    }
    f.pushed = true;
    f.lastPushAt = performance.now();

    const voiced = midi !== null && Number.isFinite(midi);
    if (frame && sampleRate > 0) {
        f.fine = true;
        for (const reading of fineReadings(frame, sampleRate, seconds)) {
            f.track.push(reading);
        }
    } else {
        f.track.push({ t: seconds, midi: voiced ? midi : null });
    }
    // Only the last second or so is ever looked at.
    const cutoff = seconds - 1.2;
    let drop = 0;
    while (drop < f.track.length && f.track[drop].t < cutoff) {
        drop++;
    }
    if (drop) {
        f.track.splice(0, drop);
    }

    if (!voiced) {
        f.head = null;
        return;
    }
    const run = f.fine
        ? latestRun(f.track, MEASURE_SECONDS, 0.06)
        : latestRun(f.track, MEASURE_SECONDS * 2, 0.2);
    f.measure = measureVibrato(run, f.fine);
    // The fine track's last voiced reading is the truer "now"; the coarse one is the fallback.
    const latest = f.fine && run.length ? run[run.length - 1].midi : midi;
    f.head = { seconds, midi: latest };
    publishMeasure(f.measure);
}

/// Forgets the take: the field is emptied and the readout cleared.
export function clear() {
    if (!fluid) {
        return;
    }
    halt();
    fluid.map = null;
    fluid.head = null;
    fluid.track = [];
    fluid.measure = null;
    if (fluid.readout) {
        fluid.readout.textContent = '';
    }
    delete fluid.canvas.dataset.vibratoRate;
    delete fluid.canvas.dataset.vibratoDepth;
    if (fluid.backend) {
        fluid.backend.reset();
        // One empty frame, so the still left on the canvas is the cleared field.
        fluid.backend.step(frameParams({ ...fluid, head: null }, 1 / 60));
    }
}

export function dispose() {
    if (!fluid) {
        return;
    }
    const f = fluid;
    f.generation++;
    if (f.raf !== null) {
        cancelAnimationFrame(f.raf);
    }
    f.unsubscribe?.();
    f.backend?.destroy();
    if (f.readout) {
        f.readout.textContent = '';
    }
    try {
        f.canvas.dataset.vibratoFluid = 'off';
        delete f.canvas.dataset.vibratoRate;
        delete f.canvas.dataset.vibratoDepth;
    } catch {
        // Already detached.
    }
    fluid = null;
}
