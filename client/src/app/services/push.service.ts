import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';

@Injectable({
  providedIn: 'root'
})
export class PushService {
  private http = inject(HttpClient);
  private swRegistration: ServiceWorkerRegistration | null = null;
  private readonly baseUrl = `${environment.apiUrl}/notifications`;

  async registerServiceWorker() {
    if (!('serviceWorker' in navigator)) {
      console.warn('Service Workers not supported');
      return false;
    }

    try {
      this.swRegistration = await navigator.serviceWorker.register('/sw.js', {
        scope: '/',
      });
      console.log('Service Worker registered');
      return true;
    } catch (error) {
      console.error('Service Worker registration failed:', error);
      return false;
    }
  }

  isSupported(): boolean {
    return (
      'serviceWorker' in navigator &&
      'PushManager' in window &&
      'Notification' in window
    );
  }

  currentPermission(): NotificationPermission {
    return Notification.permission;
  }

  async requestPermissionAndSubscribe(employeeId: number, pin: string): Promise<boolean> {
    if (!this.isSupported()) {
      console.warn('Push not supported on this browser');
      return false;
    }

    // Register service worker if not already done
    if (!this.swRegistration) {
      const registered = await this.registerServiceWorker();
      if (!registered) return false;
    }

    // Request permission
    if (Notification.permission === 'denied') {
      console.warn('Notification permission denied');
      return false;
    }

    if (Notification.permission !== 'granted') {
      try {
        const permission = await Notification.requestPermission();
        if (permission !== 'granted') {
          console.warn('User denied notification permission');
          return false;
        }
      } catch (error) {
        console.error('Failed to request permission:', error);
        return false;
      }
    }

    // Get VAPID public key
    let vapidKey: string;
    try {
      const response = await firstValueFrom(
        this.http.get<{ publicKey: string }>(`${this.baseUrl}/vapid-public-key`)
      );
      vapidKey = response.publicKey;
    } catch (error) {
      console.error('Failed to get VAPID key:', error);
      return false;
    }

    // Subscribe to push
    if (!this.swRegistration) {
      console.error('Service worker registration is not available');
      return false;
    }
    const pm = this.swRegistration.pushManager;
    let subscription: PushSubscription;
    try {
      subscription = await pm.subscribe({
        userVisibleOnly: true,
        applicationServerKey: this.urlBase64ToUint8Array(vapidKey),
      });
    } catch (error) {
      console.error('Failed to subscribe to push:', error);
      return false;
    }

    // Send subscription to backend
    try {
      const subJson = subscription.toJSON();
      await firstValueFrom(
        this.http.post(`${this.baseUrl}/subscribe`, {
          employeeId,
          pin,
          subscription: {
            endpoint: subscription.endpoint,
            keys: {
              p256dh: subJson.keys?.['p256dh'],
              auth: subJson.keys?.['auth'],
            },
            userAgent: navigator.userAgent,
          },
        })
      );
      console.log('Subscribed to push notifications');
      return true;
    } catch (error) {
      console.error('Failed to subscribe:', error);
      return false;
    }
  }

  async unsubscribe(endpoint: string): Promise<boolean> {
    try {
      await firstValueFrom(
        this.http.delete(`${this.baseUrl}/subscribe`, {
          body: { endpoint },
        })
      );
      return true;
    } catch (error) {
      console.error('Failed to unsubscribe:', error);
      return false;
    }
  }

  private urlBase64ToUint8Array(base64String: string): Uint8Array<ArrayBuffer> {
    const padding = '='.repeat((4 - (base64String.length % 4)) % 4);
    const base64 = (base64String + padding)
      .replace(/\-/g, '+')
      .replace(/_/g, '/');

    const rawData = window.atob(base64);
    const buffer = new ArrayBuffer(rawData.length);
    const outputArray = new Uint8Array(buffer);

    for (let i = 0; i < rawData.length; ++i) {
      outputArray[i] = rawData.charCodeAt(i);
    }
    return outputArray;
  }
}
