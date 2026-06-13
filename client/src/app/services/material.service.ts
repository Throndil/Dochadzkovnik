import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';

export interface Material {
  id: number;
  name: string;
  unit: string;
  /** Current catalogue price per unit, in EUR. */
  pricePerUnit: number;
  isActive: boolean;
}

export interface CreateMaterial {
  name: string;
  unit: string;
  pricePerUnit: number;
}

export interface UpdateMaterial {
  name: string;
  unit: string;
  pricePerUnit: number;
  isActive: boolean;
}

export interface MaterialUsage {
  id: number;
  locationId: number;
  materialId: number;
  materialName: string;
  unit: string;
  quantity: number;
  /** Snapshot of catalogue price at the time the usage was recorded (EUR/Unit). */
  unitPriceAtTime: number;
  /** Quantity * unitPriceAtTime — always present, server-computed. */
  lineCost: number;
  date: string;          // ISO date string
  employeeId?: number;
  employeeName?: string;
  note?: string;
  photoUrl?: string;
  /**
   * True when this row is a read-side synthesis from a MaterialPurchase line
   * (admin saved a Nákup with this Location as the target). Edit / delete are
   * disabled in the slide-over for these rows — the admin manages them via
   * the Nákupy admin tab. The id on synthetic rows is the negated purchase-line
   * id; UI should branch on `fromPurchase`, not on the sign of `id`.
   */
  fromPurchase?: boolean;
  /** Source MaterialPurchase id when fromPurchase=true. Useful for deep-links. */
  purchaseId?: number;
  /**
   * True when this usage was minted from an invoice line tagged IsService
   * (typically `Prenájom` rentals). Drives the purple "Faktúra (služba)"
   * badge in the Pracovisko Spotreba materiálu table. Always false on
   * material lines and on manual kiosk-nákup pseudo-rows.
   */
  isService?: boolean;
}

export interface CreateMaterialUsage {
  materialId: number;
  quantity: number;
  date: string;          // YYYY-MM-DD
  employeeId?: number;
  note?: string;
  /** Optional override; if omitted, the server snapshots the current catalogue price. */
  unitPriceAtTime?: number;
}

export interface UpdateMaterialUsage {
  materialId: number;
  quantity: number;
  date: string;
  employeeId?: number;
  note?: string;
  /** Optional override; if omitted, the existing snapshot is preserved (inflation protection). */
  unitPriceAtTime?: number;
}

export interface MaterialSummaryRow {
  materialId: number;
  materialName: string;
  unit: string;
  totalQuantity: number;
  /** Sum of (Quantity * UnitPriceAtTime) across included usages, in EUR. */
  totalCost: number;
  entryCount: number;
  lastEntryDate?: string;
}

@Injectable({ providedIn: 'root' })
export class MaterialService {
  private materialsUrl = `${environment.apiUrl}/materials`;
  private locUrl       = `${environment.apiUrl}/locations`;

  constructor(private http: HttpClient) {}

  // ─── Catalogue ───
  getCatalogue(activeOnly = false) {
    const q = activeOnly ? '?activeOnly=true' : '';
    return this.http.get<Material[]>(`${this.materialsUrl}${q}`);
  }
  createMaterial(dto: CreateMaterial)               { return this.http.post<Material>(this.materialsUrl, dto); }
  updateMaterial(id: number, dto: UpdateMaterial)   { return this.http.put(`${this.materialsUrl}/${id}`, dto); }
  toggleMaterialActive(id: number)                  { return this.http.patch(`${this.materialsUrl}/${id}/toggle-active`, {}); }
  deleteMaterial(id: number)                        { return this.http.delete<{ soft: boolean; message?: string }>(`${this.materialsUrl}/${id}`); }

  // ─── Per-location usage ───
  private dateRange(from?: string, to?: string) {
    const params: string[] = [];
    if (from) params.push(`from=${from}`);
    if (to)   params.push(`to=${to}`);
    return params.length ? `?${params.join('&')}` : '';
  }

  getUsages(locationId: number, from?: string, to?: string) {
    return this.http.get<MaterialUsage[]>(`${this.locUrl}/${locationId}/materials${this.dateRange(from, to)}`);
  }
  getSummary(locationId: number, from?: string, to?: string) {
    return this.http.get<MaterialSummaryRow[]>(`${this.locUrl}/${locationId}/materials/summary${this.dateRange(from, to)}`);
  }
  createUsage(locationId: number, dto: CreateMaterialUsage) {
    return this.http.post<MaterialUsage>(`${this.locUrl}/${locationId}/materials`, dto);
  }
  updateUsage(locationId: number, usageId: number, dto: UpdateMaterialUsage) {
    return this.http.put(`${this.locUrl}/${locationId}/materials/${usageId}`, dto);
  }
  deleteUsage(locationId: number, usageId: number) {
    return this.http.delete(`${this.locUrl}/${locationId}/materials/${usageId}`);
  }

  // ─── Photo on a usage entry ───
  uploadUsagePhoto(locationId: number, usageId: number, file: File) {
    const form = new FormData();
    form.append('file', file);
    return this.http.post<string>(`${this.locUrl}/${locationId}/materials/${usageId}/photo`, form);
  }
  deleteUsagePhoto(locationId: number, usageId: number) {
    return this.http.delete(`${this.locUrl}/${locationId}/materials/${usageId}/photo`);
  }

  // ─── Excel export — authenticated, triggers a browser save ───
  downloadExcel(locationId: number, locationName: string, from?: string, to?: string): void {
    const url = `${this.locUrl}/${locationId}/materials/export${this.dateRange(from, to)}`;
    this.http.get(url, { responseType: 'blob', observe: 'response' }).subscribe({
      next: res => {
        const blob = res.body!;
        const objectUrl = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = objectUrl;

        // Try to use the server-supplied filename from Content-Disposition; fall back to a sensible default.
        const cd = res.headers.get('Content-Disposition') || '';
        const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/.exec(cd);
        const safe = locationName.replace(/\s+/g, '_').normalize('NFD').replace(/[̀-ͯ]/g, '');
        a.download = match?.[1] ?? `Spotreba_${safe}.xlsx`;

        a.click();
        URL.revokeObjectURL(objectUrl);
      },
      error: () => alert('Sťahovanie Excel súboru zlyhalo. Skúste znova.')
    });
  }

  /**
   * Cross-Pracoviská Excel — every location, every entry, costs at the price
   * the material was used at (UnitPriceAtTime / line.UnitPrice snapshot).
   * Server endpoint: GET /api/locations/materials/export-all.
   */
  downloadAllLocationsExcel(from?: string, to?: string): void {
    const url = `${this.locUrl}/materials/export-all${this.dateRange(from, to)}`;
    this.http.get(url, { responseType: 'blob', observe: 'response' }).subscribe({
      next: res => {
        const blob = res.body!;
        const objectUrl = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = objectUrl;
        const cd = res.headers.get('Content-Disposition') || '';
        const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/.exec(cd);
        a.download = match?.[1] ?? 'Spotreba_vsetky_pracoviska.xlsx';
        a.click();
        URL.revokeObjectURL(objectUrl);
      },
      error: () => alert('Sťahovanie Excel súboru zlyhalo. Skúste znova.')
    });
  }
}
