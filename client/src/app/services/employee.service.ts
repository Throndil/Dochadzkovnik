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
}
