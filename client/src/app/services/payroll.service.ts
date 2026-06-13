import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';

// ─── Types — mirror API/DTOs/Dtos.cs ───

export interface PayrollRow {
  employeeId: number;
  firstName: string;
  lastName: string;
  isActive: boolean;
  hoursWorked: number;
  hourlyWageSnapshotAvg: number | null;
  hourlyWageCurrent: number | null;
  wageMissing: boolean;
  advancesTotal: number;
  gross: number;
  payout: number;
}

export interface PayrollMonthly {
  month: string;          // 'YYYY-MM'
  rows: PayrollRow[];
  totals: PayrollRow;
}

export interface EmployeeAdvance {
  id: number;
  employeeId: number;
  date: string;           // ISO
  amount: number;
  note: string | null;
  createdBy: string | null;
  createdAt: string;
}

export interface CreateEmployeeAdvance {
  employeeId: number;
  date: string;           // YYYY-MM-DD
  amount: number;
  note?: string;
}

export interface UpdateEmployeeAdvance {
  date: string;
  amount: number;
  note?: string;
}

/**
 * Selected payroll period — either a whole calendar month ('YYYY-MM') or an
 * explicit inclusive date range ('YYYY-MM-DD'). Mirrors the query contract
 * of GET /api/payroll/monthly (month= XOR from=&to=).
 */
export type PayrollPeriod = { month: string } | { from: string; to: string };

/**
 * Frontend client for the Mzdy / payroll API. Manager-only (JWT). All
 * endpoints behind the PayrollAndPnL feature flag. See
 * PAYROLL_AND_PNL_PLAN.md.
 */
@Injectable({ providedIn: 'root' })
export class PayrollService {
  private readonly url   = `${environment.apiUrl}/payroll`;
  private readonly advUrl = `${environment.apiUrl}/employee-advances`;

  constructor(private http: HttpClient) {}

  // ─── Period summary ────────────────────────────────────────────

  monthly(period: PayrollPeriod): Promise<PayrollMonthly> {
    return firstValueFrom(
      this.http.get<PayrollMonthly>(`${this.url}/monthly`, { params: this.periodParams(period) })
    );
  }

  private periodParams(period: PayrollPeriod): HttpParams {
    return 'month' in period
      ? new HttpParams().set('month', period.month)
      : new HttpParams().set('from', period.from).set('to', period.to);
  }

  private periodQuery(period: PayrollPeriod): string {
    return 'month' in period
      ? `month=${encodeURIComponent(period.month)}`
      : `from=${encodeURIComponent(period.from)}&to=${encodeURIComponent(period.to)}`;
  }

  /** Filename token matching the API: '2026-05' or '2026-05-04_2026-05-10'. */
  private periodToken(period: PayrollPeriod): string {
    return 'month' in period ? period.month : `${period.from}_${period.to}`;
  }

  /**
   * Set an employee's hourly wage and (optionally) backfill the WageAtTime
   * snapshot on existing TimeEntries from a given date onwards.
   *
   * Returns the number of historical entries that were updated. `applyFrom`
   * null = no backfill, only future entries pick up the rate via the
   * snapshot pattern. `rate` null = clear the rate; applyFrom is ignored.
   */
  setWage(employeeId: number, rate: number | null, applyFrom: string | null): Promise<number> {
    return firstValueFrom(
      this.http.post<number>(`${this.url}/employee/${employeeId}/set-wage`, {
        rate,
        applyFrom: applyFrom || null
      })
    );
  }

  // ─── Excel exports ─────────────────────────────────────────────

  /** Period summary XLSX — opens a browser save dialog via blob URL. */
  downloadMonthlySummary(period: PayrollPeriod): void {
    const url = `${this.url}/monthly/export?${this.periodQuery(period)}`;
    this.downloadBlob(url, `Mzdy_${this.periodToken(period)}.xlsx`);
  }

  /** Per-employee comprehensive workbook. */
  downloadEmployeeReport(employeeId: number, period: PayrollPeriod): void {
    const url = `${this.url}/employee/${employeeId}/export?${this.periodQuery(period)}`;
    this.downloadBlob(url, `Mzda_${this.periodToken(period)}.xlsx`);
  }

  private downloadBlob(url: string, fallbackName: string): void {
    this.http.get(url, { responseType: 'blob', observe: 'response' }).subscribe({
      next: res => {
        const blob = res.body!;
        const objectUrl = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = objectUrl;
        const cd = res.headers.get('Content-Disposition') || '';
        const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/.exec(cd);
        a.download = match?.[1] ?? fallbackName;
        a.click();
        URL.revokeObjectURL(objectUrl);
      },
      error: () => alert('Sťahovanie Excel súboru zlyhalo. Skúste znova.')
    });
  }

  // ─── Advances CRUD ─────────────────────────────────────────────

  listAdvances(filters: { employeeId?: number; from?: string; to?: string } = {}): Promise<EmployeeAdvance[]> {
    let params = new HttpParams();
    if (filters.employeeId != null) params = params.set('employeeId', String(filters.employeeId));
    if (filters.from) params = params.set('from', filters.from);
    if (filters.to)   params = params.set('to', filters.to);
    return firstValueFrom(this.http.get<EmployeeAdvance[]>(this.advUrl, { params }));
  }

  createAdvance(dto: CreateEmployeeAdvance): Promise<EmployeeAdvance> {
    return firstValueFrom(this.http.post<EmployeeAdvance>(this.advUrl, dto));
  }

  updateAdvance(id: number, dto: UpdateEmployeeAdvance): Promise<EmployeeAdvance> {
    return firstValueFrom(this.http.put<EmployeeAdvance>(`${this.advUrl}/${id}`, dto));
  }

  deleteAdvance(id: number): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${this.advUrl}/${id}`));
  }
}
