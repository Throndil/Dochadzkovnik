import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';

export interface FuelCard {
  id: number;
  label: string;
  note: string | null;
  /** Current holder. Null = unassigned (holder may not be in the system yet). */
  employeeId: number | null;
  employeeName: string | null;
  employeePosition: string | null;
  isActive: boolean;
}

export interface SaveFuelCard {
  label: string;
  note?: string | null;
  employeeId?: number | null;
  isActive: boolean;
}

/** Palivové karty (F6) — registry with current holder. */
@Injectable({ providedIn: 'root' })
export class FuelCardService {
  private readonly url = `${environment.apiUrl}/fuel-cards`;

  constructor(private http: HttpClient) {}

  list(): Promise<FuelCard[]> {
    return firstValueFrom(this.http.get<FuelCard[]>(this.url));
  }

  create(dto: SaveFuelCard): Promise<FuelCard> {
    return firstValueFrom(this.http.post<FuelCard>(this.url, dto));
  }

  update(id: number, dto: SaveFuelCard): Promise<FuelCard> {
    return firstValueFrom(this.http.put<FuelCard>(`${this.url}/${id}`, dto));
  }

  delete(id: number): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.url}/${id}`));
  }
}
