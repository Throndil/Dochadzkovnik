import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';

export interface CompanyRate {
  id: number;
  /** Stable key on app-used rows ("odvody", "ubytovanie", "vyjazd_auta"); null on custom rows. */
  key: string | null;
  label: string;
  amount: number;
  unit: string | null;
  updatedAt: string;
}

export interface SaveCompanyRate {
  label: string;
  amount: number;
  unit?: string | null;
}

/** "Odvody" — configurable company amounts (odvody, ubytovanie, výjazd…). */
@Injectable({ providedIn: 'root' })
export class CompanyRateService {
  private readonly url = `${environment.apiUrl}/company-rates`;

  constructor(private http: HttpClient) {}

  list(): Promise<CompanyRate[]> {
    return firstValueFrom(this.http.get<CompanyRate[]>(this.url));
  }

  create(dto: SaveCompanyRate): Promise<CompanyRate> {
    return firstValueFrom(this.http.post<CompanyRate>(this.url, dto));
  }

  update(id: number, dto: SaveCompanyRate): Promise<CompanyRate> {
    return firstValueFrom(this.http.put<CompanyRate>(`${this.url}/${id}`, dto));
  }

  delete(id: number): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.url}/${id}`));
  }
}
