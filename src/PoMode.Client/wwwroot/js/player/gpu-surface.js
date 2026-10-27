// The plumbing the analysis canvas's two GPU passes share (spectral-glass.js, heat-haze.js): getting
// a WebGPU device or, failing that, a WebGL2 context on an offscreen canvas, and the few WebGL2
// helpers both fallbacks need. Neither pass draws onto the page itself: each renders into its own
// canvas and canvas.js composites the result into the 2D canvas with drawImage, the same hand-off
// chroma-layer.js uses. Nothing here knows what is being drawn.

/// A WebGPU device, or null when the browser has none or the adapter refuses. The caller owns the
/// device and destroys it in its dispose.
export async function webgpuDevice() {
    if (!navigator.gpu) {
        return null;
    }
    try {
        const adapter = await navigator.gpu.requestAdapter();
        return adapter ? await adapter.requestDevice() : null;
    } catch {
        return null;
    }
}

/// A WebGPU context on a fresh offscreen canvas, configured for premultiplied output.
export function webgpuSurface(device) {
    const canvas = document.createElement('canvas');
    const context = canvas.getContext('webgpu');
    if (!context) {
        return null;
    }
    const format = navigator.gpu.getPreferredCanvasFormat();
    context.configure({ device, format, alphaMode: 'premultiplied' });
    return { canvas, context, format };
}

/// The full-screen triangle pair every pass draws with. Fragment shaders read @builtin(position),
/// so the vertex stage carries nothing else.
export const FULLSCREEN_WGSL = /* wgsl */ `
@vertex fn vs(@builtin(vertex_index) i: u32) -> @builtin(position) vec4f {
    var corners = array<vec2f, 6>(vec2f(-1, -1), vec2f(1, -1), vec2f(-1, 1), vec2f(-1, 1), vec2f(1, -1), vec2f(1, 1));
    return vec4f(corners[i], 0, 1);
}`;

/// A WebGL2 context on a fresh offscreen canvas, or null. Premultiplied, like the WebGPU path, so
/// canvas.js composites both the same way.
export function webgl2Surface() {
    const canvas = document.createElement('canvas');
    const gl = canvas.getContext('webgl2', { premultipliedAlpha: true, antialias: false, depth: false, stencil: false });
    if (!gl) {
        return null;
    }
    const vao = gl.createVertexArray();
    return { canvas, gl, vao };
}

const FULLSCREEN_GLSL = `#version 300 es
void main() {
    vec2 corners[6] = vec2[6](vec2(-1, -1), vec2(1, -1), vec2(-1, 1), vec2(-1, 1), vec2(1, -1), vec2(1, 1));
    gl_Position = vec4(corners[gl_VertexID], 0, 1);
}`;

/// Compiles one fragment shader against the shared full-screen vertex stage. Answers the program
/// and its uniform locations by name, or null when the driver rejects it.
export function glProgram(gl, fragment, uniforms) {
    const compile = (type, source) => {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        return shader;
    };
    const vs = compile(gl.VERTEX_SHADER, FULLSCREEN_GLSL);
    const fs = compile(gl.FRAGMENT_SHADER, fragment);
    const program = gl.createProgram();
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.linkProgram(program);
    gl.deleteShader(vs);
    gl.deleteShader(fs);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        gl.deleteProgram(program);
        return null;
    }
    const at = {};
    for (const name of uniforms) {
        at[name] = gl.getUniformLocation(program, name);
    }
    return { program, at };
}

/// An RGBA8 texture, linear-filtered and clamped, with a framebuffer to render into it.
export function glTarget(gl, width, height) {
    const texture = glTexture(gl, gl.CLAMP_TO_EDGE);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, width, height, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
    const framebuffer = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, framebuffer);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    return { texture, framebuffer, width, height };
}

export function glDeleteTarget(gl, target) {
    if (target) {
        gl.deleteTexture(target.texture);
        gl.deleteFramebuffer(target.framebuffer);
    }
}

/// A bound, linear-filtered texture with the given horizontal wrap (vertical always clamps).
export function glTexture(gl, wrapS) {
    const texture = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, wrapS);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    return texture;
}

/// A CSS colour as [r, g, b] in 0..1, resolved through a 2D context so any syntax the theme uses works.
export function rgb(colour) {
    const probe = document.createElement('canvas').getContext('2d');
    probe.fillStyle = colour || '#888';
    const value = probe.fillStyle;
    if (value.startsWith('#')) {
        return [1, 3, 5].map((i) => parseInt(value.slice(i, i + 2), 16) / 255);
    }
    const parts = value.match(/[\d.]+/g)?.map(Number) ?? [136, 136, 136];
    return parts.slice(0, 3).map((part) => part / 255);
}

/// True when the page is drawn dark: an explicit data-theme wins, otherwise the OS preference.
export function darkTheme() {
    const chosen = document.documentElement.dataset.theme;
    if (chosen === 'dark' || chosen === 'light') {
        return chosen === 'dark';
    }
    return window.matchMedia('(prefers-color-scheme: dark)').matches;
}
