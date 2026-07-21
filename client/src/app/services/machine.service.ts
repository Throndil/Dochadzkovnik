import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';

export interface Machine {
  id: number;
  name: string;
  note?: string | null;
  photoUrl?: string | null;
  isActive: boolean;
  /** Informational F1 rollup: s-DPH sum of tagged cost documents. */
  costTotal?: number;
}

export interface CreateMachine {
  name: string;
  note?: string;
}

export interface UpdateMachine {
  name: string;
  note?: string | null;
  isActive: boolean;
}

/** AZ Stroje registry (Fáza F0) — mirrors CarService. */
@Injectable({ providedIn: 'root' })
export class MachineService {
  private url = `${environment.apiUrl}/machines`;

  constructor(private http: HttpClient) {}

  getAll() { return this.http.get<Machine[]>(this.url); }
  get(id: number) { return this.http.get<Machine>(`${this.url}/${id}`); }
  create(dto: CreateMachine) { return this.http.post<Machine>(this.url, dto); }
  update(id: number, dto: UpdateMachine) { return this.http.put(`${this.url}/${id}`, dto); }
  toggleActive(id: number) { return this.http.patch(`${this.url}/${id}/toggle-active`, {}); }
  delete(id: number) { return this.http.delete(`${this.url}/${id}`); }
  uploadPhoto(id: number, file: File) {
    const form = new FormData();
    form.append('file', file);
    return this.http.post<string>(`${this.url}/${id}/photo`, form);
  }
}
