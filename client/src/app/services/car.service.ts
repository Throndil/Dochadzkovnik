import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';

export interface Car {
  id: number;
  name: string;
  licensePlate?: string;
  photoUrl?: string;
  /** 'profistav' | 'stroje' — vehicle's division (Fáza F). */
  division?: string;
  isActive: boolean;
  /** Informational F1 rollup: s-DPH sum of tagged cost documents. */
  costTotal?: number;
}

/** One cost document tagged to a car/mašina (F4 service ledger). */
export interface AssetCostDoc {
  invoiceDocumentId: number;
  invoiceNumber: string;
  supplierName: string;
  issueDate: string;
  documentKind: string;
  status: string;
  grossTotal: number;
}

export interface CreateCar {
  name: string;
  licensePlate?: string;
  division?: string;
}

export interface UpdateCar {
  name: string;
  licensePlate?: string;
  division?: string;
  isActive: boolean;
}

@Injectable({ providedIn: 'root' })
export class CarService {
  private url = `${environment.apiUrl}/cars`;

  constructor(private http: HttpClient) {}

  getAll() { return this.http.get<Car[]>(this.url); }
  get(id: number) { return this.http.get<Car>(`${this.url}/${id}`); }
  getCosts(id: number) { return this.http.get<AssetCostDoc[]>(`${this.url}/${id}/costs`); }
  create(dto: CreateCar) { return this.http.post<Car>(this.url, dto); }
  update(id: number, dto: UpdateCar) { return this.http.put(`${this.url}/${id}`, dto); }
  toggleActive(id: number) { return this.http.patch(`${this.url}/${id}/toggle-active`, {}); }
  delete(id: number) { return this.http.delete(`${this.url}/${id}`); }
  uploadPhoto(id: number, file: File) {
    const form = new FormData();
    form.append('file', file);
    return this.http.post<string>(`${this.url}/${id}/photo`, form);
  }
}
