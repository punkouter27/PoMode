// The service worker, registered from a module so a browser without service workers simply skips it,
// and deferred (modules always are) so registration never competes with the WASM runtime the user is
// waiting on. A file rather than an inline script so the Content Security Policy can stay 'self'.
import('./pwa.js').then(pwa => pwa.register()).catch(() => { });
