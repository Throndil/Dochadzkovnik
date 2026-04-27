import { Component, computed, effect, EventEmitter, HostListener, Input, OnDestroy, Output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  MaterialService,
  Material,
  MaterialUsage,
  MaterialSummaryRow,
  CreateMaterial,
  CreateMaterialUsage,
  UpdateMaterialUsage
} from '../../services/material.service';
import { Location as Pracovisko } from '../../services/location.service';

/**
 * Slide-over right-hand panel for managing material consumption on a single Lokácia.
 * The Lokácie page renders this once and toggles `[location]` to open/close.
 */
@Component({
  selector: 'app-location-manage-panel',
  imports: [FormsModule],
  templateUrl: './location-manage-panel.component.html'
})
export class LocationManagePanelComponent implements OnDestroy {
  @Input() set location(loc: Pracovisko | null) {
    this._location.set(loc);
    if (loc) {
      // Snap the date filter to the current month every time the panel reopens.
      // The effect() below will pick up the new range + location and reload data.
      this.snapToCurrentMonth();
      // Lock background scroll while open (mobile-friendly)
      if (typeof document !== 'undefined') document.body.classList.add('overflow-hidden');
    } else {
      // Reset transient UI state so the next open is clean
      this.showAddForm.set(false);
      this.editingId.set(null);
      this.errorMsg.set('');
      if (typeof document !== 'undefined') document.body.classList.remove('overflow-hidden');
    }
  }
  get location(): Pracovisko | null { return this._location(); }

  private _location = signal<Pracovisko | null>(null);
  isOpen = computed(() => this._location() !== null);

  /** Emitted when the panel should close (backdrop click, Esc, ✕). The parent owns the open/close state. */
  @Output() close = new EventEmitter<void>();

  catalogue   = signal<Material[]>([]);
  summary     = signal<MaterialSummaryRow[]>([]);
  entries     = signal<MaterialUsage[]>([]);
  loading     = signal(false);
  showAddForm = signal(false);
  errorMsg    = signal('');

  // Filter range — defaults to current calendar month
  from = signal<string>('');
  to   = signal<string>('');
  rangeMode = signal<'month' | 'custom'>('month');

  // Add form
  newEntry: CreateMaterialUsage = { materialId: 0, quantity: 1, date: this.todayString(), note: '' };
  quickQuantities = [1, 5, 10, 20, 50];

  // Inline "create new catalogue item" form (no need to leave the panel)
  showAddMaterial = signal(false);
  newMaterial: CreateMaterial = { name: '', unit: '', pricePerUnit: 0 };
  unitPresets = ['vrece', 'kg', 'l', 'm²', 'm³', 'ks', 'bm'];

  // Edit state — usageId currently being edited
  editingId = signal<number | null>(null);
  editForm: UpdateMaterialUsage = { materialId: 0, quantity: 0, date: '', note: '' };

  constructor(private materialSvc: MaterialService) {
    // Whenever the date range changes (and we have a location), reload data.
    effect(() => {
      const loc = this._location();
      const f = this.from();
      const t = this.to();
      if (loc) this.load(loc.id, f, t);
    });
  }

  // Headline numbers shown in the sticky footer (top 2 by total quantity, regardless of unit)
  headline = computed(() => this.summary().slice(0, 2));

  totalEntries = computed(() => this.entries().length);

  /** Grand total cost across all summary rows (EUR), shown in the sticky footer + summary table. */
  grandTotal = computed(() => this.summary().reduce((acc, r) => acc + (r.totalCost || 0), 0));

  // ── Lifecycle / dismissal ──────────────────────────────────────
  ngOnDestroy() {
    if (typeof document !== 'undefined') document.body.classList.remove('overflow-hidden');
  }

  @HostListener('document:keydown.escape')
  onEsc() { if (this.isOpen()) this.requestClose(); }

  // Tell the parent we want to close; the parent will null out its [location] binding,
  // which will run our setter again with null and clean up internal state.
  requestClose() {
    this.showAddForm.set(false);
    this.editingId.set(null);
    this.errorMsg.set('');
    this.close.emit();
  }

  // ── Data loading ───────────────────────────────────────────────
  private load(locationId?: number, from?: string, to?: string) {
    const id = locationId ?? this._location()?.id;
    if (!id) return;
    this.loading.set(true);
    this.errorMsg.set('');

    const f = from ?? this.from();
    const t = to   ?? this.to();

    this.materialSvc.getCatalogue(true).subscribe({
      next: cat => {
        this.catalogue.set(cat);
        if (!this.newEntry.materialId && cat.length) this.newEntry.materialId = cat[0].id;
      },
      error: () => this.errorMsg.set('Nepodarilo sa načítať katalóg materiálu.')
    });

    this.materialSvc.getSummary(id, f, t).subscribe({
      next: s => this.summary.set(s),
      error: () => this.errorMsg.set('Nepodarilo sa načítať súhrn.')
    });

    this.materialSvc.getUsages(id, f, t).subscribe({
      next: e => { this.entries.set(e); this.loading.set(false); },
      error: () => { this.errorMsg.set('Nepodarilo sa načítať záznamy.'); this.loading.set(false); }
    });
  }

  // ── Date range helpers ─────────────────────────────────────────
  todayString(): string {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }

  snapToCurrentMonth() {
    const now = new Date();
    const start = new Date(now.getFullYear(), now.getMonth(), 1);
    const end   = new Date(now.getFullYear(), now.getMonth() + 1, 0);
    this.from.set(this.fmtDate(start));
    this.to.set(this.fmtDate(end));
    this.rangeMode.set('month');
  }
  fmtDate(d: Date) {
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }

  onDateInputChange() { this.rangeMode.set('custom'); }

  // ── Add / edit / delete ───────────────────────────────────────
  /**
   * Default date for a new entry = a sensible date inside the active filter range.
   * - If today falls inside [from..to] → use today (most common case: viewing current month).
   * - Otherwise → use the LAST day of the filtered range. This is what the customer wants:
   *   when they're filtering by "last month" and click + Pridať, the default date becomes the
   *   last day of last month, not today.
   */
  private defaultEntryDate(): string {
    const f = this.from();
    const t = this.to();
    const today = this.todayString();
    if (!f || !t) return today;
    if (today >= f && today <= t) return today;
    return t; // last day of the filtered range (string compare on YYYY-MM-DD is safe)
  }

  toggleAdd() {
    const opening = !this.showAddForm();
    this.showAddForm.set(opening);
    if (opening) {
      // Re-default the date every time the form opens so a stale value from a previous
      // session doesn't stick around when the filter range has changed.
      this.newEntry.date = this.defaultEntryDate();
      if (!this.newEntry.materialId && this.catalogue().length) {
        this.newEntry.materialId = this.catalogue()[0].id;
      }
    } else {
      // Closing also collapses the inline catalogue-add UI
      this.showAddMaterial.set(false);
    }
  }

  adjustNewQty(delta: number) {
    const next = Math.max(0.001, +(this.newEntry.quantity + delta).toFixed(3));
    this.newEntry.quantity = next;
  }
  setNewQty(q: number) { this.newEntry.quantity = q; }

  onSaveNew() {
    const loc = this._location();
    if (!loc) return;
    if (!this.newEntry.materialId || this.newEntry.quantity <= 0) {
      this.errorMsg.set('Vyberte materiál a zadajte množstvo.');
      return;
    }
    this.materialSvc.createUsage(loc.id, { ...this.newEntry }).subscribe({
      next: () => {
        this.showAddForm.set(false);
        // Keep the same materialId pre-selected so a worker logging multiple entries doesn't
        // have to reselect it each time. Reset quantity and re-default the date.
        this.newEntry = {
          materialId: this.newEntry.materialId,
          quantity: 1,
          date: this.defaultEntryDate(),
          note: ''
        };
        this.load();
      },
      error: e => this.errorMsg.set(e?.error ?? 'Uloženie zlyhalo.')
    });
  }

  // ── Inline "Pridať nový materiál" (catalogue) ─────────────────
  toggleAddMaterial() {
    this.showAddMaterial.update(v => !v);
    if (!this.showAddMaterial()) this.newMaterial = { name: '', unit: '', pricePerUnit: 0 };
  }
  pickUnitForNew(u: string) { this.newMaterial.unit = u; }
  onSaveNewMaterial() {
    if (!this.newMaterial.name.trim() || !this.newMaterial.unit.trim()) {
      this.errorMsg.set('Vyplňte názov aj jednotku nového materiálu.');
      return;
    }
    if (this.newMaterial.pricePerUnit < 0) {
      this.errorMsg.set('Cena nesmie byť záporná.');
      return;
    }
    this.materialSvc.createMaterial({ ...this.newMaterial }).subscribe({
      next: created => {
        this.showAddMaterial.set(false);
        this.newMaterial = { name: '', unit: '', pricePerUnit: 0 };
        // Refresh catalogue and pre-select the newly created material in the entry form
        this.materialSvc.getCatalogue(true).subscribe({
          next: cat => {
            this.catalogue.set(cat);
            this.newEntry.materialId = created.id;
          }
        });
      },
      error: e => this.errorMsg.set(typeof e?.error === 'string' ? e.error : 'Vytvorenie materiálu zlyhalo.')
    });
  }

  startEdit(u: MaterialUsage) {
    this.editingId.set(u.id);
    this.editForm = {
      materialId: u.materialId,
      quantity: u.quantity,
      date: u.date.substring(0, 10),
      employeeId: u.employeeId,
      note: u.note ?? ''
    };
  }
  cancelEdit() { this.editingId.set(null); }
  saveEdit() {
    const loc = this._location();
    const id = this.editingId();
    if (!loc || !id) return;
    this.materialSvc.updateUsage(loc.id, id, this.editForm).subscribe({
      next: () => { this.editingId.set(null); this.load(); },
      error: e => this.errorMsg.set(e?.error ?? 'Úprava zlyhala.')
    });
  }

  onDelete(u: MaterialUsage) {
    const loc = this._location();
    if (!loc) return;
    if (!confirm(`Odstrániť záznam "${u.materialName} – ${u.quantity} ${u.unit}" z ${this.formatDate(u.date)}?`)) return;
    this.materialSvc.deleteUsage(loc.id, u.id).subscribe({ next: () => this.load() });
  }

  // ── Excel export ───────────────────────────────────────────────
  onDownloadExcel() {
    const loc = this._location();
    if (!loc) return;
    this.materialSvc.downloadExcel(loc.id, loc.name, this.from(), this.to());
  }

  // ── Formatting helpers ─────────────────────────────────────────
  formatDate(iso: string): string {
    const d = new Date(iso);
    if (isNaN(d.getTime())) return iso;
    return `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')}.${d.getFullYear()}`;
  }

  // Friendly relative label ("dnes", "včera", "pred 3 dňami")
  relativeDay(iso: string): string {
    const d = new Date(iso);
    if (isNaN(d.getTime())) return '';
    const today = new Date(); today.setHours(0,0,0,0);
    const target = new Date(d.getFullYear(), d.getMonth(), d.getDate());
    const diff = Math.round((today.getTime() - target.getTime()) / (1000 * 60 * 60 * 24));
    if (diff === 0) return 'dnes';
    if (diff === 1) return 'včera';
    if (diff > 1 && diff < 7) return `pred ${diff} dňami`;
    return this.formatDate(iso);
  }

  formatQty(n: number): string {
    // Up to 2 decimals, trailing zeros stripped: 5 → "5", 19.501 → "19,5", 19.55 → "19,55"
    return n.toLocaleString('sk-SK', { maximumFractionDigits: 2 });
  }

  /** Slovak EUR — two decimals, used for prices, line costs, and grand totals in the panel. */
  formatEur(n: number | null | undefined): string {
    return new Intl.NumberFormat('sk-SK', { style: 'currency', currency: 'EUR', minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(n || 0);
  }

  // Lookup helper for the edit form's material dropdown
  unitFor(materialId: number): string {
    return this.catalogue().find(m => m.id === materialId)?.unit ?? '';
  }

  /** Catalogue price for the currently selected material in the add form (used for live cost preview). */
  selectedMaterialPrice(): number {
    return this.catalogue().find(m => m.id === this.newEntry.materialId)?.pricePerUnit ?? 0;
  }
}
