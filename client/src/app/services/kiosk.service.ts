import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';
import { Location } from './location.service';
import { TimeEntry } from './time-entry.service';

export interface KioskResponse {
  message: string;
  employeeName: string;
  timestamp: string;
}

export interface KioskStatus {
  employeeName: string;
  isClockedIn: boolean;
  clockInTime?: string;
  locationName?: string;
}

@Injectable({ providedIn: 'root' })
export class KioskService {
  private url = `${environment.apiUrl}/kiosk`;

  constructor(private http: HttpClient) {}

  getLocations() {
    return this.http.get<Location[]>(`${this.url}/locations`);
  }

  clockIn(pin: string, locationId: number) {
    return this.http.post<KioskResponse>(`${this.url}/clock-in`, { pin, locationId });
  }

  clockOut(pin: string, note?: string) {
    return this.http.post<KioskResponse>(`${this.url}/clock-out`, { pin, note });
  }

  manualEntry(pin: string, locationId: number, clockIn: string, clockOut: string, note?: string) {
    return this.http.post<KioskResponse>(`${this.url}/manual-entry`, { pin, locationId, clockIn, clockOut, note });
  }

  getStatus(pin: string) {
    return this.http.post<KioskStatus>(`${this.url}/status`, { pin });
  }

  getMyHours(pin: string, from: string, to: string) {
    return this.http.post<TimeEntry[]>(`${this.url}/my-hours`, { pin, from, to });
  }
}
