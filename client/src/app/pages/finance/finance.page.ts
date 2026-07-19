import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { AlertComponent } from '../../components/alert/alert.component';
import { AuthService } from '../../services/auth.service';
import { ApiErrorService } from '../../services/api-error.service';
import { FeatureFlagService } from '../../services/feature-flag.service';
import { CostTrendMonth, PayrollService } from '../../services/payroll.service';
import { MaterialPurchaseService } from '../../services/material-purchase.service';
import { InvoiceService, InvoiceDocument } from '../../services/invoice.service';
import { Division, DivisionService, DIVISION_LABELS } from '../../services/division.service';
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
  /** Month's documents — feeds the per-division Príjem/Výdaj/Rozdiel card. */
  invoiceDocs = signal<InvoiceDocument[]>([]);
  /** This month's paid AI extraction cost (exact usage-based). */
  aiSpend = signal<{ costEur: number; calls: number } | null>(null);
  /** Last 6 months of cost totals (oldest first) — hero sparkline. */
  trend = signal<CostTrendMonth[] | null>(null);

  private divisionSvc = inject(DivisionService);
  private router = inject(Router);

  /** Príjem / Výdaj / Rozdiel per division for the selected month (Fáza D). */
  divisionStats = computed(() => {
    const docs = this.invoiceDocs().filter(i => i.status !== 'discarded');
    return (['profistav', 'stroje'] as Division[]).map(key => {
      const mine = docs.filter(i => (i.division || 'profistav') === key);
      const income = mine.filter(i => i.direction === 'income').reduce((s, i) => s + (i.totalInclVat || 0), 0);
      const expense = mine.filter(i => i.direction !== 'income').reduce((s, i) => s + (i.totalInclVat || 0), 0);
      return { key, label: DIVISION_LABELS[key], income, expense, diff: income - expense, count: mine.length };
    });
  });

  /** D6 — monthly division report Excel for the month being viewed. */
  downloadDivisionReport() {
    this.invoices.downloadMonthlyReport(this.month());
  }

  /** Súhrn card click-through: activate the division and open its page ON
   *  THE MONTH being viewed here (carried via ?mesiac=YYYY-MM). */
  openDivision(d: Division) {
    this.divisionSvc.set(d);
    this.router.navigate(['/admin/invoices'], { queryParams: { mesiac: this.month() } });
  }

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
      || (r.trips?.cost ?? 0) > 0
      || (r.invoicedInclVat ?? 0) > 0));

  reportTotals = computed(() => {
    const rows = this.reportRows() ?? [];
    return {
      hours: rows.reduce((s, r) => s + (r.labour?.hoursWorked ?? 0), 0),
      wages: rows.reduce((s, r) => s + (r.labour?.cost ?? 0), 0),
      material: rows.reduce((s, r) => s + (r.material?.cost ?? 0), 0),
      trips: rows.reduce((s, r) => s + (r.trips?.cost ?? 0), 0),
      invoiced: rows.reduce((s, r) => s + (r.invoicedInclVat ?? 0), 0),
      total: rows.reduce((s, r) => s + (r.labour?.cost ?? 0) + (r.material?.cost ?? 0) + (r.trips?.cost ?? 0), 0)
    };
  });

  // ─── Overview hero (the three cost pillars) ───
  // Month cost = wages + material + stroje. Profistav expense invoices are
  // NOT added on top: their items land inside `materialSpend` on commit, so
  // that would double-count. AZ Stroje invoices are the opposite case — they
  // are excluded from material views by design, so without this pillar they
  // appeared in no total at all (customer: "160 € spolu, ale Stroje majú
  // −2 833 €").
  strojeExpense = computed(() =>
    this.invoiceDocs()
      .filter(i => i.status !== 'discarded' && i.direction !== 'income' && (i.division || 'profistav') === 'stroje')
      .reduce((s, i) => s + (i.totalInclVat || 0), 0));
  /** Výjazdy for the selected month — from the trend (its last month IS the
   *  selected month), the only per-month client source for trip costs. */
  tripsCost = computed(() => this.trend()?.at(-1)?.trips ?? 0);
  /** Income invoices of both divisions in the selected month. */
  incomeTotal = computed(() =>
    this.invoiceDocs()
      .filter(i => i.status !== 'discarded' && i.direction === 'income')
      .reduce((s, i) => s + (i.totalInclVat || 0), 0));
  costTotal = computed(() =>
    (this.wagesPayout() ?? 0) + (this.materialSpend() ?? 0) + this.strojeExpense() + this.tripsCost());

  /** Share of one cost pillar on the month total, in whole %. */
  pctOf(value: number | null): number {
    const t = this.costTotal();
    return t > 0 ? Math.round(((value ?? 0) / t) * 100) : 0;
  }

  /** Donut segments for the hero cost split (r=15.9155 → circumference 100,
   *  so dasharray works in percent). Negative components (advances over
   *  gross) clamp to 0; single-part months render a full ring without gaps. */
  heroDonut = computed(() => {
    const w = Math.max(this.wagesPayout() ?? 0, 0);
    const m = Math.max(this.materialSpend() ?? 0, 0);
    const st = Math.max(this.strojeExpense(), 0);
    const tr = Math.max(this.tripsCost(), 0);
    const total = w + m + st + tr;
    if (total <= 0) return [];
    const parts = [
      { cls: 'stroke-sky-500', value: w },
      { cls: 'stroke-amber-500', value: m },
      { cls: 'stroke-rose-500', value: st },
      { cls: 'stroke-violet-500', value: tr },
    ].filter(p => p.value > 0);
    const gap = parts.length > 1 ? 2 : 0;
    let start = 0;
    return parts.map(p => {
      const pct = (p.value / total) * 100;
      const visible = Math.max(pct - gap, 0.5);
      const seg = { cls: p.cls, dash: `${visible} ${100 - visible}`, offset: -(start + gap / 2) };
      start += pct;
      return seg;
    });
  });

  // ─── Trend sparklines (FLOWii-style, one per hero card) ───

  /** Sparkline coordinates in a fixed 100×28 viewBox, x spaced evenly,
   *  y scaled to the window's max. Empty = nothing to draw. */
  private buildCoords(values: number[]) {
    if (values.length < 2 || !values.some(v => v > 0)) return [];
    const max = Math.max(...values);
    const w = 100, h = 28, pad = 3;
    return values.map((v, i) => ({
      x: +(pad + (i * (w - 2 * pad)) / (values.length - 1)).toFixed(1),
      y: +(h - pad - (Math.max(v, 0) / max) * (h - 2 * pad)).toFixed(1)
    }));
  }

  /** % change vs the previous month; null when the previous month is 0. */
  private static delta(values: number[]): number | null {
    if (values.length < 2) return null;
    const prev = values[values.length - 2];
    if (prev <= 0) return null;
    return Math.round(((values[values.length - 1] - prev) / prev) * 100);
  }

  trendDelta = computed(() => FinancePage.delta((this.trend() ?? []).map(m => m.total)));
  trendCoords = computed(() => {
    const t = this.trend() ?? [];
    return this.buildCoords(t.map(m => m.total)).map((p, i) => ({ ...p, m: t[i] }));
  });
  trendPoints = computed(() => this.trendCoords().map(c => `${c.x},${c.y}`).join(' '));
  trendLastPoint = computed(() => this.trendCoords().at(-1) ?? null);

  incomeDelta = computed(() => FinancePage.delta((this.trend() ?? []).map(m => m.income)));
  incomeCoords = computed(() => {
    const t = this.trend() ?? [];
    return this.buildCoords(t.map(m => m.income)).map((p, i) => ({ ...p, m: t[i] }));
  });
  incomePoints = computed(() => this.incomeCoords().map(c => `${c.x},${c.y}`).join(' '));
  incomeLastPoint = computed(() => this.incomeCoords().at(-1) ?? null);

  private static fmtEur(v: number): string {
    return `${v.toLocaleString('sk-SK', { minimumFractionDigits: 2, maximumFractionDigits: 2 })} €`;
  }

  private static monthShort(month: string): string {
    const [y, m] = month.split('-').map(Number);
    return new Date(y, m - 1, 1).toLocaleDateString('sk-SK', { month: 'short' });
  }

  /** Hover title for one sparkline month, e.g. "júl 2026 · 12 345,60 €". */
  sparkTitle(m: CostTrendMonth, value: number): string {
    const [y, mo] = m.month.split('-').map(Number);
    const label = new Date(y, mo - 1, 1).toLocaleDateString('sk-SK', { month: 'short', year: 'numeric' });
    return `${label} · ${FinancePage.fmtEur(value)}`;
  }

  // ─── Consolidated chart: Príjmy vs. Náklady per month ───

  /** Grouped-bar geometry in a 320×130 viewBox (plot 8..112, labels below).
   *  Negative months clamp to 0 — a bar can't go below the baseline. */
  consolidated = computed(() => {
    const t = this.trend() ?? [];
    if (t.length < 2) return null;
    const raw = Math.max(...t.map(m => Math.max(m.income, m.total, 0)));
    if (raw <= 0) return null;
    const niceMax = FinancePage.niceCeil(raw);
    const W = 320, top = 8, bottom = 112;
    const plotH = bottom - top;
    const groupW = (W - 12) / t.length;
    const barW = Math.min(15, groupW * 0.3);
    const groups = t.map((m, i) => {
      const cx = 6 + groupW * i + groupW / 2;
      const hInc = (Math.max(m.income, 0) / niceMax) * plotH;
      const hCost = (Math.max(m.total, 0) / niceMax) * plotH;
      return {
        m, cx, barW,
        label: FinancePage.monthShort(m.month),
        incX: cx - barW - 1.5, incY: bottom - hInc, incH: hInc,
        costX: cx + 1.5, costY: bottom - hCost, costH: hCost,
        groupX: cx - groupW / 2, groupW,
      };
    });
    return { groups, niceMax, W, top, bottom, midY: (top + bottom) / 2 };
  });

  /** Window sums for the row under the consolidated chart. */
  consolidatedTotals = computed(() => {
    const t = this.trend() ?? [];
    const income = t.reduce((s, m) => s + m.income, 0);
    const cost = t.reduce((s, m) => s + m.total, 0);
    return { income, cost, diff: income - cost };
  });

  /** Hover title for one chart month: both sides + rozdiel. */
  barTitle(m: CostTrendMonth): string {
    const [y, mo] = m.month.split('-').map(Number);
    const label = new Date(y, mo - 1, 1).toLocaleDateString('sk-SK', { month: 'long', year: 'numeric' });
    const diff = m.income - m.total;
    return `${label}\nPríjmy: ${FinancePage.fmtEur(m.income)}\nNáklady: ${FinancePage.fmtEur(m.total)}\nRozdiel: ${diff >= 0 ? '+' : '−'} ${FinancePage.fmtEur(Math.abs(diff))}`;
  }

  /** Axis label without cents — the gridline values. */
  axisLabel(v: number): string {
    return v.toLocaleString('sk-SK', { maximumFractionDigits: 0 });
  }

  /** Smallest "nice" ceiling (1/2/2.5/5 × 10^k) ≥ v — the chart's y max. */
  private static niceCeil(v: number): number {
    const pow = Math.pow(10, Math.floor(Math.log10(v)));
    for (const m of [1, 2, 2.5, 5, 10]) {
      if (m * pow >= v) return m * pow;
    }
    return 10 * pow;
  }

  /** Largest per-site total in the report — scales the comparison bars. */
  reportMax = computed(() => {
    const rows = this.visibleReportRows();
    const m = Math.max(0, ...rows.map(r => (r.labour?.cost ?? 0) + (r.material?.cost ?? 0) + (r.trips?.cost ?? 0)));
    return m > 0 ? m : 1;
  });

  /** Bar width (%) for a value relative to the biggest site's total. */
  barPct(value: number | null | undefined): number {
    return Math.round(((value ?? 0) / this.reportMax()) * 100);
  }

  rowTotal(r: LocationPnl): number {
    return (r.labour?.cost ?? 0) + (r.material?.cost ?? 0) + (r.trips?.cost ?? 0);
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
      // Sparkline is decoration — best-effort like every other card.
      tasks.push((async () => {
        try {
          this.trend.set(await this.payroll.costTrend(this.month()));
        } catch {
          this.trend.set(null);
        }
      })());
    }

    if (this.canInvoices()) {
      tasks.push((async () => {
        try {
          const rows = await this.invoices.list({ from, to });
          this.invoiceDocs.set(rows);
          // The footnote explains what's already inside Materiál — so it must
          // count ONLY the documents that actually feed material purchases:
          // expense direction, non-stroje (income and AZ Stroje docs are
          // excluded from material views), not discarded. Counting everything
          // made the note contradict a 0 € Materiál.
          const inMaterial = rows.filter(d =>
            d.status !== 'discarded'
            && d.direction !== 'income'
            && (d.division || 'profistav') !== 'stroje');
          this.invoiceCount.set(inMaterial.length);
          this.invoiceTotal.set(inMaterial.reduce((s, d) => s + (d.totalInclVat || 0), 0));
          this.invoicePending.set(rows.filter(d => d.status === 'review').length);
        } catch {
          this.invoiceDocs.set([]);
          this.invoiceCount.set(null);
          this.invoiceTotal.set(null);
          this.invoicePending.set(null);
        }
      })());
    }

    await Promise.all(tasks);
    if (this.canInvoices()) {
      this.invoices.getAiSpend().then(s => this.aiSpend.set(s)).catch(() => this.aiSpend.set(null));
    }
    this.loading.set(false);
    // The per-location spending report loads independently of the cards.
    this.loadReport();
  }
}
