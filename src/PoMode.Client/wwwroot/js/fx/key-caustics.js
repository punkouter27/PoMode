// The section map's key-change ripple: when the playhead crosses into a section whose mode moves the
// key signature, refracted light spreads across the similarity matrix from that boundary for about a
// second. The ripple is the size of the move, nothing more: its ring count (and so its wavelength) is
// how many steps the key signature travelled around the circle of fifths, 0 to 6, and its colour is
// the new section's mode colour. section-map.js decides when and how far; this module only draws.
//
// WebGPU when available, WebGL2 otherwise, and plain 2D rings as the last resort. With motion off the
// ripple is one still frame held for its lifetime; at the subtle level the refraction is dropped and
// the field drawn at device-independent resolution.

const LIFE_SECONDS = 1.2;

const WGSL = /* wgsl */ `
struct Ripple {
    size: vec4f,      // width, height (CSS px), age 0..1, steps
    origin: vec4f,    // x, y (CSS px), reach (CSS px), refraction 0|1
    colour: vec4f,
};
@group(0) @binding(0) var<uniform> ripple: Ripple;

struct Out { @builtin(position) position: vec4f, @location(0) uv: vec2f };

@vertex fn vs(@builtin(vertex_index) i: u32) -> Out {
    var corners = array<vec2f, 3>(vec2f(-1, -1), vec2f(3, -1), vec2f(-1, 3));
    var out: Out;
    out.position = vec4f(corners[i], 0, 1);
    out.uv = vec2f((corners[i].x + 1) * 0.5, (1 - corners[i].y) * 0.5);
    return out;
}

@fragment fn fs(input: Out) -> @location(0) vec4f {
    let p = input.uv * ripple.size.xy;
    let age = ripple.size.z;
    let steps = ripple.size.w;
    let offset = p - ripple.origin.xy;
    let d = length(offset);
    let front = max(age * ripple.origin.z, 1.0);
    // One line at the front and one per circle-of-fifths step behind it, evenly spaced back to the boundary.
    let phase = (front - d) / (front / (steps + 1.0));
    let angle = atan2(offset.y, offset.x);
    let bend = ripple.origin.w * 0.22 * sin((angle * 7.0) + (d * 0.05) - (age * 9.0)) * sin((angle * 5.0) - (d * 0.07) + (age * 6.0));
    let w = fract(phase + bend);
    let line = pow(1.0 - min(w, 1.0 - w) * 2.0, 10.0);
    let web = ripple.origin.w * 0.45 * pow(abs(sin((angle * 9.0) + (phase * 3.1) + (bend * 4.0))), 14.0);
    let within = step(0.0, phase) * (1.0 - smoothstep(steps + 0.5, steps + 0.9, phase));
    let edge = exp(-abs(d - front) / 2.5);
    let fade = pow(1.0 - age, 1.5);
    let alpha = clamp(((line * (1.0 + web)) * within + edge) * fade * 0.6, 0.0, 0.7);
    return vec4f(ripple.colour.rgb * alpha, alpha);
}`;

const GLSL_VERTEX = `#version 300 es
out vec2 vUv;
void main() {
    vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vUv = vec2(corner.x, 1.0 - corner.y);
    gl_Position = vec4((corner * 2.0) - 1.0, 0.0, 1.0);
}`;

const GLSL_FRAGMENT = `#version 300 es
precision highp float;
in vec2 vUv;
uniform vec4 uSize;
uniform vec4 uOrigin;
uniform vec4 uColour;
out vec4 outColour;

void main() {
    vec2 p = vUv * uSize.xy;
    float age = uSize.z;
    float steps = uSize.w;
    vec2 offset = p - uOrigin.xy;
    float d = length(offset);
    float front = max(age * uOrigin.z, 1.0);
    float phase = (front - d) / (front / (steps + 1.0));
    float angle = atan(offset.y, offset.x);
    float bend = uOrigin.w * 0.22 * sin((angle * 7.0) + (d * 0.05) - (age * 9.0)) * sin((angle * 5.0) - (d * 0.07) + (age * 6.0));
    float w = fract(phase + bend);
    float line = pow(1.0 - min(w, 1.0 - w) * 2.0, 10.0);
    float web = uOrigin.w * 0.45 * pow(abs(sin((angle * 9.0) + (phase * 3.1) + (bend * 4.0))), 14.0);
    float within = step(0.0, phase) * (1.0 - smoothstep(steps + 0.5, steps + 0.9, phase));
    float edge = exp(-abs(d - front) / 2.5);
    float fade = pow(1.0 - age, 1.5);
    float alpha = clamp(((line * (1.0 + web)) * within + edge) * fade * 0.6, 0.0, 0.7);
    outColour = vec4(uColour.rgb * alpha, alpha);
}`;

function sizeCanvas(canvas, scale) {
    const width = Math.max(canvas.clientWidth, 1);
    const height = Math.max(canvas.clientHeight, 1);
    const w = Math.max(1, Math.round(width * scale));
    const h = Math.max(1, Math.round(height * scale));
    if (canvas.width !== w || canvas.height !== h) {
        canvas.width = w;
        canvas.height = h;
    }
    return { width, height };
}

async function startGpu(canvas) {
    if (!navigator.gpu) {
        return null;
    }
    try {
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
        const format = navigator.gpu.getPreferredCanvasFormat();
        context.configure({ device, format, alphaMode: 'premultiplied' });
        const module = device.createShaderModule({ code: WGSL });
        const pipeline = device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs' },
            fragment: { module, entryPoint: 'fs', targets: [{ format }] },
            primitive: { topology: 'triangle-list' },
        });
        const uniform = device.createBuffer({ size: 48, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        const bindings = device.createBindGroup({
            layout: pipeline.getBindGroupLayout(0),
            entries: [{ binding: 0, resource: { buffer: uniform } }],
        });
        const pass = (data, clearOnly) => {
            device.queue.writeBuffer(uniform, 0, data);
            const encoder = device.createCommandEncoder();
            const render = encoder.beginRenderPass({
                colorAttachments: [{
                    view: context.getCurrentTexture().createView(),
                    clearValue: { r: 0, g: 0, b: 0, a: 0 }, loadOp: 'clear', storeOp: 'store',
                }],
            });
            if (!clearOnly) {
                render.setPipeline(pipeline);
                render.setBindGroup(0, bindings);
                render.draw(3);
            }
            render.end();
            device.queue.submit([encoder.finish()]);
        };
        return {
            name: 'webgpu',
            draw: data => pass(data, false),
            clear: () => pass(new Float32Array(12), true),
            dispose() {
                uniform.destroy();
                context.unconfigure();
                device.destroy();
            },
        };
    } catch {
        return null;
    }
}

function compile(gl, type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
        gl.deleteShader(shader);
        throw new Error('shader failed');
    }
    return shader;
}

function startGl(canvas) {
    const gl = canvas.getContext('webgl2', { premultipliedAlpha: true, antialias: false });
    if (!gl) {
        return null;
    }
    try {
        const program = gl.createProgram();
        const vertex = compile(gl, gl.VERTEX_SHADER, GLSL_VERTEX);
        const fragment = compile(gl, gl.FRAGMENT_SHADER, GLSL_FRAGMENT);
        gl.attachShader(program, vertex);
        gl.attachShader(program, fragment);
        gl.linkProgram(program);
        gl.deleteShader(vertex);
        gl.deleteShader(fragment);
        if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
            gl.deleteProgram(program);
            return null;
        }
        const vao = gl.createVertexArray();
        const u = {
            size: gl.getUniformLocation(program, 'uSize'),
            origin: gl.getUniformLocation(program, 'uOrigin'),
            colour: gl.getUniformLocation(program, 'uColour'),
        };
        const clear = () => {
            gl.viewport(0, 0, canvas.width, canvas.height);
            gl.clearColor(0, 0, 0, 0);
            gl.clear(gl.COLOR_BUFFER_BIT);
        };
        return {
            name: 'webgl2',
            draw(data) {
                clear();
                gl.useProgram(program);
                gl.bindVertexArray(vao);
                gl.uniform4fv(u.size, data.subarray(0, 4));
                gl.uniform4fv(u.origin, data.subarray(4, 8));
                gl.uniform4fv(u.colour, data.subarray(8, 12));
                gl.drawArrays(gl.TRIANGLES, 0, 3);
            },
            clear,
            dispose() {
                gl.deleteVertexArray(vao);
                gl.deleteProgram(program);
                gl.getExtension('WEBGL_lose_context')?.loseContext();
            },
        };
    } catch {
        return null;
    }
}

/// The last resort: the same front and rings as stroked circles, without the refraction.
function start2d(canvas) {
    const ctx = canvas.getContext('2d');
    if (!ctx) {
        return null;
    }
    const clear = () => {
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, canvas.width, canvas.height);
    };
    return {
        name: '2d',
        draw(data) {
            clear();
            const [width, , age, steps, x, y, reach] = data;
            const scale = canvas.width / Math.max(width, 1);
            const [r, g, b] = [data[8], data[9], data[10]].map(value => Math.round(value * 255));
            const front = Math.max(age * reach, 1);
            ctx.setTransform(scale, 0, 0, scale, 0, 0);
            ctx.strokeStyle = `rgb(${r} ${g} ${b})`;
            ctx.globalAlpha = 0.6 * Math.pow(1 - age, 1.5);
            for (let ring = 0; ring <= steps; ring++) {
                ctx.lineWidth = ring === 0 ? 2.5 : 1.5;
                ctx.beginPath();
                ctx.arc(x, y, front * (1 - (ring / (steps + 1))), 0, Math.PI * 2);
                ctx.stroke();
            }
            ctx.globalAlpha = 1;
        },
        clear,
        dispose: clear,
    };
}

/// Starts the ripple layer on `canvas`. Answers { ready, renderer(), play(ripple, level), dispose() }.
export function createCaustics(canvas) {
    let backend = null;
    let disposed = false;
    let frame = null;
    let timer = null;
    let onEnd = null;
    const ready = (async () => {
        backend = (await startGpu(canvas)) ?? startGl(canvas) ?? start2d(canvas);
        if (disposed) {
            backend?.dispose();
            backend = null;
        }
        return backend?.name ?? 'none';
    })();

    const stop = () => {
        if (frame !== null) {
            cancelAnimationFrame(frame);
            frame = null;
        }
        clearTimeout(timer);
        timer = null;
        backend?.clear();
        const ended = onEnd;
        onEnd = null;
        ended?.();
    };

    return {
        ready,
        renderer: () => backend?.name ?? 'none',
        /// `ripple` is { x, y (0..1 of the canvas), steps (0..6), rgb (0..1) }; `level` is the prefs
        /// level: 'full' refracts, 'subtle' draws plain rings at CSS resolution, 'off' holds one still
        /// frame. `ended` is called when it has gone, or when a newer ripple replaces it.
        play(ripple, level, ended) {
            stop();
            if (!backend) {
                ended?.();
                return;
            }
            onEnd = ended;
            const scale = level === 'full' ? Math.min(window.devicePixelRatio || 1, 2) : 1;
            const render = age => {
                const { width, height } = sizeCanvas(canvas, scale);
                const x = ripple.x * width;
                const y = ripple.y * height;
                // Far enough to reach the furthest corner from the boundary before it fades.
                const reach = Math.max(Math.hypot(x, y), Math.hypot(width - x, y), Math.hypot(x, height - y), Math.hypot(width - x, height - y));
                backend.draw(new Float32Array([
                    width, height, age, ripple.steps,
                    x, y, reach, level === 'full' ? 1 : 0,
                    ...ripple.rgb, 1,
                ]));
            };
            if (level === 'off') {
                render(0.5);
                timer = setTimeout(stop, LIFE_SECONDS * 1000);
                return;
            }
            const started = performance.now();
            const tick = now => {
                const age = (now - started) / (LIFE_SECONDS * 1000);
                if (age >= 1) {
                    frame = null;
                    stop();
                    return;
                }
                render(Math.max(age, 0));
                frame = requestAnimationFrame(tick);
            };
            frame = requestAnimationFrame(tick);
        },
        dispose() {
            disposed = true;
            onEnd = null;
            stop();
            backend?.dispose();
            backend = null;
        },
    };
}
