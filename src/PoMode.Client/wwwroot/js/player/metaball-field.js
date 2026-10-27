// The home-note view's consonance field: a signed-distance picture drawn under the bodies of the
// notes that are sounding together, so a chord reads as one shape rather than a scatter of lit dots.
// Every pair of sounding bodies is joined by a neck whose width and smoothing follow how consonant
// the two pitches are: a fifth or a fourth melts into one blob, a third or sixth keeps a waist, and a
// second, a seventh or the tritone pinches to a thread with a crease across it. The table is the
// usual interval-class ranking (Plomp-Levelt roughness, coarsened); nothing about the music is decided
// here — mode-gravity.js hands over which bodies sound, where they sit and their interval classes.
//
// WebGPU when available (the chroma layer's pattern, but redrawn per frame), WebGL2 otherwise. With
// neither the field is simply absent and the 2D bodies on top carry the view alone.

const MAX_BODIES = 8;
const MAX_PAIRS = (MAX_BODIES * (MAX_BODIES - 1)) / 2;
const FLOATS = 4 + (MAX_BODIES * 8) + (MAX_PAIRS * 8);

/// How smoothly two pitch classes merge, by interval class (0..6): unison, m2/M7, M2/m7, m3/M6,
/// M3/m6, P4/P5, tritone. 1 melts into one body, near 0 pinches.
const CONSONANCE = [1, 0.05, 0.25, 0.6, 0.65, 0.9, 0.1];

export function consonance(pitchClassA, pitchClassB) {
    const span = (((pitchClassA - pitchClassB) % 12) + 12) % 12;
    return CONSONANCE[Math.min(span, 12 - span)];
}

/// Any CSS colour the browser understands as 0..1 RGB, through a 1x1 canvas. Theme changes only.
const probe = (() => {
    let context = null;
    return css => {
        try {
            context ??= Object.assign(document.createElement('canvas'), { width: 1, height: 1 })
                .getContext('2d', { willReadFrequently: true });
            context.clearRect(0, 0, 1, 1);
            context.fillStyle = '#808080';
            context.fillStyle = css || '#808080';
            context.fillRect(0, 0, 1, 1);
            const [r, g, b] = context.getImageData(0, 0, 1, 1).data;
            return [r / 255, g / 255, b / 255];
        } catch {
            return [0.5, 0.5, 0.5];
        }
    };
})();

export const toRgb = probe;

// The same field in both shading languages. Positions are CSS pixels, y down. Each body is a disc
// scaled by how far it has bonded in; each pair adds a neck whose waist and smooth-union radius
// follow its consonance, and a dissonant neck gets a dark seam across its middle.
const WGSL = /* wgsl */ `
struct Field {
    size: vec4f,
    bodies: array<vec4f, ${MAX_BODIES}>,
    colours: array<vec4f, ${MAX_BODIES}>,
    pairEnds: array<vec4f, ${MAX_PAIRS}>,
    pairShape: array<vec4f, ${MAX_PAIRS}>,
};
@group(0) @binding(0) var<uniform> field: Field;

struct Out { @builtin(position) position: vec4f, @location(0) uv: vec2f };

@vertex fn vs(@builtin(vertex_index) i: u32) -> Out {
    var corners = array<vec2f, 3>(vec2f(-1, -1), vec2f(3, -1), vec2f(-1, 3));
    var out: Out;
    out.position = vec4f(corners[i], 0, 1);
    out.uv = vec2f((corners[i].x + 1) * 0.5, (1 - corners[i].y) * 0.5);
    return out;
}

fn smin(a: f32, b: f32, k: f32) -> f32 {
    let h = max(k - abs(a - b), 0.0) / k;
    return min(a, b) - (h * h * k * 0.25);
}

@fragment fn fs(input: Out) -> @location(0) vec4f {
    let p = input.uv * field.size.xy;
    var d = 1e5;
    var colour = vec3f(0.0);
    var weight = 1e-4;
    for (var i = 0; i < i32(field.size.z); i++) {
        let body = field.bodies[i];
        let di = length(p - body.xy) - (body.z * body.w);
        d = smin(d, di, 4.0);
        let w = body.w / (1.0 + max(di, 0.0) * max(di, 0.0) * 0.01);
        colour += field.colours[i].rgb * w;
        weight += w;
    }
    var crease = 0.0;
    for (var j = 0; j < i32(field.size.w); j++) {
        let a = field.pairEnds[j].xy;
        let b = field.pairEnds[j].zw;
        let shape = field.pairShape[j];
        let ba = b - a;
        let pa = p - a;
        let h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-3), 0.0, 1.0);
        let waist = mix(0.16, 0.8, shape.z);
        let radius = mix(shape.x, shape.y, h) * (1.0 - ((1.0 - waist) * 4.0 * h * (1.0 - h))) * shape.w;
        let dp = length(pa - (ba * h)) - radius;
        let k = max(mix(1.5, 0.5 * min(shape.x, shape.y), shape.z) * shape.w, 0.01);
        d = smin(d, dp, k);
        let across = abs(dot(p - ((a + b) * 0.5), normalize(ba)));
        crease = max(crease, (1.0 - shape.z) * shape.w * (1.0 - smoothstep(0.6, 2.2, across)) * step(dp, 0.5));
    }
    let inside = 1.0 - smoothstep(-1.0, 1.0, d);
    let core = clamp(-d / 18.0, 0.0, 1.0);
    let rim = 1.0 - smoothstep(0.0, 1.6, abs(d));
    let glow = exp(-max(d, 0.0) / 7.0) * 0.18 * (1.0 - inside);
    let alpha = clamp(((inside * (0.22 + (0.2 * core))) + (rim * 0.22) + glow) * (1.0 - (0.85 * crease)), 0.0, 0.62);
    return vec4f((colour / weight) * alpha, alpha);
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
uniform vec4 uBodies[${MAX_BODIES}];
uniform vec4 uColours[${MAX_BODIES}];
uniform vec4 uPairEnds[${MAX_PAIRS}];
uniform vec4 uPairShape[${MAX_PAIRS}];
out vec4 outColour;

float smin(float a, float b, float k) {
    float h = max(k - abs(a - b), 0.0) / k;
    return min(a, b) - (h * h * k * 0.25);
}

void main() {
    vec2 p = vUv * uSize.xy;
    float d = 1e5;
    vec3 colour = vec3(0.0);
    float weight = 1e-4;
    for (int i = 0; i < ${MAX_BODIES}; i++) {
        if (i >= int(uSize.z)) break;
        vec4 body = uBodies[i];
        float di = length(p - body.xy) - (body.z * body.w);
        d = smin(d, di, 4.0);
        float w = body.w / (1.0 + max(di, 0.0) * max(di, 0.0) * 0.01);
        colour += uColours[i].rgb * w;
        weight += w;
    }
    float crease = 0.0;
    for (int j = 0; j < ${MAX_PAIRS}; j++) {
        if (j >= int(uSize.w)) break;
        vec2 a = uPairEnds[j].xy;
        vec2 b = uPairEnds[j].zw;
        vec4 shape = uPairShape[j];
        vec2 ba = b - a;
        vec2 pa = p - a;
        float h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-3), 0.0, 1.0);
        float waist = mix(0.16, 0.8, shape.z);
        float radius = mix(shape.x, shape.y, h) * (1.0 - ((1.0 - waist) * 4.0 * h * (1.0 - h))) * shape.w;
        float dp = length(pa - (ba * h)) - radius;
        float k = max(mix(1.5, 0.5 * min(shape.x, shape.y), shape.z) * shape.w, 0.01);
        d = smin(d, dp, k);
        float across = abs(dot(p - ((a + b) * 0.5), normalize(ba)));
        crease = max(crease, (1.0 - shape.z) * shape.w * (1.0 - smoothstep(0.6, 2.2, across)) * step(dp, 0.5));
    }
    float inside = 1.0 - smoothstep(-1.0, 1.0, d);
    float core = clamp(-d / 18.0, 0.0, 1.0);
    float rim = 1.0 - smoothstep(0.0, 1.6, abs(d));
    float glow = exp(-max(d, 0.0) / 7.0) * 0.18 * (1.0 - inside);
    float alpha = clamp(((inside * (0.22 + (0.2 * core))) + (rim * 0.22) + glow) * (1.0 - (0.85 * crease)), 0.0, 0.62);
    outColour = vec4((colour / weight) * alpha, alpha);
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

/// Packs the frame into the uniform block both shaders read. `bodies` are { x, y, r, bond, rgb } in
/// CSS pixels; `pairs` are { a, b, consonance } indexes into it.
function pack(width, height, bodies, pairs) {
    const data = new Float32Array(FLOATS);
    const count = Math.min(bodies.length, MAX_BODIES);
    const kept = pairs.filter(pair => pair.a < count && pair.b < count).slice(0, MAX_PAIRS);
    data.set([width, height, count, kept.length], 0);
    for (let i = 0; i < count; i++) {
        const body = bodies[i];
        data.set([body.x, body.y, body.r, body.bond], 4 + (i * 4));
        data.set([...body.rgb, 1], 4 + (MAX_BODIES * 4) + (i * 4));
    }
    kept.forEach((pair, j) => {
        const a = bodies[pair.a];
        const b = bodies[pair.b];
        data.set([a.x, a.y, b.x, b.y], 4 + (MAX_BODIES * 8) + (j * 4));
        data.set([a.r, b.r, pair.consonance, Math.min(a.bond, b.bond)], 4 + (MAX_BODIES * 8) + (MAX_PAIRS * 4) + (j * 4));
    });
    return data;
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
        const uniform = device.createBuffer({ size: FLOATS * 4, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        const bindings = device.createBindGroup({
            layout: pipeline.getBindGroupLayout(0),
            entries: [{ binding: 0, resource: { buffer: uniform } }],
        });
        return {
            name: 'webgpu',
            draw(data) {
                device.queue.writeBuffer(uniform, 0, data);
                const encoder = device.createCommandEncoder();
                const pass = encoder.beginRenderPass({
                    colorAttachments: [{
                        view: context.getCurrentTexture().createView(),
                        clearValue: { r: 0, g: 0, b: 0, a: 0 }, loadOp: 'clear', storeOp: 'store',
                    }],
                });
                pass.setPipeline(pipeline);
                pass.setBindGroup(0, bindings);
                pass.draw(3);
                pass.end();
                device.queue.submit([encoder.finish()]);
            },
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
        const at = name => gl.getUniformLocation(program, name);
        const u = { size: at('uSize'), bodies: at('uBodies'), colours: at('uColours'), ends: at('uPairEnds'), shape: at('uPairShape') };
        const pairsAt = 4 + (MAX_BODIES * 8);
        return {
            name: 'webgl2',
            draw(data) {
                gl.viewport(0, 0, canvas.width, canvas.height);
                gl.clearColor(0, 0, 0, 0);
                gl.clear(gl.COLOR_BUFFER_BIT);
                gl.useProgram(program);
                gl.bindVertexArray(vao);
                gl.uniform4fv(u.size, data.subarray(0, 4));
                gl.uniform4fv(u.bodies, data.subarray(4, 4 + (MAX_BODIES * 4)));
                gl.uniform4fv(u.colours, data.subarray(4 + (MAX_BODIES * 4), pairsAt));
                gl.uniform4fv(u.ends, data.subarray(pairsAt, pairsAt + (MAX_PAIRS * 4)));
                gl.uniform4fv(u.shape, data.subarray(pairsAt + (MAX_PAIRS * 4)));
                gl.drawArrays(gl.TRIANGLES, 0, 3);
            },
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

/// Starts a field on `canvas`. Answers { ready, renderer(), render(bodies, pairs, scale), clear(),
/// dispose() }; `ready` resolves to the renderer's name, or 'none' when neither API is available.
export function createField(canvas) {
    let backend = null;
    let disposed = false;
    let blank = true;
    const ready = (async () => {
        backend = (await startGpu(canvas)) ?? startGl(canvas);
        if (disposed) {
            backend?.dispose();
            backend = null;
        }
        return backend?.name ?? 'none';
    })();
    return {
        ready,
        renderer: () => backend?.name ?? 'none',
        /// `scale` is device pixels per CSS pixel: the subtle level draws the field at half resolution.
        render(bodies, pairs, scale) {
            if (!backend) {
                return;
            }
            const { width, height } = sizeCanvas(canvas, scale);
            backend.draw(pack(width, height, bodies, pairs));
            blank = bodies.length === 0;
        },
        clear() {
            if (backend && !blank) {
                const { width, height } = sizeCanvas(canvas, 1);
                backend.draw(pack(width, height, [], []));
                blank = true;
            }
        },
        dispose() {
            disposed = true;
            backend?.dispose();
            backend = null;
        },
    };
}
