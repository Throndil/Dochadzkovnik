import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { AlertComponent } from '../../components/alert/alert.component';
import { AuthService } from '../../services/auth.service';
import { ApiErrorService } from '../../services/api-error.service';
import { FeatureFlagService } from '../../services/feature-flag.service';
import { PayrollService } from '../../services/payroll.service';
import { MaterialPurchaseService } from '../../services/material-purchase.service';
import { InvoiceService } from '../../services/invoice.service';
import { LocationService, LocationPnl } from '../../services/location.service';
import { DatepickerDirective } from '../../directives/datepicker.directive';
import { MonthPickerComponent } from '../../components/month-picker/month-picker.component';
import { FormsModule } from '@angular/forms';

/**
 * /admin/finance — landing/overview for the Finance ("Financie") side.
 *
 * A month-scoped summary built entirely from existing endpoints (payroll,
 * material purchases, invoices) — no new backend. Each metric is fetched
 * best-effort and gated by the same feature flags as its section, so a
 * flag being off (or a 403) simply hides that card rather than breaking the
 * page. Company-wide profit isn't shown here because P&L is per-location
 * (see location detail); this page focuses on month costs + quick access.
 */
@Component({
  selector: 'app-finance',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink, NavbarComponent, SpinnerComponent, AlertComponent, DatepickerDirective, MonthPickerComponent],
  templateUrl: './finance.page.html'
})
export class FinancePage {
  auth = inject(AuthService);
  flags = inject(FeatureFlagService);
  private payroll = inject(PayrollService);
  private purchases = inject(MaterialPurchaseService);
  private invoices = inject(InvoiceService);
  private locations = inject(LocationService);
  private apiError = inject(ApiErrorService);

  /** Selected month as 'YYYY-MM'; defaults to the current month. */
  month = signal(new Date().toISOString().slice(0, 7));
  loading = signal(false);

  wagesPayout = signal<number | null>(null);
  materialSpend = signal<number | null>(null);
  invoiceTotal = signal<number | null>(null);
  invoiceCount = signal<number | null>(null);
  invoicePending = signal<number | null>(null);

  canPayroll = computed(() => this.flags.payrollAndPnL() || this.auth.isSuperAdmin());
  canInvoices = computed(() => this.flags.invoiceScanning() || this.auth.isSuperAdmin());
  canMaterial = computed(() => this.flags.materialPurchases() || this.auth.isSuperAdmin());

  monthLabel = computed(() => {
    const [y, m] = this.month().split('-').map(Number);
    return new Date(y, m - 1, 1).toLocaleDateString('sk-SK', { month: 'long', year: 'numeric' });
  });

  // ─── Spending report (Náklady podľa pracoviska) ───
  // Own from/to range so the manager can report across any time frame,
  // not just one calendar month. Defaults follow the selected month.
  reportFrom = signal('');
  reportTo = signal('');
  reportRows = signal<LocationPnl[] | null>(null);
  reportLoading = signal(false);
  /** Error channel for the spending report ONLY — the per-card load()
   *  failures stay silent by design (feature-flag-gated, best-effort). */
  error = signal<string | null>(null);

  /** Rows with any activity in the range — a site where nothing was worked,
   *  bought or invoiced is noise for the customer. Totals still sum over
   *  ALL rows (identical result; zero rows contribute zero). */
  visibleReportRows = computed(() =>
    (this.reportRows() ?? []).filter(r =>
      (r.labour?.hoursWorked ?? 0) > 0
      || (r.labour?.cost ?? 0) > 0
      || (r.material?.cost ?? 0) > 0
      || (r.invoicedInclVat ?? 0) > 0));

  reportTotals = computed(() => {
    const rows = this.reportRows() ?? [];
    return {
      hours: rows.reduce((s, r) => s + (r.labour?.hoursWorked ?? 0), 0),
      wages: rows.reduce((s, r) => s + (r.labour?.cost ?? 0), 0),
      material: rows.reduce((s, r) => s + (r.material?.cost ?? 0), 0),
      invoiced: rows.reduce((s, r) => s + (r.invoicedInclVat ?? 0), 0),
      total: rows.reduce((s, r) => s + (r.labour?.cost ?? 0) + (r.material?.cost ?? 0), 0)
    };
  });

  // ─── Overview hero (the two real cost pillars) ───
  // Month cost = wages + material. Invoices are NOT added: supplier-invoice
  // material is already counted inside `materialSpend` (the P&L does the same),
  // so adding faktúry on top would double-count. Faktúry is shown as a source,
  // not a third pillar.
  costTotal = computed(() => (this.wagesPayout() ?? 0) + (this.materialSpend() ?? 0));
  wagesPct = computed(() => {
    const t = this.costTotal();
    return t > 0 ? Math.round((this.wagesPayout() ?? 0) / t * 100) : 0;
  });

  /** Largest per-site total in the report — scales the comparison bars. */
  reportMax = computed(() => {
    const rows = this.visibleReportRows();
    const m = Math.max(0, ...rows.map(r => (r.labour?.cost ?? 0) + (r.material?.cost ?? 0)));
    return m > 0 ? m : 1;
  });

  /** Bar width (%) for a value relative to the biggest site's total. */
  barPct(value: number | null | undefined): number {
    return Math.round(((value ?? 0) / this.reportMax()) * 100);
  }

  rowTotal(r: LocationPnl): number {
    return (r.labour?.cost ?? 0) + (r.material?.cost ?? 0);
  }

  constructor() {
    const { from, to } = this.monthRange();
    this.reportFrom.set(from);
    this.reportTo.set(to);
    this.load();
  }

  onMonthChange(value: string) {
    if (!value) return;
    this.month.set(value);
    // Keep the report range in sync with the month until the manager
    // overrides it explicitly via the range inputs.
    const { from, to } = this.monthRange();
    this.reportFrom.set(from);
    this.reportTo.set(to);
    this.load();
  }

  onReportRangeChange(from: string, to: string) {
    if (!from || !to) return;
    this.reportFrom.set(from);
    this.reportTo.set(to);
    this.loadReport();
  }

  async loadReport() {
    if (!this.canPayroll()) return;
    this.reportLoading.set(true);
    this.error.set(null);
    try {
      const rows = await firstValueFrom(this.locations.getPnlSummary(this.reportFrom(), this.reportTo()));
      this.reportRows.set(rows);
    } catch (e) {
      this.error.set(this.apiError.friendly(e, 'Načítanie reportu nákladov zlyhalo'));
      this.reportRows.set(null);
    } finally {
      this.reportLoading.set(false);
    }
  }

  /** First and last calendar day of the selected month, as 'YYYY-MM-DD'. */
  private monthRange(): { from: string; to: string } {
    const [y, m] = this.month().split('-').map(Number);
    const lastDay = new Date(y, m, 0).getDate(); // day 0 of next month
    return { from: `${this.month()}-01`, to: `${this.month()}-${String(lastDay).padStart(2, '0')}` };
  }

  async load() {
    this.loading.set(true);
    const { from, to } = this.monthRange();
    const tasks: Promise<void>[] = [];

    // Material spend — sum of purchase totals in the month. Only fetched
    // when the MaterialPurchases feature is on (the endpoint 403s otherwise
    // and the card would show a dead "—").
    if (this.canMaterial()) {
      tasks.push((async () => {
        try {
          const rows = await firstValueFrom(this.purchases.list({ from, to }));
          this.materialSpend.set(rows.reduce((s, p) => s + (p.totalCost || 0), 0));
        } catch {
          this.materialSpend.set(null);
        }
      })());
    }

    if (this.canPayroll()) {
      tasks.push((async () => {
        try {
          const res = await this.payroll.monthly({ month: this.month() });
          this.wagesPayout.set(res.totals?.payout ?? null);
        } catch {
          this.wagesPayout.set(null);
        }
      })());
    }

    if (this.canInvoices()) {
      tasks.push((async () => {
        try {
          const rows = await this.invoices.list({ from, to });
          this.invoiceCount.set(rows.length);
          this.invoiceTotal.set(rows.reduce((s, d) => s + (d.totalInclVat || 0), 0));
          this.invoicePending.set(rows.filter(d => d.status === 'review').length);
        } catch {
          this.invoiceCount.set(null);
          this.invoiceTotal.set(null);
          this.invoicePending.set(null);
        }
      })());
    }

    await Promise.all(tasks);
    this.loading.set(false);
    // The per-location spending report loads independently of the cards.
    this.loadReport();
  }
}
