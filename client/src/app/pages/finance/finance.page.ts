import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { AuthService } from '../../services/auth.service';
import { FeatureFlagService } from '../../services/feature-flag.service';
import { PayrollService } from '../../services/payroll.service';
import { MaterialPurchaseService } from '../../services/material-purchase.service';
import { InvoiceService } from '../../services/invoice.service';

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
  imports: [CommonModule, RouterLink, NavbarComponent, SpinnerComponent],
  templateUrl: './finance.page.html'
})
export class FinancePage {
  auth = inject(AuthService);
  flags = inject(FeatureFlagService);
  private payroll = inject(PayrollService);
  private purchases = inject(MaterialPurchaseService);
  private invoices = inject(InvoiceService);

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

  monthLabel = computed(() => {
    const [y, m] = this.month().split('-').map(Number);
    return new Date(y, m - 1, 1).toLocaleDateString('sk-SK', { month: 'long', year: 'numeric' });
  });

  constructor() {
    this.load();
  }

  onMonthChange(value: string) {
    if (!value) return;
    this.month.set(value);
    this.load();
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

    // Material spend — sum of purchase totals in the month.
    tasks.push((async () => {
      try {
        const rows = await firstValueFrom(this.purchases.list({ from, to }));
        this.materialSpend.set(rows.reduce((s, p) => s + (p.totalCost || 0), 0));
      } catch {
        this.materialSpend.set(null);
      }
    })());

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
  }
}
