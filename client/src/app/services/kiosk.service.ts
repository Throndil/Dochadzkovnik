import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';
import { Location } from './location.service';
import { Car } from './car.service';
import { Machine } from './machine.service';
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
  /** Full-month total for the first (or only) calendar month of the viewed week. */
  totalHours: number;
  /** Full-month total for the second calendar month when the week spans a boundary; 0 otherwise. */
  totalHoursMonth2: number;
}

export interface WeeklyOverview {
  weekStart: string;
  days: string[];
  rows: WeeklyRow[];
  /** True when the 7-day window crosses a month boundary (e.g. week of 27 Apr – 3 May). */
  spansTwoMonths: boolean;
  /** Slovak abbreviated name of the first month (e.g. "apr"). Always present. */
  month1Label?: string;
  /** Slovak abbreviated name of the second month (e.g. "máj"). Present only when spansTwoMonths is true. */
  month2Label?: string;
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

  /** Active machines for the transport step (Auto / Stroj / Pešo, Fáza F3). */
  getMachines() {
    return this.http.get<Machine[]>(`${this.url}/machines`);
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

  logHours(pin: string, locationId: number, hoursWorked: number, note?: string, date?: string, carId?: number, machineId?: number, proofOfWorkSkipped?: boolean) {
    return this.http.post<KioskResponse>(`${this.url}/log-hours`, { pin, locationId, hoursWorked, note, date, carId, machineId, proofOfWorkSkipped });
  }

  /**
   * Auto-skip check for the kiosk proof-of-work step. Asks the backend whether
   * the worker already has a recent proof (photo or diary) for this Location
   * and Date in the past hour. Used to skip the proof-pick step entirely.
   * Returns { exists, source: 'photo' | 'diary' | null, at: ISO local | null }.
   * Behind the ProofOfWorkChoices feature flag.
   */
  checkProofExists(pin: string, locationId: number, date?: string) {
    return this.http.post<{ exists: boolean; source: string | null; at: string | null }>(
      `${this.url}/proof-exists`,
      { pin, locationId, date }
    );
  }

  /**
   * Today's TimeEntry roll-up at a Location — who already clocked here today,
   * how many hours, and what they wrote in the note. PIN-gated. Read-only.
   * Shown on the kiosk hours step so the next worker has site context.
   */
  getTodayAtLocation(pin: string, locationId: number) {
    return this.http.post<Array<{
      employeeId: number;
      employeeName: string;
      clockIn: string;
      hoursWorked: number | null;
      note: string | null;
      diaryBody: string | null;
      isMine: boolean;
    }>>(`${this.url}/today-at-location`, { pin, locationId });
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

  /** PIN-authenticated — worker declines push notifications and gives a reason. */
  declineNotifications(pin: string, reason: string) {
    return this.http.post(`${this.url}/decline-notifications`, { pin, reason });
  }

  /** Public endpoint — list of workers with no time entry for last 2 days. */
  getMissingHoursOverview() {
    return this.http.get<MissingHoursOverview>(`${this.url}/missing-hours-overview`);
  }

  /** PIN-authenticated — the missing days for the worker that owns the PIN. */
  getMyMissingDays(pin: string) {
    return this.http.post<MyMissingDays>(`${this.url}/my-missing-days`, { pin });
  }
}

export interface EmployeeMissingDays {
  id: number;
  firstName: string;
  lastName: string;
  fullName: string;
  photoUrl?: string;
  // phoneNumber intentionally NOT exposed — the kiosk endpoint is anonymous and
  // anything on this interface ends up on a publicly-visible tablet. Managers
  // look phone numbers up via the JWT-protected admin Employees page instead.
  missingDates: string[]; // yyyy-MM-dd
}

export interface MissingHoursOverview {
  checkedDates: string[];
  employees: EmployeeMissingDays[];
}

export interface MyMissingDays {
  employeeName: string;
  missingDates: string[];
}

export interface WorkPhotoResult {
  photoUrl: string;
  employeeName: string;
  locationName: string;
  createdAt: string;
  remainingToday: number;  // how many more uploads are allowed today (out of 5)
}
