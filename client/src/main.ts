import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { App } from './app/app';
import { registerLocaleData } from '@angular/common';
import localeSk from '@angular/common/locales/sk';

registerLocaleData(localeSk);

// Register service worker for push notifications
if ('serviceWorker' in navigator && !location.hostname.includes('localhost')) {
  navigator.serviceWorker.register('/sw.js', { scope: '/' })
    .then(() => console.log('Service Worker registered'))
    .catch((err) => console.error('Service Worker registration failed:', err));
}

bootstrapApplication(App, appConfig)
  .catch((err) => console.error(err));
