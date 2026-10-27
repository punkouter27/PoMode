// Detents on a slider: the values a control has a reason to rest on (the tempo a song measured, its
// half and its double) are drawn as marks under the track, and crossing one is felt and heard once.
// The marks are positioned here because their place is a fraction of the track the markup cannot
// state without an inline style; the snapping itself is the component's, in C#.

import * as prefs from './prefs.js';
import { click } from './sfx.js';

/// Moves every [data-at] mark inside `container` to its fraction (0..1) of the track.
export function place(container) {
    for (const mark of container?.querySelectorAll('[data-at]') ?? []) {
        mark.style.setProperty('--pm-at', mark.dataset.at);
    }
}

/// A detent was crossed: sfx's click (it checks the sound setting itself) and an 8 ms buzz where
/// the effect level allows one.
export function cross() {
    click();
    try {
        if (prefs.allowsMotion() && typeof navigator.vibrate === 'function') {
            navigator.vibrate(8);
        }
    } catch {
        // A refused pattern is nothing to report.
    }
}
