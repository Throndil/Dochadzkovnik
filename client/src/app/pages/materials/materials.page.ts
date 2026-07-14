import { Component, OnInit, signal, computed, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { AlertComponent } from '../../components/alert/alert.component';
import { EmptyStateComponent } from '../../components/empty-state/empty-state.component';
import { DatepickerDirective } from '../../directives/datepicker.directive';
import { MaterialService, Material, CreateMaterial, UpdateMaterial } from '../../services/material.service';
import {
  MaterialPurchaseService,
  MaterialPurchase,
  UnknownMaterialGroup,
  PromoteMaterialLine,
  PurchaseFilters,
  UpdateMaterialPurchase,
  UpdateMaterialPurchaseLine
} from '../../services/material-purchase.service';
import { LocationService, Location as Loc } from '../../services/location.service';
import { EmployeeService, Employee } from '../../services/employee.service';
import { FeatureFlagService } from '../../services/feature-flag.service';
import { AuthService } from '../../services/auth.service';
import { ApiErrorService } from '../../services/api-error.service';

type Tab = 'katalog' | 'nakupy' | 'inventar' | 'neid';

@Component({
  selector: 'app-materials',
  imports: [NavbarComponent, FormsModule, SpinnerComponent, AlertComponent, EmptyStateComponent, DatepickerDirective],
  templateUrl: './materials.page.html'
})
export class MaterialsPage implements OnInit {
  // ─── Tab state ───────────────────────────────────────────────────
  /** Current tab. Nákupy + Neidentifikované are gated by the MaterialPurchases
   *  feature flag (or superadmin); when off, only Katalóg is reachable. */
  tab = signal<Tab>('katalog');
  flags = inject(FeatureFlagService);
  auth  = inject(AuthService);

  /** True when the customer's environment has the new tabs enabled OR a superadmin
   *  is logged in for testing. Drives the tab bar visibility. */
  purchasesAvailable = computed(() => this.flags.materialPurchases() || this.auth.isSuperAdmin());

  // ─── Katalóg (unchanged from V1 — preserved verbatim) ────────────
  materials = signal<Material[]>([]);
  loading = signal(true);
  showForm  = signal(false);
  newMaterial: CreateMaterial = { name: '', unit: '', pricePerUnit: 0 };
  errorMsg = signal('');

  editingId = signal<number | null>(null);
  editForm: UpdateMaterial = { name: '', unit: '', pricePerUnit: 0, isActive: true };

  unitPresets = ['vrece', 'kg', 'l', 'm²', 'm³', 'ks', 'bm'];

  /** Katalóg display structure: ACTIVE materials grouped by jednotka
   *  (largest groups first, items alphabetical), inactive ones collapsed
   *  behind a counter toggle — the flat 100-row table was unusable. */
  showInactiveCatalog = signal(false);
  catalogGroups = computed(() => {
    const groups = new Map<string, Material[]>();
    for (const m of this.materials()) {
      if (!m.isActive) continue;
      const key = (m.unit || '—').trim().toLowerCase();
      (groups.get(key) ?? groups.set(key, []).get(key)!).push(m);
    }
    return [...groups.values()]
      .map(items => ({
        unit: (items[0].unit || '—').trim(),
        items: [...items].sort((a, b) => a.name.localeCompare(b.name, 'sk'))
      }))
      .sort((a, b) => b.items.length - a.items.length || a.unit.localeCompare(b.unit, 'sk'));
  });
  inactiveMaterials = computed(() =>
    this.materials()
      .filter(m => !m.isActive)
      .sort((a, b) => a.name.localeCompare(b.name, 'sk')));

  // ─── Nákupy ──────────────────────────────────────────────────────
  purchases = signal<MaterialPurchase[]>([]);
  purchasesLoading = signal(false);
  purchasesError   = signal('');
  /** Expanded purchase row id — only one open at a time to keep the table compact. */
  expandedPurchaseId = signal<number | null>(null);
  /** Busy flag for the Excel export buttons (shared by Nákupy + Inventár —
   *  only one is visible at a time, the header button is tab-specific). */
  exporting = signal(false);

  // Filters — defaults to current month so the page opens with a useful subset.
  filterFrom: string = '';
  filterTo: string   = '';
  filterLocationId: number | null = null;
  filterEmployeeId: number | null = null;
  filterSupplier = '';

  // Filter dropdown sources
  locations = signal<Loc[]>([]);
  employees = signal<Employee[]>([]);

  // ─── Inventár (Nákupy with no target Location) ──────────────────
  inventoryPurchases = signal<MaterialPurchase[]>([]);
  inventoryLoading   = signal(false);
  inventoryError     = signal('');
  expandedInventoryId = signal<number | null>(null);
  inventoryFilterFrom = '';
  inventoryFilterTo   = '';

  /** Per-material aggregate across the loaded inventory purchases — drives the
   *  small "Spolu v inventári" table at the top of the Inventár tab. */
  inventorySummary = computed(() => {
    const lines = this.inventoryPurchases().flatMap(p => p.lines.map(l => ({ p, l })));
    const groups = new Map<string, {
      name: string; unit: string; qty: number; cost: number; lastSeen: string;
    }>();
    for (const { p, l } of lines) {
      const name = (l.materialName ?? l.materialNameRaw ?? '').trim();
      const unit = (l.unit ?? '').trim();
      const key  = `${name.toLowerCase()}|${unit.toLowerCase()}`;
      const ex   = groups.get(key);
      if (ex) {
        ex.qty  += l.quantity;
        ex.cost += l.lineTotal;
        if (p.purchaseDate > ex.lastSeen) ex.lastSeen = p.purchaseDate;
      } else {
        groups.set(key, { name, unit, qty: l.quantity, cost: l.lineTotal, lastSeen: p.purchaseDate });
      }
    }
    return Array.from(groups.values()).sort((a, b) => b.cost - a.cost);
  });

  inventoryGrandTotal = computed(() =>
    this.inventoryPurchases().reduce((sum, p) => sum + p.totalCost, 0));

  // ─── Neidentifikované ────────────────────────────────────────────
  unknownGroups = signal<UnknownMaterialGroup[]>([]);
  unknownLoading = signal(false);
  unknownError   = signal('');

  // ─── Edit purchase modal ─────────────────────────────────────────
  /** When non-null, the edit modal renders for this purchase. */
  editPurchaseTarget = signal<MaterialPurchase | null>(null);
  editPurchaseDate = '';
  editPurchaseLocationId: number | null = null;
  editPurchaseSupplier = '';
  editPurchaseNote = '';
  editPurchaseLines = signal<UpdateMaterialPurchaseLine[]>([]);
  editPurchaseSaving = signal(false);
  editPurchaseError = signal('');
  editReceiptFile = signal<File | null>(null);
  editReceiptPreview = signal<string | null>(null);
  editReceiptUploading = signal(false);

  // ─── Add-line picker (inside edit modal) ─────────────────────────
  /** When true, the inline add-line picker is open inside the edit modal. */
  editAddingLine = signal(false);
  editPickerSearch = '';
  editNewMaterialOpen = signal(false);
  editNewMaterialName = '';
  editNewMaterialUnit = '';
  editNewMaterialPrice: number = 0;

  filteredEditCatalogue = computed(() => {
    const q = this.editPickerSearch.trim().toLowerCase();
    const all = this.materials();
    if (!q) return all.slice(0, 50);
    return all.filter(m => m.name.toLowerCase().includes(q));
  });

  /** Promote dialog state. When non-null, the modal renders for that group. */
  promoteTarget = signal<UnknownMaterialGroup | null>(null);
  promoteMode = signal<'new' | 'merge'>('new');
  promoteName = '';
  promoteUnit = '';
  promotePrice: number = 0;
  promoteApplyAll = true;
  promoteCatalogueId: number | null = null;
  promoteCatalogueSearch = '';
  promoteSaving = signal(false);
  promoteError = signal('');
  promoteResultMessage = signal('');

  filteredPromoteCatalogue = computed(() => {
    const q = this.promoteCatalogueSearch.trim().toLowerCase();
    const all = this.materials();
    if (!q) return all.slice(0, 50);
    return all.filter(m => m.name.toLowerCase().includes(q));
  });

  private materialSvc = inject(MaterialService);
  private mpService   = inject(MaterialPurchaseService);
  private locationSvc = inject(LocationService);
  private employeeSvc = inject(EmployeeService);
  private apiError    = inject(ApiErrorService);

  ngOnInit() {
    // Default filter range = current calendar month (mirrors the location panel).
    const now = new Date();
    this.filterFrom = this.fmtIso(new Date(now.getFullYear(), now.getMonth(), 1));
    this.filterTo   = this.fmtIso(new Date(now.getFullYear(), now.getMonth() + 1, 0));

    // Inventár filters default to current month, matching Nákupy.
    this.inventoryFilterFrom = this.filterFrom;
    this.inventoryFilterTo   = this.filterTo;

    this.load();

    // Eagerly load filter dropdown sources only when the new tabs are reachable.
    if (this.purchasesAvailable()) {
      this.locationSvc.getAll().subscribe({ next: ls => this.locations.set(ls) });
      this.employeeSvc.getAll().subscribe({ next: es => this.employees.set(es) });
    }
  }

  // ─── Tab switching ───────────────────────────────────────────────

  setTab(t: Tab) {
    this.tab.set(t);
    if (t === 'nakupy'   && this.purchases().length === 0)        this.loadPurchases();
    if (t === 'inventar' && this.inventoryPurchases().length === 0) this.loadInventory();
    if (t === 'neid'     && this.unknownGroups().length === 0)    this.loadUnknownGroups();
  }

  // ─── Katalóg ─────────────────────────────────────────────────────

  load() {
    this.loading.set(true);
    this.materialSvc.getCatalogue().subscribe({
      next: ms => { this.materials.set(ms); this.loading.set(false); },
      error: e => { this.errorMsg.set(this.apiError.friendly(e, 'Načítanie materiálov zlyhalo')); this.loading.set(false); }
    });
  }

  toggleForm() {
    this.showForm.update(v => !v);
    if (!this.showForm()) this.newMaterial = { name: '', unit: '', pricePerUnit: 0 };
    this.errorMsg.set('');
  }

  onCreate() {
    if (!this.newMaterial.name.trim() || !this.newMaterial.unit.trim()) {
      this.errorMsg.set('Vyplňte názov aj jednotku.');
      return;
    }
    if (this.newMaterial.pricePerUnit == null || this.newMaterial.pricePerUnit < 0) {
      this.errorMsg.set('Cena nesmie byť záporná.');
      return;
    }
    this.materialSvc.createMaterial(this.newMaterial).subscribe({
      next: () => { this.toggleForm(); this.load(); },
      error: e => this.errorMsg.set(this.apiError.friendly(e, 'Vytvorenie materiálu zlyhalo'))
    });
  }

  startEdit(m: Material) {
    this.editingId.set(m.id);
    this.editForm = { name: m.name, unit: m.unit, pricePerUnit: m.pricePerUnit, isActive: m.isActive };
  }
  cancelEdit() { this.editingId.set(null); }
  saveEdit() {
    const id = this.editingId();
    if (!id) return;
    if (this.editForm.pricePerUnit == null || this.editForm.pricePerUnit < 0) {
      this.errorMsg.set('Cena nesmie byť záporná.');
      return;
    }
    this.materialSvc.updateMaterial(id, this.editForm).subscribe({
      next: () => { this.editingId.set(null); this.load(); },
      error: e => this.errorMsg.set(this.apiError.friendly(e, 'Úprava materiálu zlyhala'))
    });
  }

  onToggleActive(m: Material) {
    this.materialSvc.toggleMaterialActive(m.id).subscribe({ next: () => this.load() });
  }

  onDelete(m: Material) {
    if (!confirm(`Odstrániť materiál "${m.name}"? Ak bol už použitý, bude iba deaktivovaný.`)) return;
    this.materialSvc.deleteMaterial(m.id).subscribe({
      next: res => { if (res?.message) alert(res.message); this.load(); },
      error: e => this.errorMsg.set(this.apiError.friendly(e, 'Odstránenie materiálu zlyhalo'))
    });
  }

  pickUnit(u: string) { this.newMaterial.unit = u; }

  // ─── Nákupy ──────────────────────────────────────────────────────

  private buildFilters(): PurchaseFilters {
    return {
      from: this.filterFrom || undefined,
      to:   this.filterTo   || undefined,
      locationId: this.filterLocationId ?? undefined,
      employeeId: this.filterEmployeeId ?? undefined,
      supplier:   this.filterSupplier.trim() || undefined
    };
  }

  loadPurchases() {
    this.purchasesError.set('');
    this.purchasesLoading.set(true);
    this.mpService.list(this.buildFilters()).subscribe({
      next: ps => { this.purchases.set(ps); this.purchasesLoading.set(false); },
      error: e => { this.purchasesError.set(this.apiError.friendly(e, 'Načítanie nákupov zlyhalo')); this.purchasesLoading.set(false); }
    });
  }

  applyFilters() { this.loadPurchases(); }

  resetFilters() {
    this.filterLocationId = null;
    this.filterEmployeeId = null;
    this.filterSupplier = '';
    const now = new Date();
    this.filterFrom = this.fmtIso(new Date(now.getFullYear(), now.getMonth(), 1));
    this.filterTo   = this.fmtIso(new Date(now.getFullYear(), now.getMonth() + 1, 0));
    this.loadPurchases();
  }

  togglePurchaseRow(id: number) {
    this.expandedPurchaseId.update(open => open === id ? null : id);
  }

  /** Cross-purchase grand total. Computed from the loaded list (already filtered). */
  filteredGrandTotal = computed(() =>
    this.purchases().reduce((sum, p) => sum + p.totalCost, 0));

  exportPurchasesExcel() {
    this.exporting.set(true);
    this.mpService.downloadExcel(this.buildFilters());
    // downloadExcel is fire-and-forget (no completion callback on the download
    // helper), so the busy state is reset via a bounded timeout instead.
    setTimeout(() => this.exporting.set(false), 4000);
  }

  /** Hard delete a purchase from the admin Nákupy tab. Receipt photo cleanup is handled server-side. */
  onDeletePurchase(p: MaterialPurchase) {
    if (!confirm(`Vymazať nákup z ${this.fmtDate(p.purchaseDate)} (${this.formatEur(p.totalCost)})?`)) return;
    this.mpService.delete(p.id).subscribe({
      next: () => this.loadPurchases(),
      error: e => this.purchasesError.set(this.apiError.friendly(e, 'Vymazanie nákupu zlyhalo'))
    });
  }

  // ─── Edit purchase ───────────────────────────────────────────────

  startEditPurchase(p: MaterialPurchase) {
    this.editPurchaseTarget.set(p);
    // Use local date components, NOT a UTC slice — purchaseDate is an ISO
    // timestamp and slicing the first 10 chars gives the UTC calendar date,
    // which can be one day off from the user-visible local date (the display
    // uses getDate() etc. in the local timezone). If a purchase is stored as
    // 2026-05-05T22:30:00Z, Bratislava local is 06.05 and the display shows
    // "06.05.2026" — but a UTC slice would prefill the input as 05.05.2026.
    const d = new Date(p.purchaseDate);
    const yyyy = d.getFullYear();
    const mm   = String(d.getMonth() + 1).padStart(2, '0');
    const dd   = String(d.getDate()).padStart(2, '0');
    this.editPurchaseDate = `${yyyy}-${mm}-${dd}`;
    this.editPurchaseLocationId = p.locationId;
    this.editPurchaseSupplier = p.supplierName ?? '';
    this.editPurchaseNote = p.note ?? '';
    this.editPurchaseLines.set(p.lines.map(l => ({
      id: l.id,
      materialId: l.materialId,
      materialNameRaw: l.materialNameRaw,
      unit: l.unit,
      quantity: l.quantity,
      unitPrice: l.unitPrice
    })));
    this.editReceiptFile.set(null);
    this.editReceiptPreview.set(null);
    this.editPurchaseError.set('');
  }

  cancelEditPurchase() {
    this.editPurchaseTarget.set(null);
    this.editPurchaseSaving.set(false);
    this.editPurchaseError.set('');
    this.editReceiptFile.set(null);
    this.editReceiptPreview.set(null);
  }

  setEditLineQty(idx: number, qty: number) {
    if (qty < 0) qty = 0;
    this.editPurchaseLines.update(arr =>
      arr.map((l, i) => i === idx ? { ...l, quantity: qty } : l));
  }

  setEditLineUnitPrice(idx: number, price: number) {
    if (price < 0) price = 0;
    this.editPurchaseLines.update(arr =>
      arr.map((l, i) => i === idx ? { ...l, unitPrice: price } : l));
  }

  removeEditLine(idx: number) {
    this.editPurchaseLines.update(arr => arr.filter((_, i) => i !== idx));
  }

  // ─── Add line inside the edit modal ──────────────────────────────

  openEditAddLine() {
    this.editAddingLine.set(true);
    this.editPickerSearch = '';
    this.editNewMaterialOpen.set(false);
    this.editNewMaterialName = '';
    this.editNewMaterialUnit = '';
    this.editNewMaterialPrice = 0;
  }

  cancelEditAddLine() {
    this.editAddingLine.set(false);
    this.editNewMaterialOpen.set(false);
  }

  /** Add an existing catalogue material as a fresh line on the purchase being edited. */
  addEditCatalogueLine(m: { id: number; name: string; unit: string; pricePerUnit: number }) {
    this.editPurchaseLines.update(arr => [...arr, {
      id: null,                               // null → server inserts as new
      materialId: m.id,
      materialNameRaw: m.name,
      unit: m.unit,
      quantity: 1,
      unitPrice: m.pricePerUnit
    }]);
    this.editAddingLine.set(false);
  }

  /** Add a free-typed material line. Server-side stays MaterialId=null until promoted. */
  addEditCustomLine() {
    const name = this.editNewMaterialName.trim();
    const unit = this.editNewMaterialUnit.trim();
    if (!name || !unit) return;
    this.editPurchaseLines.update(arr => [...arr, {
      id: null,
      materialId: null,
      materialNameRaw: name,
      unit,
      quantity: 1,
      unitPrice: this.editNewMaterialPrice || 0
    }]);
    this.editAddingLine.set(false);
    this.editNewMaterialOpen.set(false);
    this.editNewMaterialName = '';
    this.editNewMaterialUnit = '';
    this.editNewMaterialPrice = 0;
  }

  /** Live grand total of the edit working set — drives the modal's footer. */
  editTotalCost(): number {
    return this.editPurchaseLines().reduce((sum, l) => sum + (l.quantity * l.unitPrice), 0);
  }

  /** Receipt file picked in edit mode. Just stages it in memory; uploaded on save. */
  async onEditReceiptSelected(ev: Event) {
    const input = ev.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    this.editReceiptFile.set(file);
    this.editReceiptPreview.set(URL.createObjectURL(file));
    input.value = '';
  }

  removeEditReceipt() {
    const url = this.editReceiptPreview();
    if (url && url.startsWith('blob:')) URL.revokeObjectURL(url);
    this.editReceiptFile.set(null);
    this.editReceiptPreview.set(null);
  }

  /** Delete the saved receipt (server-side). Stages no-op on the file picker. */
  deleteSavedReceipt() {
    const target = this.editPurchaseTarget();
    if (!target?.receiptPhotoUrl) return;
    if (!confirm('Vymazať aktuálnu účtenku?')) return;
    this.mpService.deleteReceipt(target.id).subscribe({
      next: () => {
        // Clone the target signal value to drop the photo URL — modal mirrors
        // the server state without a full reload.
        this.editPurchaseTarget.set({ ...target, receiptPhotoUrl: null });
        this.purchases.set([]); // invalidate Nákupy cache
      },
      error: e => this.editPurchaseError.set(this.apiError.friendly(e, 'Vymazanie účtenky zlyhalo'))
    });
  }

  savePurchaseEdit() {
    const target = this.editPurchaseTarget();
    if (!target) return;
    if (this.editPurchaseLines().length === 0) {
      this.editPurchaseError.set('Pridaj aspoň jednu položku.');
      return;
    }
    this.editPurchaseError.set('');
    this.editPurchaseSaving.set(true);

    const dto: UpdateMaterialPurchase = {
      locationId: this.editPurchaseLocationId,
      supplierName: this.editPurchaseSupplier.trim() || null,
      note: this.editPurchaseNote.trim() || null,
      // Send the calendar date as a no-offset local-style timestamp (noon to
      // avoid any midnight DST edge case). This matches how BratislavaNow on
      // the kiosk stores PurchaseDate — a DateTime with Kind=Unspecified that
      // round-trips through Postgres `timestamp without time zone` and back to
      // the browser's local interpretation without shifting the calendar date.
      // DO NOT use new Date(...).toISOString() — that promotes to UTC and
      // shifts the day backwards by the local TZ offset on save (ticket
      // 2026-05-06 "date went to a day yesterday after editing").
      purchaseDate: `${this.editPurchaseDate}T12:00:00`,
      lines: this.editPurchaseLines()
    };

    this.mpService.update(target.id, dto).subscribe({
      next: () => {
        // Invalidate the Inventár cache too — admin may have moved a Nákup in
        // or out of Inventár (locationId set/cleared), so the next visit to
        // either tab reloads fresh data.
        this.inventoryPurchases.set([]);
        // Optionally upload a freshly picked receipt after the save succeeds.
        const file = this.editReceiptFile();
        if (file) {
          this.editReceiptUploading.set(true);
          this.mpService.uploadReceipt(target.id, file).subscribe({
            next: () => {
              this.editReceiptUploading.set(false);
              this.editPurchaseSaving.set(false);
              this.cancelEditPurchase();
              this.loadPurchases();
              if (this.tab() === 'inventar') this.loadInventory();
            },
            error: () => {
              this.editReceiptUploading.set(false);
              this.editPurchaseSaving.set(false);
              this.editPurchaseError.set('Záznam uložený, ale účtenka sa nenahrala.');
              // Don't auto-close so the admin sees the warning.
              this.loadPurchases();
              if (this.tab() === 'inventar') this.loadInventory();
            }
          });
        } else {
          this.editPurchaseSaving.set(false);
          this.cancelEditPurchase();
          this.loadPurchases();
          if (this.tab() === 'inventar') this.loadInventory();
        }
      },
      error: e => {
        this.editPurchaseSaving.set(false);
        this.editPurchaseError.set(this.apiError.friendly(e, 'Uloženie nákupu zlyhalo'));
      }
    });
  }

  // ─── Inventár ────────────────────────────────────────────────────

  loadInventory() {
    this.inventoryError.set('');
    this.inventoryLoading.set(true);
    this.mpService.list({
      from: this.inventoryFilterFrom || undefined,
      to:   this.inventoryFilterTo   || undefined,
      inventoryOnly: true
    }).subscribe({
      next: ps => { this.inventoryPurchases.set(ps); this.inventoryLoading.set(false); },
      error: e => { this.inventoryError.set(this.apiError.friendly(e, 'Načítanie inventára zlyhalo')); this.inventoryLoading.set(false); }
    });
  }

  applyInventoryFilters() { this.loadInventory(); }

  toggleInventoryRow(id: number) {
    this.expandedInventoryId.update(open => open === id ? null : id);
  }

  exportInventoryExcel() {
    this.exporting.set(true);
    this.mpService.downloadExcel({
      from: this.inventoryFilterFrom || undefined,
      to:   this.inventoryFilterTo   || undefined,
      inventoryOnly: true
    });
    // downloadExcel is fire-and-forget (no completion callback on the download
    // helper), so the busy state is reset via a bounded timeout instead.
    setTimeout(() => this.exporting.set(false), 4000);
  }

  onDeleteInventoryPurchase(p: MaterialPurchase) {
    if (!confirm(`Vymazať nákup z ${this.fmtDate(p.purchaseDate)} (${this.formatEur(p.totalCost)})?`)) return;
    this.mpService.delete(p.id).subscribe({
      next: () => this.loadInventory(),
      error: e => this.inventoryError.set(this.apiError.friendly(e, 'Vymazanie nákupu zlyhalo'))
    });
  }

  // ─── Neidentifikované ────────────────────────────────────────────

  loadUnknownGroups() {
    this.unknownError.set('');
    this.unknownLoading.set(true);
    this.mpService.getUnknownGroups().subscribe({
      next: gs => { this.unknownGroups.set(gs); this.unknownLoading.set(false); },
      error: e => { this.unknownError.set(this.apiError.friendly(e, 'Načítanie neidentifikovaných položiek zlyhalo')); this.unknownLoading.set(false); }
    });
  }

  openPromote(g: UnknownMaterialGroup) {
    this.promoteTarget.set(g);
    this.promoteMode.set('new');
    this.promoteName  = g.materialNameRaw;
    this.promoteUnit  = g.unit;
    this.promotePrice = +g.averageUnitPrice.toFixed(4);
    this.promoteApplyAll = true;
    this.promoteCatalogueId = null;
    this.promoteCatalogueSearch = '';
    this.promoteError.set('');
    this.promoteResultMessage.set('');
    // Make sure the catalogue is loaded for the merge picker.
    if (this.materials().length === 0) this.load();
  }

  closePromote() {
    this.promoteTarget.set(null);
    this.promoteSaving.set(false);
    this.promoteError.set('');
  }

  /** Promote a group: pick the first orphan line in the group as the target line for the API call.
   *  The bulk-apply flag handles linking the rest server-side. */
  async runPromote() {
    const g = this.promoteTarget();
    if (!g) return;
    this.promoteSaving.set(true);
    this.promoteError.set('');

    // The promote endpoint takes a (purchaseId, lineId) — we need ANY orphan line
    // in the group as the entry point. Fetch the latest list with this group's
    // raw name to find one. Server-side bulk-apply handles the rest.
    this.mpService.list({ from: undefined, to: undefined }).subscribe({
      next: purchases => {
        const targetLine = purchases
          .flatMap(p => p.lines.map(l => ({ p, l })))
          .find(({ l }) =>
            l.materialId === null
            && l.materialNameRaw.trim().toLowerCase() === g.materialNameRaw.trim().toLowerCase()
            && l.unit.trim().toLowerCase() === g.unit.trim().toLowerCase());
        if (!targetLine) {
          this.promoteSaving.set(false);
          this.promoteError.set('Cieľová položka už nie je dostupná. Skús obnoviť stránku.');
          return;
        }

        const dto: PromoteMaterialLine = this.promoteMode() === 'new'
          ? {
              mode: 'new',
              newName: this.promoteName.trim() || g.materialNameRaw,
              newUnit: this.promoteUnit.trim() || g.unit,
              newPricePerUnit: this.promotePrice >= 0 ? this.promotePrice : 0,
              applyToAllMatchingRawName: this.promoteApplyAll
            }
          : {
              mode: 'merge',
              catalogueMaterialId: this.promoteCatalogueId ?? undefined,
              applyToAllMatchingRawName: this.promoteApplyAll
            };

        if (dto.mode === 'merge' && !dto.catalogueMaterialId) {
          this.promoteSaving.set(false);
          this.promoteError.set('Vyber existujúci materiál z katalógu.');
          return;
        }

        this.mpService.promoteLine(targetLine.p.id, targetLine.l.id, dto).subscribe({
          next: res => {
            this.promoteSaving.set(false);
            this.promoteResultMessage.set(
              res.createdNewCatalogueRow
                ? `Vytvorený nový materiál „${res.materialName}" — premapovaných ${res.linesLinked} položiek.`
                : `Premapovaných ${res.linesLinked} položiek na „${res.materialName}".`);
            // Refresh data so the group disappears from the list.
            this.loadUnknownGroups();
            this.load();           // catalogue may have new row
            this.purchases.set([]);// invalidate Nákupy cache so next visit re-fetches
            // Auto-close after a short delay so the user sees the confirmation.
            setTimeout(() => this.closePromote(), 1200);
          },
          error: e => {
            this.promoteSaving.set(false);
            this.promoteError.set(this.apiError.friendly(e, 'Premapovanie zlyhalo'));
          }
        });
      },
      error: e => {
        this.promoteSaving.set(false);
        this.promoteError.set(this.apiError.friendly(e, 'Vyhľadanie cieľovej položky zlyhalo'));
      }
    });
  }

  // ─── Helpers ─────────────────────────────────────────────────────

  /** Slovak EUR formatting — used by all tabs. */
  formatEur(value: number): string {
    return new Intl.NumberFormat('sk-SK', { style: 'currency', currency: 'EUR', minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value || 0);
  }

  /** dd.MM.yyyy from an ISO timestamp. */
  fmtDate(iso: string): string {
    const d = new Date(iso);
    return `${String(d.getDate()).padStart(2, '0')}.${String(d.getMonth() + 1).padStart(2, '0')}.${d.getFullYear()}`;
  }

  /** YYYY-MM-DD for filter inputs. */
  private fmtIso(d: Date): string {
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }
}
