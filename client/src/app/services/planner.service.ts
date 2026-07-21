import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';

/** One planner bar: pracovisko assignment or absence, inclusive date range. */
export interface PlanEntry {
  id: number;
  employeeId: number;
  /** 'praca' | 'dovolenka' | 'pn' | 'volno' */
  type: string;
  locationId: number | null;
  locationName: string | null;
  startDate: string;
  endDate: string;
  note: string | null;
}

export interface SavePlanEntry {
  employeeId: number;
  type: string;
  locationId?: number | null;
  startDate: string;
  endDate: string;
  note?: string | null;
}

/** Plánovač (Planner flag) — CRUD for the week-grid bars. */
@Injectable({ providedIn: 'root' })
export class PlannerService {
  private readonly url = `${environment.apiUrl}/planner`;

  constructor(private http: HttpClient) {}

  list(from: string, to: string): Promise<PlanEntry[]> {
    return firstValueFrom(this.http.get<PlanEntry[]>(`${this.url}?from=${from}&to=${to}`));
  }

  create(dto: SavePlanEntry): Promise<PlanEntry> {
    return firstValueFrom(this.http.post<PlanEntry>(this.url, dto));
  }

  update(id: number, dto: SavePlanEntry): Promise<PlanEntry> {
    return firstValueFrom(this.http.put<PlanEntry>(`${this.url}/${id}`, dto));
  }

  delete(id: number): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.url}/${id}`));
  }
}
