import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { AlertComponent } from '../../components/alert/alert.component';
import { PlanEntry, PlannerService, SavePlanEntry } from '../../services/planner.service';
import { Employee, EmployeeService } from '../../services/employee.service';
import { Location, LocationService } from '../../services/location.service';

/** One rendered bar: grid column span within the week + lane row. */
interface PlanBar {
  entry: PlanEntry;
  /** 1-based day index within the week (1 = Monday). */
  colStart: number;
  colEnd: number;       // exclusive
  lane: number;         // 0-based stacking lane
  clippedLeft: boolean; // bar continues before this week
  clippedRight: boolean;
  label: string;
  colorClass: string;
}

interface PlanRow {
  employee: Employee;
  bars: PlanBar[];
  laneCount: number;
}

/**
 * /admin/planner — Plánovač (Planner flag): week grid of employees × days
 * with multi-day bars for pracovisko assignments and absences. Click an
 * empty cell to plan, click a bar to edit.
 */
@Component({
  selector: 'app-planner',
  standalone: true,
  imports: [CommonModule, FormsModule, NavbarComponent, SpinnerComponent, AlertComponent],
  templateUrl: './planner.page.html',
  host: {
    '(document:pointermove)': 'onDocPointerMove($event)',
    '(document:pointerup)': 'onDragEnd()',
    '(document:pointercancel)': 'onDragEnd()'
  }
})
export class PlannerPage implements OnInit {
  private svc = inject(PlannerService);
  private empSvc = inject(EmployeeService);
  private locSvc = inject(LocationService);

  /** Absence types + colors; praca bars are colored per pracovisko. */
  static readonly ABSENCES: Record<string, { label: string; color: string }> = {
    dovolenka: { label: 'Dovolenka', color: 'bg-yellow-200 dark:bg-yellow-800/70 text-yellow-900 dark:text-yellow-100 border-yellow-400' },
    pn:        { label: 'PN',        color: 'bg-red-200 dark:bg-red-900/70 text-red-900 dark:text-red-100 border-red-400' },
    volno:     { label: 'Voľno',     color: 'bg-slate-200 dark:bg-slate-600 text-slate-700 dark:text-slate-200 border-slate-400' }
  };

  /** Per-pracovisko palette — deterministic by location id. */
  private static readonly LOC_COLORS = [
    'bg-sky-200 dark:bg-sky-800/70 text-sky-900 dark:text-sky-100 border-sky-400',
    'bg-emerald-200 dark:bg-emerald-800/70 text-emerald-900 dark:text-emerald-100 border-emerald-400',
    'bg-orange-200 dark:bg-orange-800/70 text-orange-900 dark:text-orange-100 border-orange-400',
    'bg-violet-200 dark:bg-violet-800/70 text-violet-900 dark:text-violet-100 border-violet-400',
    'bg-pink-200 dark:bg-pink-800/70 text-pink-900 dark:text-pink-100 border-pink-400',
    'bg-teal-200 dark:bg-teal-800/70 text-teal-900 dark:text-teal-100 border-teal-400',
    'bg-lime-200 dark:bg-lime-800/70 text-lime-900 dark:text-lime-100 border-lime-400',
    'bg-cyan-200 dark:bg-cyan-800/70 text-cyan-900 dark:text-cyan-100 border-cyan-400'
  ];

  /** Monday of the shown week (YYYY-MM-DD). */
  weekStart = signal(this.mondayOf(new Date()));
  entries = signal<PlanEntry[]>([]);
  employees = signal<Employee[]>([]);
  locations = signal<Location[]>([]);
  loading = signal(false);
  error = signal<string | null>(null);

  weekDays = computed(() => {
    const [y, m, d] = this.weekStart().split('-').map(Number);
    return Array.from({ length: 7 }, (_, i) => {
      const date = new Date(y, m - 1, d + i);
      return {
        iso: this.isoDate(date),
        dayNum: date.getDate(),
        label: ['Po', 'Ut', 'St', 'Št', 'Pi', 'So', 'Ne'][i],
        isToday: this.isoDate(date) === this.isoDate(new Date()),
        isWeekend: i >= 5
      };
    });
  });

  weekLabel = computed(() => {
    const days = this.weekDays();
    return `${this.formatDay(days[0].iso)} – ${this.formatDay(days[6].iso)}`;
  });

  /** Grid rows: bars clipped to the week + greedy lane assignment per employee. */
  rows = computed<PlanRow[]>(() => {
    const weekFrom = this.weekDays()[0].iso;
    const weekTo = this.weekDays()[6].iso;
    return this.employees().map(emp => {
      const bars: PlanBar[] = [];
      const laneEnds: string[] = [];   // per lane: last occupied end date
      const mine = this.entries()
        .filter(e => e.employeeId === emp.id)
        .sort((a, b) => a.startDate.localeCompare(b.startDate));
      for (const e of mine) {
        const s = e.startDate.slice(0, 10);
        const t = e.endDate.slice(0, 10);
        if (t < weekFrom || s > weekTo) continue;
        const cs = s < weekFrom ? weekFrom : s;
        const ct = t > weekTo ? weekTo : t;
        let lane = laneEnds.findIndex(end => end < cs);
        if (lane === -1) { lane = laneEnds.length; laneEnds.push(ct); }
        else laneEnds[lane] = ct;
        bars.push({
          entry: e,
          colStart: this.dayIndex(cs) + 1,
          colEnd: this.dayIndex(ct) + 2,
          lane,
          clippedLeft: s < weekFrom,
          clippedRight: t > weekTo,
          label: e.type === 'praca'
            ? (e.locationName ?? 'Bez pracoviska')
            : PlannerPage.ABSENCES[e.type]?.label ?? e.type,
          colorClass: e.type === 'praca'
            ? PlannerPage.LOC_COLORS[(e.locationId ?? 0) % PlannerPage.LOC_COLORS.length]
            : PlannerPage.ABSENCES[e.type]?.color ?? ''
        });
      }
      return { employee: emp, bars, laneCount: Math.max(1, laneEnds.length) };
    });
  });

  // ─── Editor modal ────────────────────────────────────────────────
  editorOpen = signal(false);
  editingId = signal<number | null>(null);
  formEmployeeId = 0;
  formType = 'praca';
  formLocationId: number | null = null;
  formStart = '';
  formEnd = '';
  formNote = '';
  saving = signal(false);
  editorError = signal<string | null>(null);

  ngOnInit() {
    this.empSvc.getAll().subscribe(list => this.employees.set(list.filter(e => e.isActive)));
    this.locSvc.getAll().subscribe(list => this.locations.set(list.filter(l => l.isActive)));
    this.load();
  }

  async load() {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.entries.set(await this.svc.list(this.weekDays()[0].iso, this.weekDays()[6].iso));
    } catch (e: any) {
      this.error.set(this.errMsg(e));
    } finally {
      this.loading.set(false);
    }
  }

  shiftWeek(delta: -1 | 1) {
    const [y, m, d] = this.weekStart().split('-').map(Number);
    this.weekStart.set(this.isoDate(new Date(y, m - 1, d + delta * 7)));
    this.load();
  }

  goToday() {
    this.weekStart.set(this.mondayOf(new Date()));
    this.load();
  }

  // ─── Drag-select across days (mouse) ─────────────────────────────
  // pointerdown on a cell starts the selection; document pointermove
  // extends it via elementFromPoint hit-testing (immune to enter/leave
  // quirks); document pointerup opens the modal with the range. Mouse
  // only — on touch a drag must keep scrolling the page, so tablets tap
  // a cell and set the range in the modal instead. A plain click
  // (down+up on one cell) is handled by the cell's (click).
  dragEmployeeId = signal<number | null>(null);
  dragStart = signal('');
  dragEnd = signal('');

  onCellPointerDown(employeeId: number, dayIso: string, event: PointerEvent) {
    if (event.pointerType !== 'mouse' || event.button !== 0) return;
    event.preventDefault();   // no text selection / focus ring while dragging
    this.dragEmployeeId.set(employeeId);
    this.dragStart.set(dayIso);
    this.dragEnd.set(dayIso);
  }

  onDocPointerMove(event: PointerEvent) {
    const emp = this.dragEmployeeId();
    if (emp == null) return;
    const el = document.elementFromPoint(event.clientX, event.clientY);
    const cell = el?.closest?.('[data-plan-day]') as HTMLElement | null;
    if (cell && Number(cell.dataset['planEmp']) === emp)
      this.dragEnd.set(cell.dataset['planDay']!);
  }

  onDragEnd() {
    const emp = this.dragEmployeeId();
    if (emp == null) return;
    const [from, to] = [this.dragStart(), this.dragEnd()].sort();
    this.dragEmployeeId.set(null);
    // Single-cell down+up = the cell's own (click) opens the modal.
    if (from !== to) this.openRange(emp, from, to);
  }

  cellInDrag(employeeId: number, dayIso: string): boolean {
    if (this.dragEmployeeId() !== employeeId) return false;
    const [from, to] = [this.dragStart(), this.dragEnd()].sort();
    return dayIso >= from && dayIso <= to;
  }

  private openRange(employeeId: number, fromIso: string, toIso: string) {
    this.editingId.set(null);
    this.formEmployeeId = employeeId;
    this.formType = 'praca';
    this.formLocationId = null;
    this.formStart = fromIso;
    this.formEnd = toIso;
    this.formNote = '';
    this.editorError.set(null);
    this.editorOpen.set(true);
  }

  openCreate(employeeId: number, dayIso: string) {
    this.openRange(employeeId, dayIso, dayIso);
  }

  openEdit(bar: PlanBar, event: Event) {
    event.stopPropagation();
    const e = bar.entry;
    this.editingId.set(e.id);
    this.formEmployeeId = e.employeeId;
    this.formType = e.type;
    this.formLocationId = e.locationId;
    this.formStart = e.startDate.slice(0, 10);
    this.formEnd = e.endDate.slice(0, 10);
    this.formNote = e.note ?? '';
    this.editorError.set(null);
    this.editorOpen.set(true);
  }

  closeEditor() { this.editorOpen.set(false); }

  employeeName(id: number): string {
    const e = this.employees().find(x => x.id === id);
    return e ? `${e.firstName} ${e.lastName}` : '';
  }

  async save() {
    if (this.formType === 'praca' && !this.formLocationId) {
      this.editorError.set('Vyberte pracovisko.');
      return;
    }
    if (!this.formStart || !this.formEnd || this.formStart > this.formEnd) {
      this.editorError.set('Neplatný rozsah dátumov.');
      return;
    }
    const dto: SavePlanEntry = {
      employeeId: this.formEmployeeId,
      type: this.formType,
      locationId: this.formType === 'praca' ? this.formLocationId : null,
      startDate: this.formStart,
      endDate: this.formEnd,
      note: this.formNote.trim() || null
    };
    this.saving.set(true);
    this.editorError.set(null);
    try {
      const id = this.editingId();
      if (id != null) await this.svc.update(id, dto);
      else await this.svc.create(dto);
      this.editorOpen.set(false);
      await this.load();
    } catch (e: any) {
      this.editorError.set(this.errMsg(e));
    } finally {
      this.saving.set(false);
    }
  }

  async remove() {
    const id = this.editingId();
    if (id == null) return;
    if (!confirm('Zmazať tento záznam z plánu?')) return;
    this.saving.set(true);
    try {
      await this.svc.delete(id);
      this.editorOpen.set(false);
      await this.load();
    } catch (e: any) {
      this.editorError.set(this.errMsg(e));
    } finally {
      this.saving.set(false);
    }
  }

  // ─── Helpers ─────────────────────────────────────────────────────
  /** 0-based index of an ISO day within the shown week. */
  private dayIndex(iso: string): number {
    const [y1, m1, d1] = this.weekStart().split('-').map(Number);
    const [y2, m2, d2] = iso.split('-').map(Number);
    return Math.round((new Date(y2, m2 - 1, d2).getTime() - new Date(y1, m1 - 1, d1).getTime()) / 86_400_000);
  }

  private mondayOf(d: Date): string {
    return this.isoDate(new Date(d.getFullYear(), d.getMonth(), d.getDate() - ((d.getDay() + 6) % 7)));
  }

  private isoDate(d: Date): string {
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }

  private formatDay(iso: string): string {
    const [y, m, d] = iso.split('-').map(Number);
    return `${String(d).padStart(2, '0')}.${String(m).padStart(2, '0')}.${y}`;
  }

  private errMsg(e: any): string {
    return typeof e?.error === 'string' ? e.error :
           typeof e?.error?.error === 'string' ? e.error.error :
           e?.message ?? 'Neznáma chyba.';
  }
}
