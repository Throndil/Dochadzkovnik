import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';

export interface Location {
  id: number;
  name: string;
  address?: string;
  photoUrl?: string;
  isActive: boolean;
}

export interface CreateLocation {
  name: string;
  address?: string;
}

export interface UpdateLocation {
  name: string;
  address?: string;
  isActive: boolean;
}

@Injectable({ providedIn: 'root' })
export class LocationService {
  private url = `${environment.apiUrl}/locations`;

  constructor(private http: HttpClient) {}

  getAll() {
    return this.http.get<Location[]>(this.url);
  }

  get(id: number) {
    return this.http.get<Location>(`${this.url}/${id}`);
  }

  create(dto: CreateLocation) {
    return this.http.post<Location>(this.url, dto);
  }

  update(id: number, dto: UpdateLocation) {
    return this.http.put(`${this.url}/${id}`, dto);
  }

  delete(id: number) {
    return this.http.delete(`${this.url}/${id}`);
  }

  toggleActive(id: number) {
    return this.http.patch(`${this.url}/${id}/toggle-active`, {});
  }

  hardDelete(id: number) {
    return this.http.delete(`${this.url}/${id}/permanent`);
  }

  uploadPhoto(id: number, file: File) {
    const formData = new FormData();
    formData.append('file', file);
    return this.http.post<string>(`${this.url}/${id}/photo`, formData);
  }
}
