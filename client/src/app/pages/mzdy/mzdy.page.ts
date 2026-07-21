import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { AlertComponent } from '../../components/alert/alert.component';
import {
  PayrollService,
  PayrollRow,
  PayrollMonthly,
  PayrollPeriod,
  EmployeeAdvance
} from '../../services/payroll.service';
import { EmployeeService } from '../../services/employee.service';
import { ApiErrorService } from '../../services/api-error.service';
import { DivisionService } from '../../services/division.service';
import { DatepickerDirective } from '../../directives/datepicker.directive';
import { MonthPickerComponent } from '../../components/month-picker/month-picker.component';
import { StepperComponent } from '../../components/stepper/stepper.component';
import { tagTint } from '../../utils/tag-color';

type PeriodMode = 'month' | 'week' | 'custom';

/**
 * /admin/mzdy — monthly payroll table per PAYROLL_AND_PNL_PLAN.md.
 *
 * Each row: Meno | Hodiny | Hodinová sadzba (inline-editable) | Zálohy (drawer) |
 * Hrubá mzda | Výplata. Two Excel downloads per page: monthly summary and
 * per-employee detailed paycheck. Default month = previous calendar month;
 * last picked month persisted in localStorage.
 */
@Component({
  selector: 'app-mzdy',
  standalone: true,
  imports: [CommonModule, FormsModule, NavbarComponent, SpinnerComponent, AlertComponent, DatepickerDirective, MonthPickerComponent, StepperComponent],
  templateUrl: './mzdy.page.html'
})
export class MzdyPage implements OnInit {
  private svc = inject(PayrollService);
  private empSvc = inject(EmployeeService);
  private apiError = inject(ApiErrorService);
  /** Mzdy are division-scoped (Fáza D8) — follows the navbar burger. */
  division = inject(DivisionService);

  // ─── Period selection ─────────────────────────────────────────
  /** Mesiac | Týždeň | Vlastné. Persists in localStorage. */
  periodMode = signal<PeriodMode>(this.initialPeriodMode());
  /** YYYY-MM. Default = previous calendar month. Persists in localStorage. */
  month = signal<string>(this.initialMonth());
  /** Monday of the selected week (YYYY-MM-DD). Default = current week. */
  weekStart = signal<string>(this.initialStoredDate('mzdy.weekStart', this.mondayOf(new Date())));
  /** Custom range bounds (YYYY-MM-DD, inclusive). */
  customFrom = signal<string>(this.initialStoredDate('mzdy.customFrom', this.firstOfCurrentMonth()));
  customTo = signal<string>(this.initialStoredDate('mzdy.customTo', this.todayIso()));

  // ─── Data ─────────────────────────────────────────────────────
  data = signal<PayrollMonthly | null>(null);
  loading = signal(false);
  error = signal<string | null>(null);

  periodLabel = computed(() => {
    switch (this.periodMode()) {
      case 'week': {
        const r = this.weekRange();
        return `Týždeň ${this.formatIsoDay(r.from)} – ${this.formatIsoDay(r.to)}`;
      }
      case 'custom':
        return this.customRangeValid()
          ? `${this.formatIsoDay(this.customFrom())} – ${this.formatIsoDay(this.customTo())}`
          : 'Zvoľte rozsah dátumov';
      default:
        return this.formatMonthLabel(this.month());
    }
  });

  weekLabel = computed(() => {
    const r = this.weekRange();
    return `${this.formatIsoDay(r.from)} – ${this.formatIsoDay(r.to)}`;
  });

  // ─── Inline rate edit ─────────────────────────────────────────
  editingRateFor = signal<number | null>(null);
  rateDraft = signal<number | null>(null);
  /** "Od" date for the rate change. Defaults to first of currently-viewed month. */
  rateApplyFromDraft = signal<string>('');
  /** When true, the rate change retroactively updates WageAtTime on existing entries. */
  rateBackfillEnabled = signal<boolean>(true);
  savingRate = signal(false);

  // ─── Advances drawer ──────────────────────────────────────────
  advancesEmployee = signal<PayrollRow | null>(null);
  advances = signal<EmployeeAdvance[]>([]);
  advancesLoading = signal(false);

  // New-advance form (inline at top of the drawer)
  newAdvanceDate = signal<string>(this.todayIso());
  newAdvanceAmount = signal<number | null>(null);
  newAdvanceNote = signal<string>('');
  newAdvanceSaving = signal(false);
  newAdvanceError = signal<string | null>(null);

  // Inline edit of an existing advance (pencil → fields → save/cancel)
  editingAdvanceId = signal<number | null>(null);
  editAdvanceDate = signal<string>('');
  editAdvanceAmount = signal<number | null>(null);
  editAdvanceNote = signal<string>('');
  editAdvanceSaving = signal(false);
  editAdvanceError = signal<string | null>(null);

  ngOnInit() {
    this.load();
  }

  // ─── Loading ──────────────────────────────────────────────────

  async load() {
    this.loading.set(true);
    this.error.set(null);
    try {
      const d = await this.svc.monthly(this.periodParam(), this.division.active());
      this.data.set(d);
    } catch (e: any) {
      this.error.set(this.errMsg(e));
    } finally {
      this.loading.set(false);
    }
  }

  // ─── Period picker ────────────────────────────────────────────

  setPeriodMode(mode: PeriodMode) {
    if (this.periodMode() === mode) return;
    this.periodMode.set(mode);
    try { localStorage.setItem('mzdy.periodMode', mode); } catch { /* ignore */ }
    if (mode === 'custom' && !this.customRangeValid()) return;
    this.load();
  }

  onMonthChange(value: string) {
    if (!/^\d{4}-\d{2}$/.test(value)) return;
    this.month.set(value);
    try { localStorage.setItem('mzdy.month', value); } catch { /* ignore */ }
    this.load();
  }

  shiftMonth(delta: -1 | 1) {
    const [y, m] = this.month().split('-').map(Number);
    const d = new Date(y, m - 1 + delta, 1);
    const next = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
    this.onMonthChange(next);
  }

  shiftWeek(delta: -1 | 1) {
    const [y, m, d] = this.weekStart().split('-').map(Number);
    const next = this.isoDate(new Date(y, m - 1, d + delta * 7));
    this.weekStart.set(next);
    try { localStorage.setItem('mzdy.weekStart', next); } catch { /* ignore */ }
    this.load();
  }

  onCustomChange(which: 'from' | 'to', value: string) {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return;
    (which === 'from' ? this.customFrom : this.customTo).set(value);
    try {
      localStorage.setItem(which === 'from' ? 'mzdy.customFrom' : 'mzdy.customTo', value);
    } catch { /* ignore */ }
    if (this.customRangeValid()) this.load();
  }

  customRangeValid(): boolean {
    const f = this.customFrom();
    const t = this.customTo();
    return /^\d{4}-\d{2}-\d{2}$/.test(f) && /^\d{4}-\d{2}-\d{2}$/.test(t) && f <= t;
  }

  /** Inclusive [from, to] of the selected period (YYYY-MM-DD). */
  periodRange(): { from: string; to: string } {
    switch (this.periodMode()) {
      case 'week':
        return this.weekRange();
      case 'custom':
        return { from: this.customFrom(), to: this.customTo() };
      default: {
        const [y, m] = this.month().split('-').map(Number);
        return { from: `${this.month()}-01`, to: this.isoDate(new Date(y, m, 0)) };
      }
    }
  }

  private weekRange(): { from: string; to: string } {
    const from = this.weekStart();
    const [y, m, d] = from.split('-').map(Number);
    return { from, to: this.isoDate(new Date(y, m - 1, d + 6)) };
  }

  /** Query shape for the payroll API: month mode keeps the month= contract. */
  private periodParam(): PayrollPeriod {
    return this.periodMode() === 'month' ? { month: this.month() } : this.periodRange();
  }

  private initialPeriodMode(): PeriodMode {
    try {
      const saved = localStorage.getItem('mzdy.periodMode');
      if (saved === 'month' || saved === 'week' || saved === 'custom') return saved;
    } catch { /* ignore */ }
    return 'month';
  }

  private initialMonth(): string {
    try {
      const saved = localStorage.getItem('mzdy.month');
      if (saved && /^\d{4}-\d{2}$/.test(saved)) return saved;
    } catch { /* ignore */ }
    const now = new Date();
    return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`;
  }

  private initialStoredDate(key: string, fallback: string): string {
    try {
      const saved = localStorage.getItem(key);
      if (saved && /^\d{4}-\d{2}-\d{2}$/.test(saved)) return saved;
    } catch { /* ignore */ }
    return fallback;
  }

  /** Monday (start) of the week containing d, as YYYY-MM-DD. */
  private mondayOf(d: Date): string {
    return this.isoDate(new Date(d.getFullYear(), d.getMonth(), d.getDate() - ((d.getDay() + 6) % 7)));
  }

  private firstOfCurrentMonth(): string {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-01`;
  }

  private isoDate(d: Date): string {
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }

  private todayIso(): string {
    return this.isoDate(new Date());
  }

  // ─── Inline rate edit ─────────────────────────────────────────

  startEditRate(row: PayrollRow) {
    this.editingRateFor.set(row.employeeId);
    this.rateDraft.set(row.hourlyWageCurrent);
    // Default "from" date = start of currently-viewed period. Manager can
    // override if they're recording a mid-month promotion (e.g. 15.05.).
    this.rateApplyFromDraft.set(this.periodRange().from);
    this.rateBackfillEnabled.set(true);
  }

  cancelEditRate() {
    this.editingRateFor.set(null);
    this.rateDraft.set(null);
    this.rateApplyFromDraft.set('');
  }

  async saveRate(row: PayrollRow) {
    const parsed = this.rateDraft();
    if (parsed !== null && (!isFinite(parsed) || parsed < 0)) {
      alert('Neplatná sadzba.');
      return;
    }
    const applyFrom = (parsed !== null && this.rateBackfillEnabled())
      ? (this.rateApplyFromDraft() || null)
      : null;
    this.savingRate.set(true);
    try {
      const backfilled = await this.svc.setWage(row.employeeId, parsed, applyFrom);
      this.editingRateFor.set(null);
      this.rateDraft.set(null);
      this.rateApplyFromDraft.set('');
      // Feedback when there was a meaningful retroactive change.
      if (backfilled > 0) {
        // No modal — small toast feel via alert is acceptable here since
        // the manager is rate-limited by the click and the number is
        // useful to surface ("8 entries got the new rate").
        alert(`Sadzba uložená. Aktualizovaných ${backfilled} historických záznamov.`);
      }
      await this.load();
    } catch (e: any) {
      alert(this.apiError.friendly(e, 'Uloženie sadzby zlyhalo'));
    } finally {
      this.savingRate.set(false);
    }
  }

  // ─── Advances drawer ──────────────────────────────────────────

  async openAdvancesDrawer(row: PayrollRow) {
    this.advancesEmployee.set(row);
    this.advancesLoading.set(true);
    this.advances.set([]);
    this.resetNewAdvance();
    this.cancelEditAdvance();
    try {
      await this.reloadAdvances(row.employeeId);
    } catch (e: any) {
      this.newAdvanceError.set(this.errMsg(e));
    } finally {
      this.advancesLoading.set(false);
    }
  }

  closeAdvancesDrawer() {
    this.advancesEmployee.set(null);
    this.advances.set([]);
    this.resetNewAdvance();
    this.cancelEditAdvance();
  }

  /** Refetch the drawer list for the currently selected period. */
  private async reloadAdvances(employeeId: number) {
    const { from, to } = this.periodRange();
    const rows = await this.svc.listAdvances({ employeeId, from, to });
    this.advances.set(rows);
  }

  /**
   * After load(), re-point the drawer at the fresh row so its header
   * (negative-payout note) reflects the new totals.
   */
  private refreshDrawerEmployee() {
    const emp = this.advancesEmployee();
    if (!emp) return;
    const fresh = this.data()?.rows.find(r => r.employeeId === emp.employeeId);
    if (fresh) this.advancesEmployee.set(fresh);
  }

  private resetNewAdvance() {
    this.newAdvanceDate.set(this.defaultAdvanceDate());
    this.newAdvanceAmount.set(null);
    this.newAdvanceNote.set('');
    this.newAdvanceError.set(null);
  }

  /**
   * Default date for a new advance: today when it falls in the period being
   * viewed, otherwise clamped into that period. This way an advance you add
   * while looking at, say, May lands in May and shows up in the list — rather
   * than silently saving into the current month and "disappearing".
   */
  private defaultAdvanceDate(): string {
    const today = this.todayIso();
    const { from, to } = this.periodRange();
    if (today < from) return from;
    if (today > to) return to;
    return today;
  }

  async submitNewAdvance() {
    const emp = this.advancesEmployee();
    if (!emp) return;
    const amount = this.newAdvanceAmount();
    if (amount == null || !isFinite(amount) || amount <= 0) {
      this.newAdvanceError.set('Suma musí byť kladná.');
      return;
    }
    this.newAdvanceSaving.set(true);
    this.newAdvanceError.set(null);
    try {
      await this.svc.createAdvance({
        employeeId: emp.employeeId,
        date: this.newAdvanceDate(),
        amount,
        note: this.newAdvanceNote() || undefined
      });
      // Refresh the drawer list AND the main table totals.
      await this.reloadAdvances(emp.employeeId);
      this.resetNewAdvance();
      await this.load();
      this.refreshDrawerEmployee();
    } catch (e: any) {
      this.newAdvanceError.set(this.errMsg(e));
    } finally {
      this.newAdvanceSaving.set(false);
    }
  }

  async deleteAdvance(advance: EmployeeAdvance) {
    const emp = this.advancesEmployee();
    const who = emp ? ` (${emp.firstName} ${emp.lastName})` : '';
    if (!confirm(`Naozaj zmazať zálohu ${this.formatMoney(advance.amount)} €${who} z ${this.formatDate(advance.date)}?`)) return;
    try {
      await this.svc.deleteAdvance(advance.id);
      this.advances.update(arr => arr.filter(a => a.id !== advance.id));
      await this.load();
      this.refreshDrawerEmployee();
    } catch (e: any) {
      alert(this.apiError.friendly(e, 'Zmazanie zálohy zlyhalo'));
    }
  }

  // ─── Inline advance edit ──────────────────────────────────────

  startEditAdvance(a: EmployeeAdvance) {
    this.editingAdvanceId.set(a.id);
    this.editAdvanceDate.set(a.date.slice(0, 10));
    this.editAdvanceAmount.set(a.amount);
    this.editAdvanceNote.set(a.note ?? '');
    this.editAdvanceError.set(null);
  }

  cancelEditAdvance() {
    this.editingAdvanceId.set(null);
    this.editAdvanceDate.set('');
    this.editAdvanceAmount.set(null);
    this.editAdvanceNote.set('');
    this.editAdvanceError.set(null);
  }

  async saveEditAdvance(a: EmployeeAdvance) {
    const emp = this.advancesEmployee();
    if (!emp) return;
    const amount = this.editAdvanceAmount();
    if (amount == null || !isFinite(amount) || amount <= 0) {
      this.editAdvanceError.set('Suma musí byť kladná.');
      return;
    }
    if (!/^\d{4}-\d{2}-\d{2}$/.test(this.editAdvanceDate())) {
      this.editAdvanceError.set('Neplatný dátum.');
      return;
    }
    this.editAdvanceSaving.set(true);
    this.editAdvanceError.set(null);
    try {
      await this.svc.updateAdvance(a.id, {
        date: this.editAdvanceDate(),
        amount,
        note: this.editAdvanceNote() || undefined
      });
      this.cancelEditAdvance();
      // Refresh the drawer list AND the main table totals.
      await this.reloadAdvances(emp.employeeId);
      await this.load();
      this.refreshDrawerEmployee();
    } catch (e: any) {
      this.editAdvanceError.set(this.errMsg(e));
    } finally {
      this.editAdvanceSaving.set(false);
    }
  }

  // ─── Excel exports ────────────────────────────────────────────

  /** Shared busy state for both Excel export buttons. */
  exporting = signal(false);

  downloadMonthlySummary() {
    this.exporting.set(true);
    this.svc.downloadMonthlySummary(this.periodParam(), this.division.active());
    // The download helper is fire-and-forget (no completion callback), so
    // clear the busy state after a bounded ~4s delay — a bounded visual
    // busy beats a permanently dead button.
    setTimeout(() => this.exporting.set(false), 4000);
  }

  /** W3 — výplatné pásky: prints the hidden print-only slips grid
   *  (multiple employees per A4). */
  printSlips() {
    window.print();
  }

  downloadEmployeeReport(row: PayrollRow) {
    this.exporting.set(true);
    this.svc.downloadEmployeeReport(row.employeeId, this.periodParam(), `${row.firstName} ${row.lastName}`);
    // Same fire-and-forget download helper — bounded reset, see above.
    setTimeout(() => this.exporting.set(false), 4000);
  }

  // ─── Helpers ──────────────────────────────────────────────────

  /** Advances exceed gross — payout is negative (and a wage IS set). */
  isOverdrawn(row: PayrollRow): boolean {
    return row.payout < 0 && !(row.wageMissing && row.hoursWorked > 0);
  }

  formatMoney(v: number | null | undefined): string {
    if (v == null) return '—';
    return new Intl.NumberFormat('sk-SK', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(v);
  }

  formatHours(v: number): string {
    return new Intl.NumberFormat('sk-SK', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(v);
  }

  tagTint = tagTint;

  /** 1–2 letter monogram for the row avatar. */
  initials(row: PayrollRow): string {
    const s = ((row.firstName || '').trim().charAt(0) + (row.lastName || '').trim().charAt(0)).toUpperCase();
    return s || '?';
  }

  formatDate(iso: string): string {
    const d = new Date(iso);
    return `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')}.${d.getFullYear()}`;
  }

  /** dd.MM.yyyy from a date-only YYYY-MM-DD string (no timezone parsing). */
  private formatIsoDay(iso: string): string {
    const [y, m, d] = iso.split('-').map(Number);
    return `${String(d).padStart(2, '0')}.${String(m).padStart(2, '0')}.${y}`;
  }

  private formatMonthLabel(month: string): string {
    const [y, m] = month.split('-').map(Number);
    const names = ['Január', 'Február', 'Marec', 'Apríl', 'Máj', 'Jún', 'Júl', 'August', 'September', 'Október', 'November', 'December'];
    return `${names[m - 1]} ${y}`;
  }

  private errMsg(e: any): string {
    return typeof e?.error === 'string' ? e.error :
           typeof e?.error?.error === 'string' ? e.error.error :
           e?.message ?? 'Neznáma chyba.';
  }
}
