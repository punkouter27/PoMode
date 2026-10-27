// The phone's docked player: once the mixer's own transport has scrolled out of view on the long
// analysis page, a slim bar at the foot of the screen carries Play/Pause and the key, so reading the
// statistics further down never means scrolling back up to stop the music. The bar is only shown
// while the real transport is out of view, so the two are never on screen together.
//
// It also keeps the ring round both Play buttons, which is the playhead's share of the song: the
// mixer already mirrors its clock onto data-mixer-time / data-mixer-duration every frame it plays,
// so the ring follows those attributes rather than running a clock of its own. With effects off it
// moves only when the transport stops or starts, so it still says where the song was left.

import * as prefs from '../shell/prefs.js';

const observers = new WeakMap();
const rings = new WeakMap();

export function watch(transport, dock) {
    unwatch(transport);
    if (!transport || !dock || typeof IntersectionObserver === 'undefined') {
        return;
    }
    const observer = new IntersectionObserver(([entry]) => {
        dock.dataset.docked = entry.isIntersecting ? '0' : '1';
    }, { threshold: 0 });
    observer.observe(transport);
    observers.set(transport, observer);
}

export function unwatch(transport) {
    observers.get(transport)?.disconnect();
    observers.delete(transport);
}

/// Fills the ring on `root`'s Play buttons (.mixer-play, .mixer-dock-play) through --pm-progress,
/// 0..1, mirrored as data-progress. The buttons are looked up on each change because the mixer's
/// markup swaps them between its loading, failed and ready states.
export function ring(root) {
    unring(root);
    if (!root || typeof MutationObserver === 'undefined') {
        return;
    }
    let moving = prefs.allowsMotion();
    const draw = () => {
        const duration = Number(root.dataset.mixerDuration);
        const time = Number(root.dataset.mixerTime);
        const progress = duration > 0 && Number.isFinite(time) ? Math.min(1, Math.max(0, time / duration)) : 0;
        const text = progress.toFixed(4);
        for (const button of root.querySelectorAll('.mixer-play, .mixer-dock-play')) {
            if (button.dataset.progress !== text) {
                button.style.setProperty('--pm-progress', text);
                button.dataset.progress = text;
            }
        }
    };
    const observer = new MutationObserver((records) => {
        if (moving || records.some((record) => record.attributeName !== 'data-mixer-time')) {
            draw();
        }
    });
    observer.observe(root, {
        attributes: true,
        attributeFilter: ['data-mixer-time', 'data-mixer-duration', 'data-mixer-status'],
    });
    const unsubscribe = prefs.subscribe(() => {
        moving = prefs.allowsMotion();
        draw();
    });
    rings.set(root, { observer, unsubscribe });
    draw();
}

export function unring(root) {
    const held = root ? rings.get(root) : null;
    if (!held) {
        return;
    }
    held.observer.disconnect();
    held.unsubscribe();
    rings.delete(root);
}
