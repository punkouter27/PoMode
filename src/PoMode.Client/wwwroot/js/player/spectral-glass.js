// Spectral glass: a frosted slab behind the note and chord lanes whose surface is the live spectrum
// of the stem being heard. mixer.js reads the audible stem's spectrum every frame, bins it into
// BANDS log-spaced bands from bass (bottom of the lanes) to air (top), and measures its spectral
// flatness; canvas.js pushes both here against the playback clock.
//
// The bands are written into a rolling history texture, one column per COLUMN_SEC of playback, so
// the slab trails the playhead on the canvas's own time axis: what was heard a few seconds ago sits
// under the notes that were sung then, and the stretch ahead of the playhead is clear. The surface
// is a heightfield over that history: its slopes light, refract and catch a highlight like a ridged
// pane. Then a separable blur frosts it by the flatness measured at each moment, so a noisy passage
// (breath, cymbals, a full band) reads as frosted glass and a held, pitched note as clear sharp
// ridges. Everything it draws is that measurement; nothing moves unless the audio does.
//
// WebGPU first, WebGL2 with the same three passes otherwise, nothing at all without either. The
// result is copied onto a plain 2D canvas, so canvas.js can keep drawing the last frame while the
// audio is paused (a GPU canvas's contents are only guaranteed until the next frame).

import {
    webgpuDevice, webgpuSurface, webgl2Surface, FULLSCREEN_WGSL,
    glProgram, glTarget, glDeleteTarget, glTexture,
} from './gpu-surface.js';

/// Log-spaced bands from LOW_HZ to HIGH_HZ (mixer.js bins into exactly this many).
export const BANDS = 64;
/// Ring of history columns. At 30 a second that is about 8.5 s of the song behind the playhead.
const COLUMNS = 256;
const COLUMN_SEC = 1 / 30;
const HISTORY_SEC = COLUMNS * COLUMN_SEC;
/// A playback jump longer than this is a seek, not a slow frame: the history is wiped rather than
/// smeared across the gap.
const MAX_GAP_COLUMNS = 15;

const GLASS_WGSL = /* wgsl */ `
struct Glass {
    size: vec2f,      // target pixels
    view: vec2f,      // seconds at the left and right edges of the canvas
    head: vec2f,      // newest column's seconds, seconds of history kept
    column: vec2f,    // seconds per column, columns in the ring
    tint: vec4f,      // theme tint; w is 1 in a dark theme
    look: vec4f,      // x opacity, y widest frost in pixels
};

@group(0) @binding(0) var history: texture_2d<f32>;
@group(0) @binding(1) var historySampler: sampler;
@group(0) @binding(2) var<uniform> glass: Glass;
@group(0) @binding(3) var source: texture_2d<f32>;
@group(0) @binding(4) var edgeSampler: sampler;

const BANDS = ${BANDS}.0;
const RELIEF = 4.0;

fn secondsAt(x: f32) -> f32 {
    return glass.view.x + x * (glass.view.y - glass.view.x);
}

// 1 under the recent past, fading over the oldest part of the history, 0 ahead of the playhead.
fn presence(t: f32) -> f32 {
    let age = glass.head.x - t;
    if (age < 0.0 || age > glass.head.y) {
        return 0.0;
    }
    return 1.0 - smoothstep(glass.head.y * 0.55, glass.head.y, age);
}

// Repeat addressing wraps this into the ring.
fn columnU(t: f32) -> f32 {
    return t / glass.column.x / glass.column.y;
}

fn heightAt(p: vec2f) -> f32 {
    let uv = p / glass.size;
    let t = secondsAt(uv.x);
    let band = clamp(1.0 - uv.y, 0.0, 1.0) * (BANDS - 1.0) + 0.5;
    let level = textureSampleLevel(history, historySampler, vec2f(columnU(t), band / (BANDS + 1.0)), 0.0).r;
    return level * presence(t);
}

// The flatness row sits under the bands: 0 for a pure tone, 1 for noise.
fn frostAt(x: f32) -> f32 {
    let t = secondsAt(x / glass.size.x);
    let row = (BANDS + 0.5) / (BANDS + 1.0);
    return textureSampleLevel(history, historySampler, vec2f(columnU(t), row), 0.0).r * presence(t);
}

@fragment fn shade(@builtin(position) position: vec4f) -> @location(0) vec4f {
    let p = position.xy;
    let present = presence(secondsAt(p.x / glass.size.x));
    if (present <= 0.0) {
        return vec4f(0.0);
    }
    let dx = heightAt(p + vec2f(2.0, 0.0)) - heightAt(p - vec2f(2.0, 0.0));
    let dy = heightAt(p + vec2f(0.0, 2.0)) - heightAt(p - vec2f(0.0, 2.0));
    let n = normalize(vec3f(-dx * RELIEF, -dy * RELIEF, 1.0));
    let light = normalize(vec3f(-0.4, -0.6, 0.7));
    let diffuse = max(dot(n, light), 0.0);
    let spec = pow(max(dot(n, normalize(light + vec3f(0.0, 0.0, 1.0))), 0.0), 56.0);
    let slope = 1.0 - n.z;
    // Refraction: through a ridge the pane shows the spectrum a little way down its slope, so a
    // peak gathers light at its crest and thins on its flanks, as a lens would.
    let bent = heightAt(p + n.xy * 10.0);
    let frost = frostAt(p.x);
    let dark = glass.tint.w;
    let lit = mix(glass.tint.rgb * (0.55 + 0.45 * diffuse), vec3f(1.0), spec * (0.4 + 0.6 * dark));
    var alpha = 0.035 + 0.05 * frost + 0.15 * bent * bent + 0.16 * spec
        + 0.12 * slope * mix(1.0 - diffuse, 1.0, dark);
    alpha = min(alpha * glass.look.x, 0.34) * present;
    return vec4f(lit * alpha, alpha);
}

// Nine taps spanning the frost radius each side; a pitched moment barely blurs at all.
fn blur(p: vec2f, direction: vec2f) -> vec4f {
    let radius = mix(0.5, glass.look.y, frostAt(p.x));
    let stride = direction * radius * 0.25 / glass.size;
    let uv = p / glass.size;
    var sum = vec4f(0.0);
    var total = 0.0;
    for (var i = -4; i <= 4; i++) {
        let f = f32(i);
        let w = exp(-f * f / 8.0);
        sum += textureSampleLevel(source, edgeSampler, uv + stride * f, 0.0) * w;
        total += w;
    }
    return sum / total;
}

@fragment fn blurX(@builtin(position) position: vec4f) -> @location(0) vec4f {
    return blur(position.xy, vec2f(1.0, 0.0));
}

@fragment fn blurY(@builtin(position) position: vec4f) -> @location(0) vec4f {
    return blur(position.xy, vec2f(0.0, 1.0));
}`;

// The same three passes for WebGL2. gl_FragCoord is y-up; `pixel` turns it y-down so the logic
// matches the WGSL line for line, while the blur passes sample their source at gl_FragCoord itself,
// which is where the previous pass wrote that pixel.
const GLSL_COMMON = `#version 300 es
precision highp float;
uniform vec2 size;
uniform vec2 view;
uniform vec2 head;
uniform vec2 column;
uniform vec4 tint;
uniform vec4 look;
uniform sampler2D history;
uniform sampler2D source;
out vec4 colour;
const float BANDS = ${BANDS}.0;
const float RELIEF = 4.0;

vec2 pixel() { return vec2(gl_FragCoord.x, size.y - gl_FragCoord.y); }
float secondsAt(float x) { return view.x + x * (view.y - view.x); }
float presence(float t) {
    float age = head.x - t;
    if (age < 0.0 || age > head.y) { return 0.0; }
    return 1.0 - smoothstep(head.y * 0.55, head.y, age);
}
float columnU(float t) { return t / column.x / column.y; }
float heightAt(vec2 p) {
    vec2 uv = p / size;
    float t = secondsAt(uv.x);
    float band = clamp(1.0 - uv.y, 0.0, 1.0) * (BANDS - 1.0) + 0.5;
    return textureLod(history, vec2(columnU(t), band / (BANDS + 1.0)), 0.0).r * presence(t);
}
float frostAt(float x) {
    float t = secondsAt(x / size.x);
    return textureLod(history, vec2(columnU(t), (BANDS + 0.5) / (BANDS + 1.0)), 0.0).r * presence(t);
}
vec4 blur(vec2 direction) {
    float radius = mix(0.5, look.y, frostAt(gl_FragCoord.x));
    vec2 stride = direction * radius * 0.25 / size;
    vec2 uv = gl_FragCoord.xy / size;
    vec4 sum = vec4(0.0);
    float total = 0.0;
    for (int i = -4; i <= 4; i++) {
        float f = float(i);
        float w = exp(-f * f / 8.0);
        sum += textureLod(source, uv + stride * f, 0.0) * w;
        total += w;
    }
    return sum / total;
}
`;

const GLSL_SHADE = GLSL_COMMON + `
void main() {
    vec2 p = pixel();
    float present = presence(secondsAt(p.x / size.x));
    if (present <= 0.0) { colour = vec4(0.0); return; }
    float dx = heightAt(p + vec2(2.0, 0.0)) - heightAt(p - vec2(2.0, 0.0));
    float dy = heightAt(p + vec2(0.0, 2.0)) - heightAt(p - vec2(0.0, 2.0));
    vec3 n = normalize(vec3(-dx * RELIEF, -dy * RELIEF, 1.0));
    vec3 light = normalize(vec3(-0.4, -0.6, 0.7));
    float diffuse = max(dot(n, light), 0.0);
    float spec = pow(max(dot(n, normalize(light + vec3(0.0, 0.0, 1.0))), 0.0), 56.0);
    float slope = 1.0 - n.z;
    float bent = heightAt(p + n.xy * 10.0);
    float frost = frostAt(p.x);
    float dark = tint.w;
    vec3 lit = mix(tint.rgb * (0.55 + 0.45 * diffuse), vec3(1.0), spec * (0.4 + 0.6 * dark));
    float alpha = 0.035 + 0.05 * frost + 0.15 * bent * bent + 0.16 * spec
        + 0.12 * slope * mix(1.0 - diffuse, 1.0, dark);
    alpha = min(alpha * look.x, 0.34) * present;
    colour = vec4(lit * alpha, alpha);
}`;

const GLSL_BLUR_X = GLSL_COMMON + 'void main() { colour = blur(vec2(1.0, 0.0)); }';
const GLSL_BLUR_Y = GLSL_COMMON + 'void main() { colour = blur(vec2(0.0, 1.0)); }';
const GLSL_UNIFORMS = ['size', 'view', 'head', 'column', 'tint', 'look', 'history', 'source'];

/// One glass per canvas. `onReady` fires once a backend exists (WebGPU or WebGL2), so the caller
/// can redraw; with neither it never fires and `ready` stays false.
export function create(onReady) {
    const glass = {
        backend: null,
        disposed: false,
        // BANDS rows of levels, then one row of flatness, COLUMNS wide; 0..255.
        data: new Uint8Array(COLUMNS * (BANDS + 1)),
        headColumn: null,
        headSec: 0,
        hasData: false,
        dirty: true,
        renderedKey: null,
        image: document.createElement('canvas'),
    };

    (async () => {
        const backend = (await gpuBackend()) ?? glBackend();
        if (glass.disposed) {
            backend?.destroy();
            return;
        }
        glass.backend = backend;
        if (backend) {
            onReady?.();
        }
    })();

    return {
        get ready() {
            return glass.backend !== null;
        },
        get hasData() {
            return glass.hasData;
        },
        /// Records the spectrum heard at `seconds`: `bands` is BANDS levels in 0..1, bass first,
        /// `frost` the spectral flatness mapped to 0 (a pure tone) .. 1 (noise).
        push(seconds, bands, frost) {
            const column = Math.floor(seconds / COLUMN_SEC);
            const head = glass.headColumn;
            if (head === null || column < head || column - head > MAX_GAP_COLUMNS) {
                glass.data.fill(0);
                glass.headColumn = column - 1;
            }
            const f = Math.round(Math.min(Math.max(frost, 0), 1) * 255);
            // A slow frame fills the columns it skipped with this reading; a second frame inside the
            // same column overwrites it with the newer one.
            for (let c = Math.min(glass.headColumn + 1, column); c <= column; c++) {
                const slot = ((c % COLUMNS) + COLUMNS) % COLUMNS;
                for (let b = 0; b < BANDS; b++) {
                    glass.data[b * COLUMNS + slot] = Math.round(Math.min(Math.max(bands[b], 0), 1) * 255);
                }
                glass.data[BANDS * COLUMNS + slot] = f;
            }
            glass.headColumn = column;
            glass.headSec = seconds;
            glass.hasData = true;
            glass.dirty = true;
        },
        /// Renders the slab for the canvas's current view and answers an image to draw, or null.
        /// Re-renders only when the history, the view, the size or the theme moved; otherwise the
        /// last frame is answered as it was, which is what holds the glass still while paused.
        render(options) {
            if (!glass.backend || !glass.hasData) {
                return null;
            }
            const width = Math.max(1, Math.round(options.width * options.scale));
            const height = Math.max(1, Math.round(options.height * options.scale));
            const key = [width, height, options.viewStart, options.viewEnd, ...options.tint,
                options.dark, options.opacity].join('|');
            if (!glass.dirty && key === glass.renderedKey) {
                return glass.image;
            }
            const uniforms = new Float32Array([
                width, height,
                options.viewStart, options.viewEnd,
                glass.headSec, HISTORY_SEC,
                COLUMN_SEC, COLUMNS,
                ...options.tint, options.dark ? 1 : 0,
                options.opacity, 10 * options.scale, 0, 0,
            ]);
            try {
                glass.backend.draw(width, height, uniforms, glass.data, glass.dirty);
            } catch {
                return null;
            }
            if (glass.image.width !== width || glass.image.height !== height) {
                glass.image.width = width;
                glass.image.height = height;
            }
            const copy = glass.image.getContext('2d');
            copy.clearRect(0, 0, width, height);
            copy.drawImage(glass.backend.canvas, 0, 0);
            glass.dirty = false;
            glass.renderedKey = key;
            return glass.image;
        },
        /// Forgets the history (a new song); the next push starts a fresh one.
        clear() {
            glass.data.fill(0);
            glass.headColumn = null;
            glass.hasData = false;
            glass.dirty = true;
        },
        dispose() {
            glass.disposed = true;
            glass.backend?.destroy();
            glass.backend = null;
        },
    };
}

async function gpuBackend() {
    const device = await webgpuDevice();
    if (!device) {
        return null;
    }
    try {
        const surface = webgpuSurface(device);
        if (!surface) {
            device.destroy();
            return null;
        }
        const module = device.createShaderModule({ code: FULLSCREEN_WGSL + GLASS_WGSL });
        const pipeline = (entryPoint, format) => device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs' },
            fragment: { module, entryPoint, targets: [{ format }] },
            primitive: { topology: 'triangle-list' },
        });
        const shade = pipeline('shade', 'rgba8unorm');
        const blurX = pipeline('blurX', 'rgba8unorm');
        const blurY = pipeline('blurY', surface.format);

        const history = device.createTexture({
            size: [COLUMNS, BANDS + 1], format: 'r8unorm',
            usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST,
        });
        const uniform = device.createBuffer({ size: 64, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        const historySampler = device.createSampler({
            magFilter: 'linear', minFilter: 'linear', addressModeU: 'repeat', addressModeV: 'clamp-to-edge',
        });
        const edgeSampler = device.createSampler({ magFilter: 'linear', minFilter: 'linear' });

        let targets = null;
        const makeTargets = (width, height) => {
            targets?.a.destroy();
            targets?.b.destroy();
            const texture = () => device.createTexture({
                size: [width, height], format: 'rgba8unorm',
                usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.RENDER_ATTACHMENT,
            });
            const a = texture();
            const b = texture();
            const common = [
                { binding: 0, resource: history.createView() },
                { binding: 1, resource: historySampler },
                { binding: 2, resource: { buffer: uniform } },
            ];
            const blurring = (from, on) => device.createBindGroup({
                layout: on.getBindGroupLayout(0),
                entries: [...common,
                    { binding: 3, resource: from.createView() },
                    { binding: 4, resource: edgeSampler }],
            });
            targets = {
                width, height, a, b,
                shade: device.createBindGroup({ layout: shade.getBindGroupLayout(0), entries: common }),
                blurX: blurring(a, blurX),
                blurY: blurring(b, blurY),
            };
        };

        const run = (encoder, pipe, bindings, view) => {
            const pass = encoder.beginRenderPass({
                colorAttachments: [{ view, clearValue: { r: 0, g: 0, b: 0, a: 0 }, loadOp: 'clear', storeOp: 'store' }],
            });
            pass.setPipeline(pipe);
            pass.setBindGroup(0, bindings);
            pass.draw(6);
            pass.end();
        };

        const backend = {
            canvas: surface.canvas,
            draw(width, height, uniforms, data, historyChanged) {
                if (!targets || targets.width !== width || targets.height !== height) {
                    surface.canvas.width = width;
                    surface.canvas.height = height;
                    makeTargets(width, height);
                }
                device.queue.writeBuffer(uniform, 0, uniforms);
                if (historyChanged) {
                    device.queue.writeTexture({ texture: history }, data, { bytesPerRow: COLUMNS }, [COLUMNS, BANDS + 1]);
                }
                const encoder = device.createCommandEncoder();
                run(encoder, shade, targets.shade, targets.a.createView());
                run(encoder, blurX, targets.blurX, targets.b.createView());
                run(encoder, blurY, targets.blurY, surface.context.getCurrentTexture().createView());
                device.queue.submit([encoder.finish()]);
            },
            destroy() {
                targets?.a.destroy();
                targets?.b.destroy();
                history.destroy();
                uniform.destroy();
                device.destroy();
            },
        };
        // A lost device (driver reset, tab backgrounded too long) leaves the glass off, not broken.
        device.lost.then(() => {
            backend.draw = () => {
                throw new Error('device lost');
            };
        });
        return backend;
    } catch {
        device.destroy();
        return null;
    }
}

function glBackend() {
    const surface = webgl2Surface();
    if (!surface) {
        return null;
    }
    const { gl, canvas, vao } = surface;
    const shade = glProgram(gl, GLSL_SHADE, GLSL_UNIFORMS);
    const blurX = glProgram(gl, GLSL_BLUR_X, GLSL_UNIFORMS);
    const blurY = glProgram(gl, GLSL_BLUR_Y, GLSL_UNIFORMS);
    if (!shade || !blurX || !blurY) {
        return null;
    }
    const history = glTexture(gl, gl.REPEAT);
    let a = null;
    let b = null;

    const run = (program, uniforms, framebuffer, from) => {
        gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
        gl.viewport(0, 0, canvas.width, canvas.height);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.useProgram(program.program);
        const at = program.at;
        gl.uniform2f(at.size, uniforms[0], uniforms[1]);
        gl.uniform2f(at.view, uniforms[2], uniforms[3]);
        gl.uniform2f(at.head, uniforms[4], uniforms[5]);
        gl.uniform2f(at.column, uniforms[6], uniforms[7]);
        gl.uniform4f(at.tint, uniforms[8], uniforms[9], uniforms[10], uniforms[11]);
        gl.uniform4f(at.look, uniforms[12], uniforms[13], uniforms[14], uniforms[15]);
        gl.activeTexture(gl.TEXTURE0);
        gl.bindTexture(gl.TEXTURE_2D, history);
        gl.uniform1i(at.history, 0);
        gl.activeTexture(gl.TEXTURE1);
        gl.bindTexture(gl.TEXTURE_2D, from ? from.texture : null);
        gl.uniform1i(at.source, 1);
        gl.bindVertexArray(vao);
        gl.drawArrays(gl.TRIANGLES, 0, 6);
    };

    return {
        canvas,
        draw(width, height, uniforms, data, historyChanged) {
            if (gl.isContextLost()) {
                throw new Error('context lost');
            }
            if (!a || a.width !== width || a.height !== height) {
                canvas.width = width;
                canvas.height = height;
                glDeleteTarget(gl, a);
                glDeleteTarget(gl, b);
                a = glTarget(gl, width, height);
                b = glTarget(gl, width, height);
            }
            if (historyChanged) {
                gl.bindTexture(gl.TEXTURE_2D, history);
                gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
                gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, COLUMNS, BANDS + 1, 0, gl.RED, gl.UNSIGNED_BYTE, data);
            }
            run(shade, uniforms, a.framebuffer, null);
            run(blurX, uniforms, b.framebuffer, a);
            run(blurY, uniforms, null, b);
        },
        destroy() {
            glDeleteTarget(gl, a);
            glDeleteTarget(gl, b);
            gl.deleteTexture(history);
            gl.deleteProgram(shade.program);
            gl.deleteProgram(blurX.program);
            gl.deleteProgram(blurY.program);
            gl.deleteVertexArray(vao);
            gl.getExtension('WEBGL_lose_context')?.loseContext();
        },
    };
}
