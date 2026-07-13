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
  /** Per-line site override; null = inherits the delivery list's location. */
  locationId: number | null;
  /** Name of the line's own location when overridden; null when inheriting. */
  locationName: string | null;
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
  /** 'invoice' | 'receipt' (pokladničný blok) */
  documentKind: string;
  reconciliationOk: boolean;
  reconciliationNote: string | null;
  uploadedBy: string;
  uploadedAt: string;
  committedBy: string | null;
  committedAt: string | null;
  note: string | null;
  /** "file" (PDF/image picker) or "camera" (in-app scanner). */
  scanSource?: string;
  /** Distinct Pracovisko names this document's lines were assigned to. */
  locationNames?: string[];
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
  /** Informational zľava %; 0 clears it. */
  discountPercent?: number;
  isReverseCharge?: boolean;
  isService?: boolean;
  /** Per-line site: positive Location.Id assigns the row; -1 clears the override (row follows its delivery list). */
  locationId?: number;
}

/** Scanning-pipeline health (GET /api/invoices/scan-status). */
export interface ScanStatus {
  /** ok | ai-only | fallback | down */
  mode: 'ok' | 'ai-only' | 'fallback' | 'down';
  aiConfigured: boolean;
  /** UTC ISO time when the AI daily quota resets (fallback mode only). */
  aiExhaustedUntil: string | null;
}

/** Manual row addition during review (scanner missed a printed row). */
export interface AddInvoiceLinePayload {
  supplierItemCode?: string | null;
  materialNameRaw: string;
  unit?: string;
  quantity?: number;
  /** Unit price bez DPH. Omit to derive it from lineTotal ÷ quantity. */
  unitPrice?: number | null;
  /** Spolu bez DPH. Omit to compute quantity × unitPrice. */
  lineTotal?: number | null;
  vatRate?: number;
  discountPercent?: number | null;
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
  /**
   * Commit the invoice. `force` overrides a failed reconciliation (the manager
   * confirmed the figures despite a mismatch); the server records the override.
   */
  commit(invoiceId: number, force = false): Promise<InvoiceDocument> {
    const params = force ? new HttpParams().set('force', 'true') : undefined;
    return firstValueFrom(
      this.http.post<InvoiceDocument>(`${this.url}/${invoiceId}/commit`, {}, { params })
    );
  }

  /** Correct the printed grand total (incl. VAT) when the parser misread it. */
  updatePrintedTotal(invoiceId: number, totalInclVat: number): Promise<InvoiceDocument> {
    return firstValueFrom(
      this.http.put<InvoiceDocument>(`${this.url}/${invoiceId}/printed-total`, { totalInclVat })
    );
  }

  /** Correct the issue date ("dátum vyhotovenia") when the scan misread it —
   *  drives which month the document lands in on the Financie overview. */
  updateIssueDate(invoiceId: number, issueDate: string): Promise<InvoiceDocument> {
    return firstValueFrom(
      this.http.put<InvoiceDocument>(`${this.url}/${invoiceId}/issue-date`, { issueDate })
    );
  }

  /** Correct the supplier name when OCR read a logo/stamp instead of the
   *  printed company. Allowed on review and committed documents. */
  updateSupplierName(invoiceId: number, supplierName: string): Promise<InvoiceDocument> {
    return firstValueFrom(
      this.http.put<InvoiceDocument>(`${this.url}/${invoiceId}/supplier-name`, { supplierName })
    );
  }

  /** Delete a single (usually phantom/OCR-junk) line during review. */
  deleteLine(invoiceId: number, lineId: number): Promise<InvoiceDocument> {
    return firstValueFrom(
      this.http.delete<InvoiceDocument>(`${this.url}/${invoiceId}/lines/${lineId}`)
    );
  }

  /** Manually add a row the scanner missed (review only). */
  addLine(invoiceId: number, purchaseId: number, payload: AddInvoiceLinePayload): Promise<InvoiceDocument> {
    return firstValueFrom(
      this.http.post<InvoiceDocument>(`${this.url}/${invoiceId}/delivery-lists/${purchaseId}/lines`, payload)
    );
  }

  /** Re-read the stored document with the vision AI and replace the draft
   *  (review only). The reconciliation banner reports the honest result. */
  aiReparse(invoiceId: number): Promise<InvoiceDocument> {
    return firstValueFrom(
      this.http.post<InvoiceDocument>(`${this.url}/${invoiceId}/ai-reparse`, {})
    );
  }

  /** Scanning-pipeline health for the customer-facing banners. */
  getScanStatus(): Promise<ScanStatus> {
    return firstValueFrom(this.http.get<ScanStatus>(`${this.url}/scan-status`));
  }

  /** Map an upload/API error to an actionable Slovak message. */
  friendlyError(e: any, fallback: string): string {
    if (typeof e?.error === 'string' && e.error.trim().length > 0) return e.error;
    if (typeof e?.error?.error === 'string') return e.error.error;
    switch (e?.status) {
      case 0:   return 'Ste offline alebo server nie je dostupný. Skontrolujte pripojenie a skúste znova.';
      case 401: return 'Boli ste odhlásený — prihláste sa znova.';
      case 409: return 'Tento doklad už bol nahraný (duplicita).';
      case 413: return 'Súbor je príliš veľký.';
      case 429: return 'Denný limit rozpoznávania je vyčerpaný — skúste znova zajtra.';
      case 502:
      case 503: return 'Rozpoznávanie je momentálne nedostupné. Fotky si nechajte a skúste o pár minút.';
      default:  return e?.message ?? fallback;
    }
  }

  discard(invoiceId: number): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.url}/${invoiceId}`));
  }
}
