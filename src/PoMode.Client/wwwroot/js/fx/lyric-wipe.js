// The sung line's karaoke wipe: the highlight fills the line from left to right over exactly the time
// its chord lasts, so the eye sees how long is left to sing it. It animates a fact (time passing under
// the chord), so it stops when the effects are off or the system asks for reduced motion; the line is
// then highlighted whole by its CSS.

/// element: the current lyric line; seconds: how long its chord sounds.
export function wipe(element, seconds) {
    if (!element || !(seconds > 0)) return;
    try {
        if (document.documentElement.dataset.fx === 'off' || matchMedia('(prefers-reduced-motion: reduce)').matches) return;
        for (const running of element.getAnimations?.() ?? []) running.cancel();
        element.animate(
            [{ backgroundSize: '0% 100%' }, { backgroundSize: '100% 100%' }],
            { duration: seconds * 1000, easing: 'linear', fill: 'forwards' });
    } catch { }
}
