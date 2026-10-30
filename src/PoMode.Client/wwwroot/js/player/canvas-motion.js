// The analysis canvas's motion, split out of canvas.js: the reveal (notes springing onto their rows,
// the ink sweep over a hum take), the onset bursts and the note drop. Each one moves a fact the
// analysis measured (docs/ui-rules.md, "Effects that carry data"); canvas.js decides when they run.

import { clamp, firstIndexFrom, timeToX, colourForRole, roundedRect } from './canvas-geometry.js';

/// The result reveal's timing, in seconds. Each note waits for its turn by how far across the view
/// it sits, so the transcription arrives left to right as it was sung; chords land a beat behind the
/// notes. With `ink` a colour front then sweeps the same way (see reveal()).
export const REVEAL = {
    noteSpread: 0.45,
    noteRise: 0.6,
    chordDelay: 0.25,
    chordSpread: 0.45,
    chordDrop: 0.4,
    inkStart: 1.0,
    inkSweep: 1.1,
};

/// Where the reveal is, or null when none is running (and the moment it finishes, marks it done).
/// `inkX` is the colour front's pixel position; notes left of it are in their role colours.
export function revealClock(state, now, width) {
    const reveal = state.reveal;
    if (!reveal) {
        return null;
    }
    const t = (now - reveal.start) / 1000;
    const end = reveal.ink ? REVEAL.inkStart + REVEAL.inkSweep : REVEAL.noteSpread + REVEAL.noteRise + 0.05;
    if (t >= end) {
        state.reveal = null;
        state.canvas.dataset.reveal = 'done';
        return null;
    }
    const inkX = reveal.ink ? clamp((t - REVEAL.inkStart) / REVEAL.inkSweep, 0, 1) * width : width;
    return { t, ink: reveal.ink, inkX };
}

/// 0..1 progress of one item whose turn comes `delay` seconds in and lasts `span`.
export function revealProgress(t, delay, span) {
    return clamp((t - delay) / span, 0, 1);
}

/// A lightly underdamped spring from 0 to 1: it overshoots its row a little and settles, which is
/// what makes the notes read as landing rather than fading in.
export function spring(progress) {
    return progress >= 1 ? 1 : 1 - (Math.exp(-5 * progress) * Math.cos(9 * progress));
}

/// The ink front: a soft band of the key colour where the verdict is being painted on.
export function drawInkFront(state, ctx, x, height) {
    if (x <= 0 || x >= Math.max(state.canvas.clientWidth, 1)) {
        return;
    }
    const band = 18;
    const gradient = ctx.createLinearGradient(x - band, 0, x + 2, 0);
    gradient.addColorStop(0, 'rgba(0,0,0,0)');
    gradient.addColorStop(1, state.colours.key);
    ctx.globalAlpha = 0.45;
    ctx.fillStyle = gradient;
    ctx.fillRect(x - band, 0, band + 2, height);
    ctx.globalAlpha = 1;
}

/// Spawns particle bursts for notes the playhead just crossed, and ages the pool. Particles live
/// in time/pitch space so panning and zooming move them with the notes. Capped hard so a dense
/// passage cannot drop the frame rate; skipped entirely under prefers-reduced-motion.
export function updateParticles(state, dt, laneHeight) {
    if (state.reducedMotion) {
        state.lastPlayhead = state.playhead;
        return;
    }
    const model = state.model;
    const advanced = state.playhead - state.lastPlayhead;
    if (advanced > 0 && advanced < 0.5 && state.particles.length < 200) {
        const first = firstIndexFrom(model.notes, state.lastPlayhead);
        for (let index = first; index < model.notes.length; index++) {
            const note = model.notes[index];
            if (note.startSec > state.playhead) {
                break;
            }
            // A longer note throws a bigger burst, and a note the mode's verdict rests on (the
            // evidence the "Why this mode?" readout cites) throws the most: the burst is the note's
            // weight in the analysis, not a flourish.
            const count = note.evidence ? 9 : 3 + Math.min(4, Math.round(note.durationSec * 4));
            const size = 1.6 + Math.min(2, note.durationSec * 2);
            for (let i = 0; i < count && state.particles.length < 240; i++) {
                state.particles.push({
                    t: note.startSec,
                    midi: note.midiPitch,
                    vt: 0.06 * ((i % 3) - 1),
                    vMidi: 2.2 * (((i * 37) % 100) / 100 - 0.5),
                    age: 0,
                    life: 0.55,
                    role: note.role,
                    size,
                });
            }
        }

        // Chord-change bursts: a taller column pop when the playhead crosses a chord boundary,
        // spread across the lane's pitch range so the change reads at any zoom.
        const firstChord = firstIndexFrom(model.chords, state.lastPlayhead);
        for (let index = firstChord; index < model.chords.length; index++) {
            const chord = model.chords[index];
            if (chord.startSec > state.playhead) {
                break;
            }
            for (let i = 0; i < 10 && state.particles.length < 240; i++) {
                state.particles.push({
                    t: chord.startSec,
                    midi: model.minPitch + ((model.maxPitch - model.minPitch) * (i / 9)),
                    vt: 0.04 * ((i % 3) - 1),
                    vMidi: 3 * (((i * 53) % 100) / 100 - 0.5),
                    age: 0,
                    life: 0.7,
                    role: 'ChordTone',
                });
            }
        }
    }
    state.lastPlayhead = state.playhead;

    for (let i = state.particles.length - 1; i >= 0; i--) {
        const particle = state.particles[i];
        particle.age += dt;
        if (particle.age >= particle.life) {
            state.particles.splice(i, 1);
        }
    }
}

export function drawParticles(state, ctx, width, laneHeight) {
    if (state.particles.length === 0) {
        return;
    }
    const model = state.model;
    const pitchSpan = Math.max(model.maxPitch - model.minPitch + 1, 1);
    const rowHeight = laneHeight / pitchSpan;

    for (const particle of state.particles) {
        const progress = particle.age / particle.life;
        const t = particle.t + (particle.vt * particle.age);
        const midi = particle.midi + (particle.vMidi * particle.age);
        if (t < state.viewStart || t > state.viewEnd) {
            continue;
        }
        const x = timeToX(state, t, width);
        const y = ((model.maxPitch - midi) + 0.5) * rowHeight;
        if (y < 0 || y > laneHeight) {
            continue;
        }
        ctx.globalAlpha = (1 - progress) * 0.7;
        ctx.fillStyle = colourForRole(state, particle.role);
        ctx.beginPath();
        ctx.arc(x, y, (particle.size ?? 2.2) * (1 - (progress * 0.5)), 0, Math.PI * 2);
        ctx.fill();
    }
    ctx.globalAlpha = 1;
}

/// End-of-song easter egg: the visible note capsules become falling, bouncing bodies for a few
/// seconds, then fade. Purely decorative; any seek, play, or new model clears it instantly.
export function updateDrop(state, dt, width, laneHeight) {
    const drop = state.drop;
    drop.age += dt;
    for (const body of drop.bodies) {
        body.vy += 900 * dt; // gravity, px/s²
        body.y += body.vy * dt;
        body.x += body.vx * dt;
        body.spin += body.vSpin * dt;
        const floor = laneHeight - body.h;
        if (body.y > floor) {
            body.y = floor;
            body.vy = -body.vy * 0.45; // lossy bounce settles quickly
            body.vx *= 0.8;
        }
    }
    if (drop.age > 4.5) {
        state.drop = null; // fully faded; normal drawing resumes
    }
}

export function drawDrop(state, ctx) {
    const drop = state.drop;
    const fade = clamp(1 - ((drop.age - 3) / 1.5), 0, 1);
    ctx.globalAlpha = fade;
    for (const body of drop.bodies) {
        ctx.save();
        ctx.translate(body.x + (body.w / 2), body.y + (body.h / 2));
        ctx.rotate(body.spin);
        ctx.fillStyle = colourForRole(state, body.role);
        roundedRect(ctx, -body.w / 2, -body.h / 2, body.w, body.h, Math.min(3, body.h / 2));
        ctx.fill();
        ctx.restore();
    }
    ctx.globalAlpha = 1;
}
