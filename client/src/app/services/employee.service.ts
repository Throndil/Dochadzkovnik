import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';

export interface Employee {
  id: number;
  firstName: string;
  lastName: string;
  phoneNumber?: string;
  address?: string;
  city?: string;
  photoUrl?: string;
  isActive: boolean;
  createdAt: string;
  pinPlain?: string;  // visible to manager on edit page
  /** Reason given when the worker declined push notifications from the kiosk. Null if never declined. */
  notificationsDeclineReason?: string;
  /** EUR/h. Null when no rate has been set yet. Admin-only field; never on kiosk DTOs. */
  hourlyWage?: number | null;
  /** 'profistav' | 'stroje' — company division (Fáza D8). */
  division?: string;
  /** Free-text pozícia (F6), e.g. 'šofér'. */
  position?: string | null;
}

export interface CreateEmployee {
  firstName: string;
  lastName: string;
  pin: string;
  phoneNumber?: string;
  address?: string;
  city?: string;
}

export interface UpdateEmployee {
  firstName: string;
  lastName: string;
  phoneNumber?: string;
  address?: string;
  city?: string;
  isActive: boolean;
  /** EUR/h. Omit to leave the existing value unchanged. */
  hourlyWage?: number | null;
  /** 'profistav' | 'stroje'. Omit to leave unchanged. */
  division?: string;
  /** Pozícia. Omit to leave unchanged; empty string clears. */
  position?: string;
}

/**
 * Admin-only "Treba pripomenúť" row. Same shape as the kiosk anonymous version
 * but additionally carries the worker's phone number so the manager can call /
 * SMS them from the Notifikácie page. Only ever returned from the JWT-protected
 * GET /api/employees/missing-hours-overview endpoint.
 */
export interface EmployeeMissingDaysAdmin {
  id: number;
  firstName: string;
  lastName: string;
  fullName: string;
  photoUrl?: string;
  phoneNumber?: string;
  missingDates: string[];
}

export interface MissingHoursOverviewAdmin {
  checkedDates: string[];
  employees: EmployeeMissingDaysAdmin[];
}

@Injectable({ providedIn: 'root' })
export class EmployeeService {
  private url = `${environment.apiUrl}/employees`;

  constructor(private http: HttpClient) {}

  getAll() {
    return this.http.get<Employee[]>(this.url);
  }

  get(id: number) {
    return this.http.get<Employee>(`${this.url}/${id}`);
  }

  create(dto: CreateEmployee) {
    return this.http.post<Employee>(this.url, dto);
  }

  update(id: number, dto: UpdateEmployee) {
    return this.http.put(`${this.url}/${id}`, dto);
  }

  delete(id: number) {
    return this.http.delete(`${this.url}/${id}`);
  }

  toggleActive(id: number) {
    return this.http.patch(`${this.url}/${id}/toggle-active`, {});
  }

  setPin(id: number, pin: string) {
    return this.http.patch(`${this.url}/${id}/pin`, { pin });
  }

  generateUniquePin() {
    return this.http.get<{ pin: string }>(`${this.url}/generate-pin`);
  }

  hardDelete(id: number) {
    return this.http.delete(`${this.url}/${id}/permanent`);
  }

  uploadPhoto(id: number, file: File) {
    const formData = new FormData();
    formData.append('file', file);
    return this.http.post<string>(`${this.url}/${id}/photo`, formData);
  }

  /** Admin "Treba pripomenúť" — JWT-protected, includes phone numbers. */
  getMissingHoursOverview() {
    return this.http.get<MissingHoursOverviewAdmin>(`${this.url}/missing-hours-overview`);
  }
}
