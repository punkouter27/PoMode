// Small pure helpers the analysis canvas and its motion module both draw with: time to pixels, a
// binary search over time-sorted items, and the shapes and colours every lane shares.

export function clamp(value, min, max) {
    return Math.min(Math.max(value, min), max);
}

/// Index of the first item whose startSec is at or after `threshold`; items are sorted by startSec.
export function firstIndexFrom(items, threshold) {
    let low = 0;
    let high = items.length;
    while (low < high) {
        const mid = (low + high) >> 1;
        if (items[mid].startSec < threshold) {
            low = mid + 1;
        } else {
            high = mid;
        }
    }
    return low;
}

export function timeToX(state, seconds, width) {
    const span = state.viewEnd - state.viewStart;
    return span <= 0 ? 0 : ((seconds - state.viewStart) / span) * width;
}

export function colourForRole(state, role) {
    return state.colours.roles[role] || state.colours.roles.Outside;
}

export function roundedRect(ctx, x, y, width, height, radius) {
    const r = Math.max(Math.min(radius, width / 2, height / 2), 0);
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + width, y, x + width, y + height, r);
    ctx.arcTo(x + width, y + height, x, y + height, r);
    ctx.arcTo(x, y + height, x, y, r);
    ctx.arcTo(x, y, x + width, y, r);
    ctx.closePath();
}
