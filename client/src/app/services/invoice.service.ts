import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';

// ─── Types — mirror API/DTOs/Dtos.cs ───

export interface InvoiceLine {
  id: number;
  purchaseId: number;
  supplierItemCode: string | null;
  materialNameRaw: string;
  unit: string;
  quantity: number;
  unitPrice: number;
  lineTotal: number;
  listPriceExclVat: number | null;
  discountPercent: number | null;
  unitPriceInclVat: number | null;
  vatRate: number;
  isReverseCharge: boolean;
  isService: boolean;
}

export interface InvoiceDeliveryList {
  id: number;
  deliveryNoteRef: string | null;
  purchaseDate: string;
  pickedUpBy: string | null;
  deliveryNote: string | null;
  locationId: number | null;
  locationName: string | null;
  akciaSuggestion: string | null;
  subtotalExclVat: number | null;
  subtotalVat: number | null;
  lines: InvoiceLine[];
}

export interface InvoiceDocument {
  id: number;
  invoiceNumber: string;
  supplierName: string;
  supplierIco: string | null;
  supplierIcDph: string | null;
  supplierIban: string | null;
  issueDate: string;
  deliveryDate: string | null;
  dueDate: string | null;
  periodFrom: string | null;
  periodTo: string | null;
  currency: string;
  totalExclVat: number;
  totalVat: number;
  totalInclVat: number;
  pdfUrl: string;
  /** 'parsing' | 'review' | 'committed' | 'discarded' */
  status: string;
  reconciliationOk: boolean;
  reconciliationNote: string | null;
  uploadedBy: string;
  uploadedAt: string;
  committedBy: string | null;
  committedAt: string | null;
  note: string | null;
  /** "file" (PDF/image picker) or "camera" (in-app scanner). */
  scanSource?: string;
  /** Number of photos in the camera scan; null on file uploads. */
  scanPageCount?: number | null;
  deliveryLists: InvoiceDeliveryList[];
}

export interface UpdateInvoiceLinePayload {
  supplierItemCode?: string | null;
  materialNameRaw?: string;
  unit?: string;
  quantity?: number;
  unitPrice?: number;
  lineTotal?: number;
  vatRate?: number;
  isReverseCharge?: boolean;
  isService?: boolean;
}

export interface UpdateInvoiceDeliveryListPayload {
  /** Pass a positive Location.Id, or -1 to clear (= Sklad / Inventár). */
  locationId?: number;
  pickedUpBy?: string;
  deliveryNote?: string;
}

/**
 * Frontend client for the invoice scanning API. Manager-only (JWT). All
 * endpoints behind the InvoiceScanning feature flag. See
 * INVOICE_SCANNING_PLAN.md for the contract.
 */
@Injectable({ providedIn: 'root' })
export class InvoiceService {
  private readonly url = `${environment.apiUrl}/invoices`;

  constructor(private http: HttpClient) {}

  /**
   * Upload a PDF (or image) of a supplier invoice. The backend runs Document
   * AI synchronously and returns the parsed result as drafts. The manager
   * then reviews and edits on /admin/invoices/{id}/review before commit.
   */
  upload(file: File): Promise<InvoiceDocument> {
    const form = new FormData();
    form.append('file', file, file.name);
    return firstValueFrom(
      this.http.post<InvoiceDocument>(`${this.url}/upload`, form)
    );
  }

  /**
   * Camera-scan upload: N image files (one per page of a paper invoice).
   * Server assembles them into a PDF, then runs the same Document AI parse
   * + persistence path as `upload`. Behind the InvoiceCameraScan flag.
   * See INVOICE_SCANNING_CAMERA_STAGES.md stage 1.
   */
  uploadPhotos(files: File[]): Promise<InvoiceDocument> {
    const form = new FormData();
    for (const f of files) form.append('files', f, f.name);
    return firstValueFrom(
      this.http.post<InvoiceDocument>(`${this.url}/upload-photos`, form)
    );
  }

  list(filters: { status?: string; from?: string; to?: string; supplier?: string } = {}): Promise<InvoiceDocument[]> {
    let params = new HttpParams();
    if (filters.status)   params = params.set('status', filters.status);
    if (filters.from)     params = params.set('from', filters.from);
    if (filters.to)       params = params.set('to', filters.to);
    if (filters.supplier) params = params.set('supplier', filters.supplier);
    return firstValueFrom(
      this.http.get<InvoiceDocument[]>(this.url, { params })
    );
  }

  get(id: number): Promise<InvoiceDocument> {
    return firstValueFrom(this.http.get<InvoiceDocument>(`${this.url}/${id}`));
  }

  updateLine(invoiceId: number, lineId: number, payload: UpdateInvoiceLinePayload): Promise<InvoiceDocument> {
    return firstValueFrom(
      this.http.put<InvoiceDocument>(`${this.url}/${invoiceId}/lines/${lineId}`, payload)
    );
  }

  updateDeliveryList(invoiceId: number, purchaseId: number, payload: UpdateInvoiceDeliveryListPayload): Promise<InvoiceDocument> {
    return firstValueFrom(
      this.http.put<InvoiceDocument>(`${this.url}/${invoiceId}/delivery-lists/${purchaseId}`, payload)
    );
  }

  /**
   * Server-authoritative reconciliation check + status flip. Returns the
   * committed invoice on success; throws (400) when reconciliation fails.
   */
  commit(invoiceId: number): Promise<InvoiceDocument> {
    return firstValueFrom(
      this.http.post<InvoiceDocument>(`${this.url}/${invoiceId}/commit`, {})
    );
  }

  discard(invoiceId: number): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.url}/${invoiceId}`));
  }
}
