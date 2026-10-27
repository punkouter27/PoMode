// Tension heat-haze: a post pass over the note lane around the playhead. The server measures, per
// window, how much of the singing sits off the chord under it (the tension ribbon canvas.js draws
// along the lane's foot); where that share is high the air over the notes shimmers like heat off a
// road and the colours split a little at the edges, and where the singing sits on the chord the
// lane stays clear. canvas.js hands over a strength profile across the region (each window's value,
// falling off from the playhead's window), so the distortion peaks exactly over the stretch of
// ribbon that is glowing. Its shimmer only advances while the audio plays; paused, it holds still.
//
// The region is copied out of the 2D canvas into a texture, displaced on the GPU and handed back
// for canvas.js to draw over the same pixels. WebGPU first, WebGL2 otherwise, nothing without either.

import {
    webgpuDevice, webgpuSurface, webgl2Surface, FULLSCREEN_WGSL, glProgram, glTexture,
} from './gpu-surface.js';

/// Strength samples across the region, left to right.
export const PROFILE = 64;

const HAZE_WGSL = /* wgsl */ `
struct Haze {
    size: vec2f,
    motion: vec4f,                      // x playback seconds, y displacement px at full strength, z pixel ratio
    profile: array<vec4f, ${PROFILE / 4}>,   // ${PROFILE} strengths, four to a vector
};

@group(0) @binding(0) var scene: texture_2d<f32>;
@group(0) @binding(1) var sceneSampler: sampler;
@group(0) @binding(2) var<uniform> haze: Haze;

fn strength(i: u32) -> f32 {
    return haze.profile[i / 4u][i % 4u];
}

fn strengthAt(x: f32) -> f32 {
    let f = clamp(x, 0.0, 1.0) * ${PROFILE - 1}.0;
    let i = u32(floor(f));
    return mix(strength(i), strength(min(i + 1u, ${PROFILE - 1}u)), fract(f));
}

// Three incommensurate ripples: smooth, cheap, and never visibly periodic over a lane's width.
fn shimmer(p: vec2f) -> f32 {
    return sin(p.x * 1.7 + sin(p.y * 2.3)) * 0.5 + sin(p.y * 3.1 - p.x * 0.9 + 1.3) * 0.35
        + sin((p.x + p.y) * 4.7) * 0.15;
}

@fragment fn fs(@builtin(position) position: vec4f) -> @location(0) vec4f {
    let p = position.xy;
    let uv = p / haze.size;
    // Eased out at the lane's top and bottom edges, so nothing is pulled in from outside it.
    let edge = smoothstep(0.0, 0.12, uv.y) * (1.0 - smoothstep(0.88, 1.0, uv.y));
    let s = strengthAt(uv.x) * edge;
    let css = p / haze.motion.z;
    // The pattern drifts upward with playback time, as hot air rises.
    let q = vec2f(css.x / 18.0, css.y / 12.0 + haze.motion.x * 2.4);
    let offset = vec2f(shimmer(q), shimmer(q.yx + vec2f(5.2, 1.3)) * 0.6) * haze.motion.y * s;
    let split = vec2f(haze.motion.y * 0.4 * s, 0.0);
    let at = p + offset;
    let red = textureSampleLevel(scene, sceneSampler, (at + split) / haze.size, 0.0);
    let mid = textureSampleLevel(scene, sceneSampler, at / haze.size, 0.0);
    let blue = textureSampleLevel(scene, sceneSampler, (at - split) / haze.size, 0.0);
    return vec4f(red.r, mid.g, blue.b, mid.a);
}`;

const HAZE_GLSL = `#version 300 es
precision highp float;
uniform vec2 size;
uniform vec4 motion;
uniform float profile[${PROFILE}];
uniform sampler2D scene;
out vec4 colour;

float strengthAt(float x) {
    float f = clamp(x, 0.0, 1.0) * ${PROFILE - 1}.0;
    int i = int(floor(f));
    return mix(profile[i], profile[min(i + 1, ${PROFILE - 1})], fract(f));
}

float shimmer(vec2 p) {
    return sin(p.x * 1.7 + sin(p.y * 2.3)) * 0.5 + sin(p.y * 3.1 - p.x * 0.9 + 1.3) * 0.35
        + sin((p.x + p.y) * 4.7) * 0.15;
}

void main() {
    vec2 p = vec2(gl_FragCoord.x, size.y - gl_FragCoord.y);
    vec2 uv = p / size;
    float edge = smoothstep(0.0, 0.12, uv.y) * (1.0 - smoothstep(0.88, 1.0, uv.y));
    float s = strengthAt(uv.x) * edge;
    vec2 css = p / motion.z;
    vec2 q = vec2(css.x / 18.0, css.y / 12.0 + motion.x * 2.4);
    vec2 offset = vec2(shimmer(q), shimmer(q.yx + vec2(5.2, 1.3)) * 0.6) * motion.y * s;
    vec2 split = vec2(motion.y * 0.4 * s, 0.0);
    vec2 at = p + offset;
    vec4 red = textureLod(scene, (at + split) / size, 0.0);
    vec4 mid = textureLod(scene, at / size, 0.0);
    vec4 blue = textureLod(scene, (at - split) / size, 0.0);
    colour = vec4(red.r, mid.g, blue.b, mid.a);
}`;

/// One haze per canvas. `onReady` fires once a backend exists, so the caller can redraw.
export function create(onReady) {
    const haze = { backend: null, disposed: false };
    (async () => {
        const backend = (await gpuBackend()) ?? glBackend();
        if (haze.disposed) {
            backend?.destroy();
            return;
        }
        haze.backend = backend;
        if (backend) {
            onReady?.();
        }
    })();

    return {
        get ready() {
            return haze.backend !== null;
        },
        /// Displaces `rect` (device pixels) of `source` by `profile` (PROFILE strengths in 0..1 across
        /// it). `seconds` is the playback clock the shimmer follows, `amplitude` the displacement in
        /// device pixels at full strength. Answers an image of exactly the rect's size to draw back
        /// over it in this same task, or null.
        render(source, rect, profile, seconds, amplitude, ratio) {
            if (!haze.backend || rect.width < 1 || rect.height < 1) {
                return null;
            }
            try {
                return haze.backend.draw(source, rect, profile, seconds, amplitude, ratio);
            } catch {
                return null;
            }
        },
        dispose() {
            haze.disposed = true;
            haze.backend?.destroy();
            haze.backend = null;
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
        const module = device.createShaderModule({ code: FULLSCREEN_WGSL + HAZE_WGSL });
        const pipeline = device.createRenderPipeline({
            layout: 'auto',
            vertex: { module, entryPoint: 'vs' },
            fragment: { module, entryPoint: 'fs', targets: [{ format: surface.format }] },
            primitive: { topology: 'triangle-list' },
        });
        // size, padding to the vec4's alignment, motion, then the profile.
        const uniforms = new Float32Array(8 + PROFILE);
        const uniform = device.createBuffer({ size: uniforms.byteLength, usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST });
        const sampler = device.createSampler({ magFilter: 'linear', minFilter: 'linear' });
        let scene = null;
        let bindings = null;

        const backend = {
            lost: false,
            draw(source, rect, profile, seconds, amplitude, ratio) {
                if (backend.lost) {
                    return null;
                }
                const { x, y, width, height } = rect;
                if (!scene || scene.width !== width || scene.height !== height) {
                    scene?.destroy();
                    scene = device.createTexture({
                        size: [width, height], format: 'rgba8unorm',
                        usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST | GPUTextureUsage.RENDER_ATTACHMENT,
                    });
                    bindings = device.createBindGroup({
                        layout: pipeline.getBindGroupLayout(0),
                        entries: [
                            { binding: 0, resource: scene.createView() },
                            { binding: 1, resource: sampler },
                            { binding: 2, resource: { buffer: uniform } },
                        ],
                    });
                    surface.canvas.width = width;
                    surface.canvas.height = height;
                }
                device.queue.copyExternalImageToTexture(
                    { source, origin: { x, y } }, { texture: scene, premultipliedAlpha: true }, [width, height]);
                uniforms[0] = width;
                uniforms[1] = height;
                uniforms[4] = seconds;
                uniforms[5] = amplitude;
                uniforms[6] = ratio;
                uniforms.set(profile, 8);
                device.queue.writeBuffer(uniform, 0, uniforms);
                const encoder = device.createCommandEncoder();
                const pass = encoder.beginRenderPass({
                    colorAttachments: [{
                        view: surface.context.getCurrentTexture().createView(),
                        clearValue: { r: 0, g: 0, b: 0, a: 0 }, loadOp: 'clear', storeOp: 'store',
                    }],
                });
                pass.setPipeline(pipeline);
                pass.setBindGroup(0, bindings);
                pass.draw(6);
                pass.end();
                device.queue.submit([encoder.finish()]);
                return surface.canvas;
            },
            destroy() {
                scene?.destroy();
                uniform.destroy();
                device.destroy();
            },
        };
        device.lost.then(() => {
            backend.lost = true;
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
    const program = glProgram(gl, HAZE_GLSL, ['size', 'motion', 'profile', 'scene']);
    if (!program) {
        return null;
    }
    const scene = glTexture(gl, gl.CLAMP_TO_EDGE);

    return {
        draw(source, rect, profile, seconds, amplitude, ratio) {
            if (gl.isContextLost()) {
                return null;
            }
            const { x, y, width, height } = rect;
            if (canvas.width !== width || canvas.height !== height) {
                canvas.width = width;
                canvas.height = height;
            }
            // WebGL2 can upload a sub-rectangle of a canvas: the skips pick its corner.
            gl.bindTexture(gl.TEXTURE_2D, scene);
            gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, true);
            gl.pixelStorei(gl.UNPACK_SKIP_PIXELS, x);
            gl.pixelStorei(gl.UNPACK_SKIP_ROWS, y);
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, width, height, 0, gl.RGBA, gl.UNSIGNED_BYTE, source);
            gl.pixelStorei(gl.UNPACK_SKIP_PIXELS, 0);
            gl.pixelStorei(gl.UNPACK_SKIP_ROWS, 0);

            gl.bindFramebuffer(gl.FRAMEBUFFER, null);
            gl.viewport(0, 0, width, height);
            gl.clearColor(0, 0, 0, 0);
            gl.clear(gl.COLOR_BUFFER_BIT);
            gl.useProgram(program.program);
            gl.uniform2f(program.at.size, width, height);
            gl.uniform4f(program.at.motion, seconds, amplitude, ratio, 0);
            gl.uniform1fv(program.at.profile, profile);
            gl.activeTexture(gl.TEXTURE0);
            gl.bindTexture(gl.TEXTURE_2D, scene);
            gl.uniform1i(program.at.scene, 0);
            gl.bindVertexArray(vao);
            gl.drawArrays(gl.TRIANGLES, 0, 6);
            return canvas;
        },
        destroy() {
            gl.deleteTexture(scene);
            gl.deleteProgram(program.program);
            gl.deleteVertexArray(vao);
            gl.getExtension('WEBGL_lose_context')?.loseContext();
        },
    };
}
