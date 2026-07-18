import { Component, ChangeDetectionStrategy, OnInit, input, output, signal, computed, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Location } from '../../services/location.service';
import {
  MaterialPurchaseService,
  MaterialPurchase,
  CreateKioskMaterialPurchase,
  CreateMaterialPurchaseLine
} from '../../services/material-purchase.service';
import { KioskService } from '../../services/kiosk.service';
import { normaliseFile, fileToDataUrl, compressImage } from '../../utils/image-utils';

/**
 * Kiosk-side Nákup materiálu capture, mounted by the kiosk page once the worker
 * has been PIN-validated through the existing šichta modal. Two entry points,
 * both pass through here:
 *
 *   • In-modal mode-pick — the worker tapped an employee tile, entered their
 *     PIN, and chose "Nákup materiálu" instead of "Zaznamenať šichtu". No
 *     šichta is recorded; the resulting MaterialPurchase has TimeEntryId=null.
 *
 *   • Post-šichta result button — the worker logged hours at the trigger
 *     Location; the result screen offers "Pokračovať s nákupom materiálu"
 *     and the MaterialPurchase links back via TimeEntryId.
 *
 * In both cases the host injects an already-validated PIN via initialPin,
 * optionally a TimeEntryId and a target Location. The component starts at the
 * Location picker (when no initialLocationId) or the Položky list (when a
 * Location was pre-selected) — never asks for the PIN again.
 *
 * Older-worker UX rules (NOTIFICATIONS_PLAN.md §10) — big targets, plain Slovak,
 * no jargon, high contrast. Mirror the kiosk patterns the customer already trusts.
 */
type Step = 'location' | 'lines' | 'receipt' | 'saving' | 'result';

interface WorkingLine {
  /** Internal client-side id; not sent to the server. */
  uid: number;
  materialId: number | null;        // null = free-typed (server-side stays null too)
  materialNameRaw: string;
  unit: string;
  quantity: number;
  unitPrice: number;
}

@Component({
  selector: 'app-nakup-flow',
  imports: [FormsModule],
  templateUrl: './nakup-flow.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class NakupFlowComponent implements OnInit {
  // ─── Inputs / outputs ───────────────────────────────────────────
  /**
   * Kept for forward-compat; both call paths today are PIN-already-validated.
   * Future entry points that need their own PIN step should add it host-side
   * rather than re-introducing one here, so the kiosk has one canonical PIN
   * UI (the existing šichta modal numpad).
   */
  mode               = input<'in-shift'>('in-shift');
  locations          = input<Location[]>([]);
  initialPin         = input<string | null>(null);
  initialTimeEntryId = input<number | null>(null);
  initialLocationId  = input<number | null>(null);
  /**
   * The "Nákup materiálu" Location.Id resolved by the kiosk page (config or
   * fallback by name). When set AND no initialTimeEntryId is provided, the
   * Location step shows an hours input and submitting will first POST to
   * /api/kiosk/log-hours under this Location, then create the purchase
   * stamped with the resulting TimeEntryId. When null OR an initialTimeEntryId
   * is already provided (post-šichta-result path), the hours input stays hidden.
   */
  triggerLocationId  = input<number | null>(null);

  close   = output<void>();
  success = output<MaterialPurchase>();

  // ─── Step ────────────────────────────────────────────────────────
  step = signal<Step>('location');
  /** PIN injected by the host (already validated). Used for the kiosk POST. */
  pin = '';

  // ─── Location ────────────────────────────────────────────────────
  selectedLocationId = signal<number | null>(null);
  /** True when "Nezadané / všeobecné stock" was explicitly chosen. Kept separate
   *  from selectedLocationId() === null so we can tell "not picked yet" from
   *  "deliberately unallocated". */
  generalStockChosen = signal(false);

  selectedLocationName = computed(() => {
    if (this.generalStockChosen()) return 'Sklad';
    const id = this.selectedLocationId();
    return id == null ? '' : (this.locations().find(l => l.id === id)?.name ?? '');
  });

  // ─── Položky ─────────────────────────────────────────────────────
  catalogue       = signal<{ id: number; name: string; unit: string; pricePerUnit: number }[]>([]);
  catalogueLoaded = signal(false);
  lines           = signal<WorkingLine[]>([]);
  totalCost       = computed(() => this.lines().reduce((sum, l) => sum + this.roundLine(l.quantity * l.unitPrice), 0));

  // Add-line picker state
  addingLine     = signal(false);
  pickerSearch   = '';
  /** When true, the inline "+ Nový materiál" form is open. */
  newMaterialOpen = signal(false);
  newMaterialName = '';
  newMaterialUnit = '';
  newMaterialPrice = 0;

  filteredCatalogue = computed(() => {
    const q = this.pickerSearch.trim().toLowerCase();
    const all = this.catalogue();
    if (!q) return all;
    return all.filter(m => m.name.toLowerCase().includes(q));
  });

  // ─── Receipt ─────────────────────────────────────────────────────
  receiptFile    = signal<File | null>(null);
  receiptPreview = signal<string | null>(null);
  receiptUploadError = signal('');

  // ─── Save / result ───────────────────────────────────────────────
  saving      = signal(false);
  saveError   = signal('');
  resultPurchase = signal<MaterialPurchase | null>(null);
  /** Set on the result screen when the purchase saved but the receipt upload
   *  failed afterwards. Surfaced as a Slovak hint so the worker (and admin
   *  reading shoulder-over) knows the receipt is missing. */
  receiptUploadFailed = signal(false);

  // ─── Quick-quantity preset chips (mirrors the existing kiosk hours pattern) ───
  readonly qtyPresets = [1, 5, 10, 20, 50];

  // ─── Hours auto-book (mode-pick path only) ──────────────────────
  /** Hours to log under the trigger Location alongside the purchase. Default 1.
   *  Hidden when the worker came in via the post-šichta path (initialTimeEntryId set). */
  hoursWorked = signal(1);
  /** Hour preset chips — same fractions as the existing kiosk šichta flow. */
  readonly hoursPresets = [0.5, 1, 2, 4, 5, 5.5, 6, 7, 7.5, 8];

  /** True when the in-modal Location step should expose hours entry — i.e. there
   *  is a trigger Location configured AND no šichta has been recorded yet. */
  showHoursInput = computed(() =>
    this.initialTimeEntryId() == null && this.triggerLocationId() != null);

  private mpService    = inject(MaterialPurchaseService);
  private kioskService = inject(KioskService);
  private nextUid = 1;

  // ─────────────────────────────────────────────────────────────────
  //  Lifecycle
  // ─────────────────────────────────────────────────────────────────

  ngOnInit() {
    // Load catalogue eagerly — small list, used by every Položky add-row click.
    this.mpService.getKioskCatalogue().subscribe({
      next: list => {
        this.catalogue.set(list.map(m => ({ id: m.id, name: m.name, unit: m.unit, pricePerUnit: m.pricePerUnit })));
        this.catalogueLoaded.set(true);
      },
      error: () => this.catalogueLoaded.set(true)  // empty catalogue is OK; "+ Nový materiál" still works.
    });

    // PIN is always injected by the host. The target Location for the purchase
    // is normally NOT the trigger Location ("Nákup materiálu" — that's where
    // the worker shopped, not where the materials are FOR), so we always show
    // the Location picker unless the host explicitly pre-selected one.
    // Workers can pick "Inventár" if the materials aren't for a site.
    const pin = this.initialPin();
    if (pin) this.pin = pin;
    const locId = this.initialLocationId();
    if (locId != null) {
      this.selectedLocationId.set(locId);
      this.step.set('lines');
    } else {
      this.step.set('location');
    }
  }

  // ─────────────────────────────────────────────────────────────────
  //  Location step
  // ─────────────────────────────────────────────────────────────────

  pickLocation(id: number) {
    this.selectedLocationId.set(id);
    this.generalStockChosen.set(false);
    this.step.set('lines');
  }

  pickGeneralStock() {
    this.selectedLocationId.set(null);
    this.generalStockChosen.set(true);
    this.step.set('lines');
  }

  // ─── Hours adjusters ────────────────────────────────────────────

  adjustHours(delta: number) {
    // 0.25h precision, capped at 0.25..24 (mirrors the existing kiosk šichta UI)
    const next = Math.round((this.hoursWorked() + delta) * 4) / 4;
    if (next >= 0.25 && next <= 24) this.hoursWorked.set(next);
  }
  setHours(h: number) {
    this.hoursWorked.set(h);
  }

  /** Back arrow on the Location step. Closes the whole flow — host returns
   *  the worker to the kiosk main view. */
  backFromLocation() {
    this.done();
  }

  // ─────────────────────────────────────────────────────────────────
  //  Položky step
  // ─────────────────────────────────────────────────────────────────

  openAddLine() {
    this.addingLine.set(true);
    this.pickerSearch = '';
    this.newMaterialOpen.set(false);
    this.newMaterialName = '';
    this.newMaterialUnit = '';
    this.newMaterialPrice = 0;
  }

  cancelAddLine() {
    this.addingLine.set(false);
    this.newMaterialOpen.set(false);
  }

  /** Add an existing catalogue material as a fresh line with default qty=1. */
  addCatalogueLine(m: { id: number; name: string; unit: string; pricePerUnit: number }) {
    this.lines.update(arr => [...arr, {
      uid: this.nextUid++,
      materialId: m.id,
      materialNameRaw: m.name,
      unit: m.unit,
      quantity: 1,
      unitPrice: m.pricePerUnit
    }]);
    this.addingLine.set(false);
  }

  /** Add a free-typed material — server stays MaterialId=null until admin promotes. */
  addCustomLine() {
    const name = this.newMaterialName.trim();
    const unit = this.newMaterialUnit.trim();
    if (!name) { return; }
    if (!unit) { return; }
    this.lines.update(arr => [...arr, {
      uid: this.nextUid++,
      materialId: null,
      materialNameRaw: name,
      unit,
      quantity: 1,
      unitPrice: this.newMaterialPrice || 0
    }]);
    this.addingLine.set(false);
    this.newMaterialOpen.set(false);
    this.newMaterialName = '';
    this.newMaterialUnit = '';
    this.newMaterialPrice = 0;
  }

  removeLine(uid: number) {
    this.lines.update(arr => arr.filter(l => l.uid !== uid));
  }

  setLineQty(uid: number, qty: number) {
    if (qty < 0) qty = 0;
    this.lines.update(arr => arr.map(l => l.uid === uid ? { ...l, quantity: qty } : l));
  }

  adjustLineQty(uid: number, delta: number) {
    this.lines.update(arr => arr.map(l => {
      if (l.uid !== uid) return l;
      const next = Math.max(0, +(l.quantity + delta).toFixed(3));
      return { ...l, quantity: next };
    }));
  }

  setLineUnitPrice(uid: number, price: number) {
    if (price < 0) price = 0;
    this.lines.update(arr => arr.map(l => l.uid === uid ? { ...l, unitPrice: price } : l));
  }

  lineTotal(l: WorkingLine): number {
    return this.roundLine(l.quantity * l.unitPrice);
  }

  goToReceipt() {
    if (this.lines().length === 0) return;
    this.step.set('receipt');
  }

  goBackToLines() {
    this.step.set('lines');
  }

  // ─────────────────────────────────────────────────────────────────
  //  Receipt step (optional)
  // ─────────────────────────────────────────────────────────────────

  async onReceiptSelected(ev: Event) {
    this.receiptUploadError.set('');
    const input = ev.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    try {
      const normalised = await normaliseFile(file);
      const compressed = await compressImage(normalised, 2048, 0.85);
      const dataUrl    = await fileToDataUrl(compressed);
      this.receiptFile.set(compressed);
      this.receiptPreview.set(dataUrl);
    } catch {
      this.receiptUploadError.set('Fotka sa nepodarila načítať. Skús znova.');
    } finally {
      // Allow re-picking the same file
      input.value = '';
    }
  }

  removeReceipt() {
    this.receiptFile.set(null);
    this.receiptPreview.set(null);
  }

  /** Skip the receipt and submit; same as Save but without uploading. */
  skipReceiptAndSave() {
    this.receiptFile.set(null);
    this.receiptPreview.set(null);
    this.submit();
  }

  // ─────────────────────────────────────────────────────────────────
  //  Submit
  // ─────────────────────────────────────────────────────────────────

  async submit() {
    if (this.saving()) return;
    if (this.lines().length === 0) {
      this.saveError.set('Pridaj aspoň jednu položku.');
      this.step.set('lines');
      return;
    }
    this.saveError.set('');
    this.saving.set(true);
    this.step.set('saving');

    // Auto-book the šichta hours under the trigger Location (mode-pick path only).
    // If we already have an initialTimeEntryId (post-šichta-result path), skip —
    // the šichta is already logged and the purchase just links to it.
    let timeEntryId: number | null = this.initialTimeEntryId() ?? null;
    if (timeEntryId == null && this.showHoursInput()) {
      const triggerId = this.triggerLocationId();
      const hours = this.hoursWorked();
      if (triggerId != null && hours > 0) {
        try {
          const res = await new Promise<{ timeEntryId?: number | null }>((resolve, reject) => {
            this.kioskService.logHours(this.pin, triggerId, hours).subscribe({
              next: r => resolve(r as any),
              error: e => reject(e)
            });
          });
          timeEntryId = res?.timeEntryId ?? null;
        } catch (e: any) {
          this.saving.set(false);
          this.saveError.set(e?.error || 'Hodiny sa nepodarilo zaznamenať. Skús znova.');
          this.step.set('lines');
          return;
        }
      }
    }

    const payload: CreateKioskMaterialPurchase = {
      pin: this.pin,
      locationId: this.selectedLocationId(),
      timeEntryId,
      lines: this.lines().map(l => ({
        materialId: l.materialId,
        materialNameRaw: l.materialNameRaw,
        unit: l.unit,
        quantity: l.quantity,
        unitPrice: l.unitPrice
      } as CreateMaterialPurchaseLine))
    };

    this.mpService.kioskCreate(payload).subscribe({
      next: async purchase => {
        // Optional receipt upload, after the purchase row is committed.
        // Failures are NOT swallowed — a failed receipt upload that we hide
        // shows up later as "no photo on the row" and confuses the customer.
        // Surface a flag on the result screen so the worker (and the admin
        // reading the shoulder over) knows the receipt is missing and can
        // retry from the admin Nákupy edit modal.
        this.receiptUploadFailed.set(false);
        const file = this.receiptFile();
        if (file) {
          try {
            const url = await new Promise<string>((resolve, reject) => {
              this.mpService.kioskUploadReceipt(purchase.id, file, this.pin).subscribe({
                next: u => resolve(u),
                error: e => reject(e)
              });
            });
            purchase = { ...purchase, receiptPhotoUrl: url };
          } catch (e) {
            // eslint-disable-next-line no-console
            console.warn('[NakupFlow] receipt upload failed', e);
            this.receiptUploadFailed.set(true);
          }
        }

        this.resultPurchase.set(purchase);
        this.saving.set(false);
        this.step.set('result');
        this.success.emit(purchase);
      },
      error: err => {
        this.saving.set(false);
        this.saveError.set(err?.error || 'Uloženie sa nepodarilo. Skús znova.');
        this.step.set('lines');
      }
    });
  }

  // ─────────────────────────────────────────────────────────────────
  //  Done / close
  // ─────────────────────────────────────────────────────────────────

  done() {
    this.close.emit();
  }

  // ─────────────────────────────────────────────────────────────────
  //  Helpers
  // ─────────────────────────────────────────────────────────────────

  private roundLine(v: number): number {
    return Math.round(v * 10000) / 10000;
  }

  /** Slovak EUR formatting — 2 decimals, comma separator. Mirrors location-manage-panel. */
  fmtEur(value: number): string {
    return new Intl.NumberFormat('sk-SK', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value) + ' €';
  }
}
