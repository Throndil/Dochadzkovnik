import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { environment } from '../../environments/environment';
import { TimeEntry } from './time-entry.service';

export interface DailyReport {
  date: string;
  entries: DailyReportEntry[];
  totalHours: number;
}

export interface DailyReportEntry {
  employeeName: string;
  locationName: string;
  carName?: string;
  clockIn: string;
  clockOut?: string;
  hoursWorked?: number;
}

@Injectable({ providedIn: 'root' })
export class ReportService {
  private url = `${environment.apiUrl}/reports`;

  constructor(private http: HttpClient) {}

  getDaily(date?: string) {
    let params = new HttpParams();
    if (date) params = params.set('date', date);
    return this.http.get<DailyReport>(`${this.url}/daily`, { params });
  }

  getSummary(filters?: { from?: string; to?: string; employeeId?: number; locationId?: number }) {
    let params = new HttpParams();
    if (filters?.from) params = params.set('from', filters.from);
    if (filters?.to) params = params.set('to', filters.to);
    if (filters?.employeeId) params = params.set('employeeId', filters.employeeId.toString());
    if (filters?.locationId) params = params.set('locationId', filters.locationId.toString());
    return this.http.get<TimeEntry[]>(`${this.url}/summary`, { params });
  }

  exportCsv(filters?: { from?: string; to?: string; employeeId?: number; locationId?: number }) {
    let params = new HttpParams();
    if (filters?.from) params = params.set('from', filters.from);
    if (filters?.to) params = params.set('to', filters.to);
    if (filters?.employeeId) params = params.set('employeeId', filters.employeeId.toString());
    if (filters?.locationId) params = params.set('locationId', filters.locationId.toString());
    return this.http.get(`${this.url}/export/csv`, { params, responseType: 'blob' });
  }
}
