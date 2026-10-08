// Bestie's phone app service worker: notifications (web push from Bestie on the computer), opening the app
// from one, and the last good copy of the app when the computer can't be reached.
const SHELL = 'bestie-shell-v1';

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', e => e.waitUntil(self.clients.claim()));

self.addEventListener('push', e => {
  let d = {};
  try { d = e.data ? e.data.json() : {}; } catch (_) { d = { body: e.data ? e.data.text() : '' }; }
  e.waitUntil((async () => {
    await self.registration.showNotification(d.title || 'Bestie', {
      body: d.body || '', icon: 'icon-180.png', badge: 'icon-180.png', tag: (d.tag || 'bestie') + '-' + Date.now(), data: { url: d.url || './' }
    });
    // the number on the home-screen icon: how many are waiting
    try { if (self.navigator.setAppBadge) self.navigator.setAppBadge((await self.registration.getNotifications()).length); } catch (_) {}
  })());
});

self.addEventListener('notificationclick', e => {
  e.notification.close();
  const url = new URL((e.notification.data && e.notification.data.url) || './', self.registration.scope).href;
  e.waitUntil((async () => {
    const wins = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const isFile = url.includes('/api/file');
    if (!isFile) for (const w of wins) { if ('focus' in w) { w.postMessage({ type: 'refresh' }); return w.focus(); } }
    return self.clients.openWindow(url);
  })());
});

// the app page: always fresh from the computer, but keep the last copy for when it's off or out of reach
self.addEventListener('fetch', e => {
  const req = e.request;
  if (req.method !== 'GET' || req.mode !== 'navigate') return;
  e.respondWith((async () => {
    try {
      const res = await fetch(req);
      if (res.ok) { const c = await caches.open(SHELL); c.put('shell', res.clone()); }
      return res;
    } catch (_) {
      const c = await caches.open(SHELL);
      return (await c.match('shell')) || new Response('<meta name="viewport" content="width=device-width"><body style="font:17px -apple-system,sans-serif;padding:60px 28px;background:#0f0f0f;color:#ececec">Can’t reach your computer. Make sure it’s on, with Bestie and Tailscale running.</body>', { headers: { 'Content-Type': 'text/html' } });
    }
  })());
});
