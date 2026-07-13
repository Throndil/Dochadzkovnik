import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';

// ─── Types (mirror API.DTOs/Dtos.cs) ───

export interface MaterialPurchaseLine {
  id: number;
  /** Null = "neidentifikovaný" — admin promotes / merges later. */
  materialId: number | null;
  /** Catalogue name when linked, otherwise null. */
  materialName: string | null;
  /** Always populated. Survives admin renames. */
  materialNameRaw: string;
  unit: string;
  quantity: number;
  /** EUR per unit, paid this trip. Independent of catalogue PricePerUnit. */
  unitPrice: number;
  /** Quantity * UnitPrice — server-computed. */
  lineTotal: number;
}

export interface MaterialPurchase {
  id: number;
  purchaseDate: string;          // ISO timestamp
  employeeId: number;
  employeeName: string;
  locationId: number | null;
  locationName: string | null;
  timeEntryId: number | null;
  /** Set when the purchase came from a scanned invoice/receipt. */
  invoiceDocumentId?: number | null;
  supplierName: string | null;
  receiptPhotoUrl: string | null;
  note: string | null;
  totalCost: number;
  lines: MaterialPurchaseLine[];
  createdAt: string;
  updatedAt: string;
}

export interface CreateMaterialPurchaseLine {
  /** Optional. Null = free-typed line, admin will promote later. */
  materialId?: number | null;
  materialNameRaw: string;
  unit: string;
  quantity: number;
  unitPrice: number;
}

/** Admin-side create payload. EmployeeId explicit. */
export interface CreateMaterialPurchase {
  employeeId: number;
  locationId?: number | null;
  timeEntryId?: number | null;
  supplierName?: string | null;
  note?: string | null;
  purchaseDate: string;          // ISO timestamp
  lines: CreateMaterialPurchaseLine[];
}

/** Kiosk-side create payload. PIN identifies the buyer. */
export interface CreateKioskMaterialPurchase {
  pin: string;
  locationId?: number | null;
  /** Set by the in-šichta combined flow, after the šichta TimeEntry is created. */
  timeEntryId?: number | null;
  supplierName?: string | null;
  note?: string | null;
  lines: CreateMaterialPurchaseLine[];
  /** Optional override; null = server uses Europe/Bratislava "now". */
  purchaseDate?: string | null;
}

export interface UpdateMaterialPurchaseLine {
  /** null = insert as new; non-null = update existing by id; any existing line whose id is missing from the request is deleted. */
  id?: number | null;
  materialId?: number | null;
  materialNameRaw: string;
  unit: string;
  quantity: number;
  unitPrice: number;
}

export interface UpdateMaterialPurchase {
  locationId?: number | null;
  supplierName?: string | null;
  note?: string | null;
  purchaseDate: string;
  lines: UpdateMaterialPurchaseLine[];
}

export interface PromoteMaterialLine {
  /** "new" creates a Material; "merge" links to an existing CatalogueMaterialId. */
  mode: 'new' | 'merge';
  newName?: string | null;
  newUnit?: string | null;
  newPricePerUnit?: number | null;
  catalogueMaterialId?: number | null;
  /** Default true server-side. Bulk-stamp sibling orphan lines with the same raw name + unit. */
  applyToAllMatchingRawName?: boolean;
}

export interface MaterialPurchasePromoteResult {
  materialId: number;
  materialName: string;
  linesLinked: number;
  createdNewCatalogueRow: boolean;
}

export interface UnknownMaterialGroup {
  materialNameRaw: string;
  unit: string;
  lineCount: number;
  totalQuantity: number;
  totalSpend: number;
  /** Volume-weighted average paid price across the group. */
  averageUnitPrice: number;
  firstSeenAt: string;
  lastSeenAt: string;
  enteredByEmployeeNames: string[];
}

export interface MaterialPurchasesKioskConfig {
  /** Null when no trigger Location is configured AND no fallback name match was found. */
  triggerLocationId: number | null;
  triggerLocationName: string | null;
}

export interface PurchaseFilters {
  from?: string;
  to?: string;
  locationId?: number;
  employeeId?: number;
  materialId?: number;
  supplier?: string;
  /** When true, returns only Nákupy with no target Location (Inventár). */
  inventoryOnly?: boolean;
}

@Injectable({ providedIn: 'root' })
export class MaterialPurchaseService {
  private adminUrl  = `${environment.apiUrl}/material-purchases`;
  private kioskUrl  = `${environment.apiUrl}/kiosk/material-purchases`;

  constructor(private http: HttpClient) {}

  // ─── Helpers ───
  private buildQuery(f: PurchaseFilters | undefined): string {
    if (!f) return '';
    const parts: string[] = [];
    if (f.from)        parts.push(`from=${f.from}`);
    if (f.to)          parts.push(`to=${f.to}`);
    if (f.locationId)  parts.push(`locationId=${f.locationId}`);
    if (f.employeeId)  parts.push(`employeeId=${f.employeeId}`);
    if (f.materialId)  parts.push(`materialId=${f.materialId}`);
    if (f.supplier)    parts.push(`supplier=${encodeURIComponent(f.supplier)}`);
    if (f.inventoryOnly) parts.push(`inventoryOnly=true`);
    return parts.length ? `?${parts.join('&')}` : '';
  }

  // ─── Admin: read ───
  list(filters?: PurchaseFilters)        { return this.http.get<MaterialPurchase[]>(`${this.adminUrl}${this.buildQuery(filters)}`); }
  get(id: number)                        { return this.http.get<MaterialPurchase>(`${this.adminUrl}/${id}`); }
  getUnknownGroups()                     { return this.http.get<UnknownMaterialGroup[]>(`${this.adminUrl}/unknown-groups`); }

  // ─── Admin: write ───
  create(dto: CreateMaterialPurchase)    { return this.http.post<MaterialPurchase>(this.adminUrl, dto); }
  update(id: number, dto: UpdateMaterialPurchase) {
    return this.http.put<MaterialPurchase>(`${this.adminUrl}/${id}`, dto);
  }
  delete(id: number)                     { return this.http.delete(`${this.adminUrl}/${id}`); }

  // ─── Admin: receipt photo ───
  uploadReceipt(id: number, file: File) {
    const form = new FormData();
    form.append('file', file);
    return this.http.post<string>(`${this.adminUrl}/${id}/photo`, form);
  }
  deleteReceipt(id: number)              { return this.http.delete(`${this.adminUrl}/${id}/photo`); }

  // ─── Admin: promote a free-typed line ───
  promoteLine(purchaseId: number, lineId: number, dto: PromoteMaterialLine) {
    return this.http.post<MaterialPurchasePromoteResult>(
      `${this.adminUrl}/${purchaseId}/lines/${lineId}/promote`, dto);
  }

  // ─── Admin: Excel export ───
  downloadExcel(filters?: PurchaseFilters): void {
    const url = `${this.adminUrl}/export${this.buildQuery(filters)}`;
    this.http.get(url, { responseType: 'blob', observe: 'response' }).subscribe({
      next: res => {
        const blob = res.body!;
        const objectUrl = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = objectUrl;
        const cd = res.headers.get('Content-Disposition') || '';
        const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/.exec(cd);
        a.download = match?.[1] ?? `Nakupy.xlsx`;
        a.click();
        URL.revokeObjectURL(objectUrl);
      },
      error: () => alert('Sťahovanie Excel súboru zlyhalo. Skúste znova.')
    });
  }

  // ─── Kiosk: config + catalogue ───
  getKioskConfig()                       { return this.http.get<MaterialPurchasesKioskConfig>(`${this.kioskUrl}/config`); }
  getKioskCatalogue()                    { return this.http.get<{ id: number; name: string; unit: string; pricePerUnit: number; isActive: boolean }[]>(`${this.kioskUrl}/catalogue`); }

  // ─── Kiosk: write ───
  kioskCreate(dto: CreateKioskMaterialPurchase) {
    return this.http.post<MaterialPurchase>(this.kioskUrl, dto);
  }
  kioskUploadReceipt(purchaseId: number, file: File, pin: string) {
    const form = new FormData();
    form.append('file', file);
    form.append('pin', pin);
    return this.http.post<string>(`${this.kioskUrl}/${purchaseId}/receipt`, form);
  }
}
