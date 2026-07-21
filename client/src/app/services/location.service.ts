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

export interface LocationPhoto {
  timeEntryId?: number;   // null for standalone WorkPhotos
  workPhotoId?: number;   // null for TimeEntry photos
  employeeName: string;
  date: string;
  photoUrl: string;
}

// ─── Náklady a zisk (P&L) — mirrors API/DTOs/Dtos.cs ───

export interface PnlLabourRow {
  employeeId: number;
  employeeName: string;
  hours: number;
  avgWage: number | null;
  cost: number;
}

export interface PnlMaterialRow {
  materialId: number;
  materialName: string;
  unit: string;
  quantity: number;
  avgUnitPrice: number | null;
  cost: number;
}

// ─── Denník podľa dátumu (P1) — the per-day "zložka" of a workplace ───

export interface DailyLogShift {
  employeeName: string;
  /** Null while the shift is still open. */
  hours: number | null;
  note: string | null;
  carName: string | null;
  machineName: string | null;
}

export interface DailyLogDiary {
  employeeName: string;
  bodyText: string;
  attachmentUrl: string | null;
}

export interface DailyLogDoc {
  purchaseId: number;
  invoiceDocumentId: number | null;
  supplierName: string | null;
  deliveryNoteRef: string | null;
  totalCost: number;
}

export interface DailyLogPhoto {
  photoUrl: string;
  employeeName: string;
}

export interface DailyLogDay {
  date: string;
  shifts: DailyLogShift[];
  diaries: DailyLogDiary[];
  materials: { materialName: string; quantity: number; unit: string; lineCost: number }[];
  documents: DailyLogDoc[];
  photos: DailyLogPhoto[];
}

export interface LocationPnl {
  location: { id: number; name: string; contractValue: number | null; isActive: boolean };
  labour: { hoursWorked: number; cost: number; breakdownByEmployee: PnlLabourRow[] };
  /** Null when the MaterialPurchases flag is off for the caller. */
  material: { cost: number; breakdownByMaterial: PnlMaterialRow[] } | null;
  /** Invoice money assigned to the location (s DPH, any status except
   *  discarded). Only filled by the pnl-summary endpoint. */
  invoicedInclVat?: number | null;
  /** Výjazdy áut (F5): one ride per car per day. Null when no car was used. */
  trips: { count: number; rate: number; cost: number } | null;
  revenue: number | null;
  profit: number | null;
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

  uploadGalleryPhoto(id: number, file: File, takenAt?: string) {
    const formData = new FormData();
    formData.append('file', file);
    if (takenAt) formData.append('takenAt', takenAt);
    return this.http.post<string>(`${this.url}/${id}/gallery-photo`, formData);
  }

  getPhotos(id: number, from: string, to: string) {
    return this.http.get<LocationPhoto[]>(`${this.url}/${id}/photos?from=${from}&to=${to}`);
  }

  /** Downloads the photo ZIP via an authenticated request and triggers a browser save-as. */
  downloadPhotosZip(id: number, ym: string, locationName: string): void {
    this.http.get(`${this.url}/${id}/photos/download?from=${ym}&to=${ym}`, {
      responseType: 'blob',
      observe: 'response'
    }).subscribe({
      next: res => {
        const blob = res.body!;
        const url  = URL.createObjectURL(blob);
        const a    = document.createElement('a');
        a.href     = url;
        a.download = `fotky-${locationName.replace(/\s+/g, '_')}-${ym}.zip`;
        a.click();
        URL.revokeObjectURL(url);
      },
      error: () => alert('Sťahovanie ZIP zlyhalo. Skúste znova.')
    });
  }

  bulkDeletePhotos(id: number, before: string) {
    return this.http.delete<number>(`${this.url}/${id}/photos?before=${before}`);
  }

  deleteWorkPhoto(workPhotoId: number) {
    return this.http.delete(`${environment.apiUrl}/work-photos/${workPhotoId}`);
  }

  // ─── Náklady a zisk (P&L) — PayrollAndPnL flag ───

  /** P1 — per-day zložka of the workplace (shifts, denník, materiál, doklady, fotky). */
  getDailyLog(id: number, from: string, to: string) {
    return this.http.get<DailyLogDay[]>(`${this.url}/${id}/daily-log?from=${from}&to=${to}`);
  }

  getPnl(id: number, from: string, to: string) {
    return this.http.get<LocationPnl>(`${this.url}/${id}/pnl?from=${from}&to=${to}`);
  }

  /** Cross-location spending report (wages + material per active Pracovisko)
   *  for a date range — powers the Financie overview table. */
  getPnlSummary(from: string, to: string) {
    return this.http.get<LocationPnl[]>(`${this.url}/pnl-summary?from=${from}&to=${to}`);
  }

  updateContractValue(id: number, contractValue: number | null) {
    return this.http.put(`${this.url}/${id}/contract-value`, { contractValue });
  }

  /** Downloads the Náklady a zisk XLSX via an authenticated request and triggers a browser save-as. */
  downloadPnlExcel(id: number, from: string, to: string, locationName: string): void {
    this.http.get(`${this.url}/${id}/pnl/export?from=${from}&to=${to}`, {
      responseType: 'blob',
      observe: 'response'
    }).subscribe({
      next: res => {
        const blob = res.body!;
        const url  = URL.createObjectURL(blob);
        const a    = document.createElement('a');
        a.href     = url;
        a.download = `Naklady_a_zisk_${locationName.replace(/\s+/g, '_')}_${from}_${to}.xlsx`;
        a.click();
        URL.revokeObjectURL(url);
      },
      error: () => alert('Sťahovanie Excel súboru zlyhalo. Skúste znova.')
    });
  }
}
