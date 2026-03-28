import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';

export interface Car {
  id: number;
  name: string;
  licensePlate?: string;
  photoUrl?: string;
  isActive: boolean;
}

export interface CreateCar {
  name: string;
  licensePlate?: string;
}

export interface UpdateCar {
  name: string;
  licensePlate?: string;
  isActive: boolean;
}

@Injectable({ providedIn: 'root' })
export class CarService {
  private url = `${environment.apiUrl}/cars`;

  constructor(private http: HttpClient) {}

  getAll() { return this.http.get<Car[]>(this.url); }
  get(id: number) { return this.http.get<Car>(`${this.url}/${id}`); }
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
