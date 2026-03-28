import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { environment } from '../../environments/environment';

export interface TimeEntry {
  id: number;
  employeeId: number;
  employeeName: string;
  employeePhotoUrl?: string;
  locationId: number;
  locationName: string;
  carId?: number;
  carName?: string;
  clockIn: string;
  clockOut?: string;
  hoursWorked?: number;
  note?: string;
}

export interface CreateTimeEntry {
  employeeId: number;
  locationId: number;
  clockIn: string;
  clockOut?: string;
  note?: string;
}

export interface UpdateTimeEntry {
  clockIn: string;
  clockOut?: string;
  note?: string;
}

@Injectable({ providedIn: 'root' })
export class TimeEntryService {
  private url = `${environment.apiUrl}/time-entries`;

  constructor(private http: HttpClient) {}

  getAll(filters?: { from?: string; to?: string; employeeId?: number; locationId?: number }) {
    let params = new HttpParams();
    if (filters?.from) params = params.set('from', filters.from);
    if (filters?.to) params = params.set('to', filters.to);
    if (filters?.employeeId) params = params.set('employeeId', filters.employeeId.toString());
    if (filters?.locationId) params = params.set('locationId', filters.locationId.toString());
    return this.http.get<TimeEntry[]>(this.url, { params });
  }

  create(dto: CreateTimeEntry) {
    return this.http.post<TimeEntry>(this.url, dto);
  }

  update(id: number, dto: UpdateTimeEntry) {
    return this.http.put(`${this.url}/${id}`, dto);
  }

  delete(id: number) {
    return this.http.delete(`${this.url}/${id}`);
  }
}
