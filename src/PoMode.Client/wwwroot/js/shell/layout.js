// Publishes the sticky header's height as --pm-header-height on <html>, so anything that sticks
// under it (the Mode Lab's transport bar) sits flush below it at any width. The header is one row on
// a desktop and two on a phone, and its height also moves with the safe-area inset; measuring is the
// only way to know it.

let observer = null;

export function watchHeader() {
    const header = document.querySelector('.app-header');
    if (!header || observer || typeof ResizeObserver === 'undefined') {
        return;
    }
    observer = new ResizeObserver(() => {
        document.documentElement.style.setProperty('--pm-header-height', `${header.getBoundingClientRect().height}px`);
    });
    observer.observe(header);
}

/// Tints the chrome with the song's key: --pm-key-hue drives the --pm-key* tokens (pm-tokens.css).
/// The hue is the tonic's pitch class times 30, decided by the page, so no musical choice is made here.
export function setKeyHue(hue) {
    document.documentElement.style.setProperty('--pm-key-hue', String(hue));
}
