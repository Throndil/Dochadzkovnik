import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';

// ─── Types (mirror API.DTOs/Dtos.cs) ───

export interface WorkDiary {
  id: number;
  employeeId: number | null;
  employeeName: string | null;
  locationId: number;
  locationName: string | null;
  /** Populated when submitted in the same kiosk session as hours; null otherwise. */
  timeEntryId: number | null;
  /** ISO date — day the work happened. */
  date: string;
  bodyText: string;
  attachmentUrl: string | null;
  createdAt: string;
  updatedAt: string;
}

/** Kiosk-side create payload — PIN-authed. */
export interface CreateKioskWorkDiary {
  pin: string;
  locationId: number;
  date: string;          // ISO date
  bodyText: string;
  timeEntryId?: number | null;
}

export interface UpdateWorkDiary {
  date?: string;
  bodyText?: string;
}

/**
 * Front-end client for the stavebný denník endpoints — see
 * PROOF_OF_WORK_UX_PLAN.md. Two surfaces:
 *  - kiosk: PIN-authed create + attachment upload.
 *  - admin: JWT-authed list/get/put/delete + attachment management.
 * Both behind the ProofOfWorkChoices feature flag at the backend.
 */
@Injectable({ providedIn: 'root' })
export class WorkDiaryService {
  private readonly apiUrl = environment.apiUrl;

  constructor(private http: HttpClient) {}

  // ─── Kiosk ───

  createFromKiosk(payload: CreateKioskWorkDiary): Promise<WorkDiary> {
    return firstValueFrom(
      this.http.post<WorkDiary>(`${this.apiUrl}/kiosk/work-diaries`, payload)
    );
  }

  uploadKioskAttachment(id: number, pin: string, file: File): Promise<string> {
    const form = new FormData();
    form.append('file', file);
    form.append('pin', pin);
    return firstValueFrom(
      this.http.post(`${this.apiUrl}/kiosk/work-diaries/${id}/attachment`, form, { responseType: 'text' })
    );
  }

  // ─── Admin ───

  list(filters: { from?: string; to?: string; locationId?: number; employeeId?: number } = {}): Promise<WorkDiary[]> {
    let params = new HttpParams();
    if (filters.from)       params = params.set('from', filters.from);
    if (filters.to)         params = params.set('to', filters.to);
    if (filters.locationId) params = params.set('locationId', String(filters.locationId));
    if (filters.employeeId) params = params.set('employeeId', String(filters.employeeId));
    return firstValueFrom(
      this.http.get<WorkDiary[]>(`${this.apiUrl}/work-diaries`, { params })
    );
  }

  get(id: number): Promise<WorkDiary> {
    return firstValueFrom(this.http.get<WorkDiary>(`${this.apiUrl}/work-diaries/${id}`));
  }

  update(id: number, payload: UpdateWorkDiary): Promise<WorkDiary> {
    return firstValueFrom(
      this.http.put<WorkDiary>(`${this.apiUrl}/work-diaries/${id}`, payload)
    );
  }

  delete(id: number): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.apiUrl}/work-diaries/${id}`));
  }

  uploadAttachment(id: number, file: File): Promise<string> {
    const form = new FormData();
    form.append('file', file);
    return firstValueFrom(
      this.http.post(`${this.apiUrl}/work-diaries/${id}/attachment`, form, { responseType: 'text' })
    );
  }

  deleteAttachment(id: number): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`${this.apiUrl}/work-diaries/${id}/attachment`)
    );
  }
}
