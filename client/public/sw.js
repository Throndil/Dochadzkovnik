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

  if (event.data) {
    try {
      const payload = event.data.json();
      title = payload.title || title;
      body = payload.body || body;
      clickUrl = payload.clickUrl || clickUrl;
    } catch (e) {
      // Invalid JSON, fall back to default notification
      console.warn('Failed to parse push payload:', e);
      body = event.data.text() || body;
    }
  }

  const options = {
    body: body,
    icon: '/profistav_logo_192.png',
    badge: '/profistav_logo_192.png',
    requireInteraction: false,
    tag: 'sichtovnica-noactivity',
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
