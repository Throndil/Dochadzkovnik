import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';
import { Location } from './location.service';
import { Car } from './car.service';
import { TimeEntry } from './time-entry.service';

export interface KioskResponse {
  message: string;
  employeeName: string;
  timestamp: string;
  timeEntryId?: number;
}

export interface KioskStatus {
  employeeId: number;
  employeeName: string;
  isClockedIn: boolean;
  clockInTime?: string;
  locationName?: string;
}

export interface WeeklyEntry {
  locationName: string;
  hours: number;
  note?: string;
}

export interface WeeklyDayData {
  date: string;
  entries: WeeklyEntry[];
}

export interface WeeklyRow {
  employeeId: number;
  employeeName: string;
  photoUrl?: string;
  days: WeeklyDayData[];
  totalHours: number;
}

export interface WeeklyOverview {
  weekStart: string;
  days: string[];
  rows: WeeklyRow[];
}

@Injectable({ providedIn: 'root' })
export class KioskService {
  private url = `${environment.apiUrl}/kiosk`;

  constructor(private http: HttpClient) {}

  getLocations() {
    return this.http.get<Location[]>(`${this.url}/locations`);
  }

  getCars() {
    return this.http.get<Car[]>(`${this.url}/cars`);
  }

  getOverview(weekStart?: string) {
    const params = weekStart ? `?weekStart=${weekStart}` : '';
    return this.http.get<WeeklyOverview>(`${this.url}/overview${params}`);
  }

  clockIn(pin: string, locationId: number) {
    return this.http.post<KioskResponse>(`${this.url}/clock-in`, { pin, locationId });
  }

  clockOut(pin: string, note?: string) {
    return this.http.post<KioskResponse>(`${this.url}/clock-out`, { pin, note });
  }

  logHours(pin: string, locationId: number, hoursWorked: number, note?: string, date?: string, carId?: number) {
    return this.http.post<KioskResponse>(`${this.url}/log-hours`, { pin, locationId, hoursWorked, note, date, carId });
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

  /** Upload 1–5 photos for a time entry in a single request.
   *  The backend stores comma-separated URLs; returns the combined photoUrl string. */
  uploadEntryPhotos(timeEntryId: number, pin: string, files: File[]) {
    const form = new FormData();
    form.append('pin', pin);
    for (const file of files) {
      form.append('photos', file, file.name);
    }
    return this.http.post<{ photoUrl: string }>(`${this.url}/photo/${timeEntryId}`, form);
  }

  uploadWorkPhoto(pin: string, locationId: number, file: File) {
    const form = new FormData();
    form.append('pin', pin);
    form.append('locationId', locationId.toString());
    form.append('photo', file, file.name);
    return this.http.post<WorkPhotoResult>(`${this.url}/work-photo`, form);
  }
}

export interface WorkPhotoResult {
  photoUrl: string;
  employeeName: string;
  locationName: string;
  createdAt: string;
  remainingToday: number;  // how many more uploads are allowed today (out of 5)
}
