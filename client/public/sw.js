// Service Worker for Šichtovnica push notifications
// Handles install, activate, push, and notification click events

self.addEventListener('install', (event) => {
  console.log('Service Worker installing');
  self.skipWaiting();
});

self.addEventListener('activate', (event) => {
  console.log('Service Worker activating');
  self.clients.claim();
});

self.addEventListener('push', (event) => {
  console.log('Push event received:', event);

  let title = 'Šichtovnica';
  let body = 'Nové upozornenie';
  let clickUrl = '/kiosk';
  let tag = null;

  if (event.data) {
    try {
      const payload = event.data.json();
      title = payload.title || title;
      body = payload.body || body;
      clickUrl = payload.clickUrl || clickUrl;
      if (typeof payload.tag === 'string' && payload.tag.length > 0) tag = payload.tag;
    } catch (e) {
      // Invalid JSON, fall back to default notification
      console.warn('Failed to parse push payload:', e);
      body = event.data.text() || body;
    }
  }

  // Per W3C Notification spec, two notifications with the same `tag` collapse silently
  // (the new one replaces the old without re-alerting). To make every push visible to
  // the user we either honour a server-supplied tag (e.g. one per trigger type) or fall
  // back to a unique-per-event tag so successive notifications don't merge.
  const options = {
    body: body,
    icon: '/profistav_logo_192.png',
    badge: '/profistav_logo_192.png',
    requireInteraction: false,
    tag: tag || ('sichtovnica-' + Date.now()),
    data: {
      clickUrl: clickUrl
    }
  };

  event.waitUntil(
    self.registration.showNotification(title, options)
  );
});

self.addEventListener('notificationclick', (event) => {
  console.log('Notification clicked:', event);

  event.notification.close();

  const clickUrl = event.notification.data?.clickUrl || '/kiosk';

  event.waitUntil(
    self.clients.matchAll({ type: 'window' }).then((clientList) => {
      // Check if there's already a window open on the app's origin
      for (let i = 0; i < clientList.length; i++) {
        const client = clientList[i];
        if (client.url === self.location.origin + '/' || client.url.startsWith(self.location.origin + '/')) {
          // Found a client on the app origin, focus and navigate it
          client.focus();
          // Post message to the client to navigate
          client.postMessage({
            type: 'navigate',
            url: clickUrl
          });
          return;
        }
      }
      // No existing window, open a new one
      return self.clients.openWindow(clickUrl);
    })
  );
});
