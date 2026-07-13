import { Component, OnInit, signal, computed, inject } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { ModalComponent } from '../../components/modal/modal.component';
import {
  InvoiceService,
  InvoiceDocument,
  InvoiceLine,
  UpdateInvoiceLinePayload,
  UpdateInvoiceDeliveryListPayload
} from '../../services/invoice.service';
import { LocationService, Location } from '../../services/location.service';

/**
 * /admin/invoices/:id — review one scanned invoice. The manager edits line
 * fields + assigns a Location per delivery list, watches the reconciliation
 * status, and either commits (server re-checks reconciliation) or discards.
 * See INVOICE_SCANNING_PLAN.md §"Admin review UX".
 */
@Component({
  selector: 'app-invoice-review',
  standalone: true,
  imports: [CommonModule, FormsModule, DatePipe, RouterLink, NavbarComponent, SpinnerComponent, ModalComponent],
  templateUrl: './invoice-review.page.html'
})
export class InvoiceReviewPage implements OnInit {
  private svc = inject(InvoiceService);
  private locationService = inject(LocationService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);

  id = 0;
  invoice = signal<InvoiceDocument | null>(null);
  locations = signal<Location[]>([]);
  loading = signal(false);
  error = signal<string | null>(null);
  committing = signal(false);
  discarding = signal(false);

  readonly: boolean = false;
  reconcileOk = computed(() => this.invoice()?.reconciliationOk ?? false);
  reconcileNote = computed(() => this.invoice()?.reconciliationNote ?? '');
  isReview = computed(() => this.invoice()?.status === 'review');
  isCommitted = computed(() => this.invoice()?.status === 'committed');
  /** Location dropdown stays editable on committed invoices too, so the
   *  manager can re-assign a delivery list to a different site after the
   *  fact. Line-item numbers stay locked — those are financial history.
   *  Only discarded invoices are fully read-only. */
  isLocationEditable = computed(() => this.invoice()?.status !== 'discarded');

  /** The issue date stays correctable even after commit — the API cascades
   *  the change to inherited purchase/usage dates, so a mis-dated document
   *  can be moved to the right month without re-uploading. */
  isDateEditable = computed(() => this.invoice()?.status !== 'discarded');

  /** Supplier name stays correctable even after commit — OCR sometimes reads
   *  a logo or stamp instead of the printed company. */
  isSupplierEditable = computed(() => this.invoice()?.status !== 'discarded');

  /** Where this document's money went: line totals grouped by the EFFECTIVE
   *  location (line override ?? delivery list ?? Sklad/Inventár). Rendered
   *  as chips in the header for a quick glance — both bez DPH and s DPH. */
  locationBreakdown = computed(() => {
    const inv = this.invoice();
    if (!inv) return [];
    const sums = new Map<string, { excl: number; incl: number }>();
    for (const dl of inv.deliveryLists) {
      for (const l of dl.lines) {
        const name = l.locationName ?? dl.locationName ?? 'Sklad / Inventár';
        const acc = sums.get(name) ?? { excl: 0, incl: 0 };
        acc.excl += l.lineTotal || 0;
        acc.incl += (l.lineTotal || 0) + this.round2((l.lineTotal || 0) * l.vatRate / 100);
        sums.set(name, acc);
      }
    }
    return [...sums.entries()]
      .map(([name, v]) => ({ name, excl: this.round2(v.excl), incl: this.round2(v.incl) }))
      .sort((a, b) => b.excl - a.excl);
  });

  /** Line total incl. VAT for the read-only "S DPH" column. */
  lineTotalInclVat(line: { lineTotal: number; vatRate: number }): number {
    return this.round2((line.lineTotal || 0) + this.round2((line.lineTotal || 0) * line.vatRate / 100));
  }

  /** Holds the purchaseId of the most recently saved delivery list so the
   *  template can flash a "✓ Uložené" hint next to its Pracovisko picker.
   *  Cleared after 2 seconds. */
  recentlySavedDlId = signal<number | null>(null);
  private savedHintTimer?: ReturnType<typeof setTimeout>;

  // ─── Modal state (replaces window.confirm + native alerts) ──────
  showCommitConfirm  = signal(false);
  showCommitSuccess  = signal(false);
  showDiscardConfirm = signal(false);

  // Sum of all line totals (excl. VAT) across all delivery lists.
  ourExclTotal = computed(() => {
    const inv = this.invoice();
    if (!inv) return 0;
    let lineSum = 0;
    for (const dl of inv.deliveryLists) for (const l of dl.lines) lineSum += l.lineTotal;
    return this.round2(lineSum);
  });

  // Sum of VAT across all lines.
  ourVatTotal = computed(() => {
    const inv = this.invoice();
    if (!inv) return 0;
    let vatSum = 0;
    for (const dl of inv.deliveryLists) for (const l of dl.lines) vatSum += this.round2(l.lineTotal * l.vatRate / 100);
    return this.round2(vatSum);
  });

  // Grand total incl. VAT — what we compare against the printed total.
  ourGrandTotal = computed(() => this.round2(this.ourExclTotal() + this.ourVatTotal()));

  ngOnInit() {
    this.id = Number(this.route.snapshot.paramMap.get('id'));
    this.load();
    this.locationService.getAll().subscribe(locs => this.locations.set(locs.filter(l => l.isActive)));
  }

  async load() {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.invoice.set(await this.svc.get(this.id));
    } catch (e: any) {
      this.error.set(e?.error ?? 'Nepodarilo sa načítať faktúru.');
    } finally {
      this.loading.set(false);
    }
  }

  async onLineEdit(line: InvoiceLine, payload: UpdateInvoiceLinePayload) {
    try {
      const updated = await this.svc.updateLine(this.id, line.id, payload);
      this.invoice.set(updated);
    } catch (e: any) {
      this.error.set(e?.error ?? 'Úprava riadku zlyhala.');
    }
  }

  async onLocationChange(purchaseId: number, locationId: number | null) {
    try {
      // -1 = "Sklad / Inventár" per the API contract
      const payload: UpdateInvoiceDeliveryListPayload = { locationId: locationId ?? -1 };
      const updated = await this.svc.updateDeliveryList(this.id, purchaseId, payload);
      this.invoice.set(updated);
      this.flashSavedHint(purchaseId);
    } catch (e: any) {
      this.error.set(e?.error ?? 'Priradenie pracoviska zlyhalo.');
    }
  }

  /** Per-row Pracovisko override. locationId null = "Podľa DL" (inherit) →
   *  send -1 so the backend clears the override and the row follows its
   *  delivery list again. A positive id pins the row to that site. */
  async onLineLocationChange(line: InvoiceLine, locationId: number | null) {
    try {
      const updated = await this.svc.updateLine(this.id, line.id, { locationId: locationId ?? -1 });
      this.invoice.set(updated);
    } catch (e: any) {
      this.error.set(e?.error ?? 'Priradenie pracoviska riadku zlyhalo.');
    }
  }

  private flashSavedHint(purchaseId: number) {
    this.recentlySavedDlId.set(purchaseId);
    if (this.savedHintTimer) clearTimeout(this.savedHintTimer);
    this.savedHintTimer = setTimeout(() => this.recentlySavedDlId.set(null), 2000);
  }

  // Commit flow: open modal → confirm → success modal → close.
  onCommit() { this.showCommitConfirm.set(true); }

  async onCommitConfirmed() {
    this.committing.set(true);
    this.error.set(null);
    try {
      // When the invoice doesn't reconcile, the confirm modal has already
      // warned the manager — commit with force so the server allows it and
      // records the override.
      const committed = await this.svc.commit(this.id, !this.reconcileOk());
      this.invoice.set(committed);
      this.showCommitConfirm.set(false);
      this.showCommitSuccess.set(true);
    } catch (e: any) {
      this.error.set(e?.error ?? 'Uloženie zlyhalo. Skontrolujte súčty.');
      this.showCommitConfirm.set(false);
    } finally {
      this.committing.set(false);
    }
  }

  /** Manager corrected the printed grand total (incl. VAT). Re-reconciles. */
  async onPrintedTotalBlur(val: string) {
    const n = this.parseNum(val);
    const inv = this.invoice();
    if (n == null || n < 0 || !inv || n === inv.totalInclVat) return;
    try {
      this.invoice.set(await this.svc.updatePrintedTotal(this.id, n));
    } catch (e: any) {
      this.error.set(e?.error ?? 'Úprava vytlačenej sumy zlyhala.');
    }
  }

  /** Zľava % edited — informational only (receipt prices are already
   *  discounted), no total recomputation. */
  async onDiscountBlur(line: { id: number; discountPercent: number | null }, val: string) {
    const n = val.trim() === '' ? 0 : this.parseNum(val);
    if (n == null || n < 0 || n > 100) return;
    if (n === (line.discountPercent ?? 0)) return;
    try {
      this.invoice.set(await this.svc.updateLine(this.id, line.id, { discountPercent: n }));
    } catch (e: any) {
      this.error.set(e?.error ?? 'Úprava zľavy zlyhala.');
    }
  }

  /** S DPH edited — receipts print GROSS per row; back-compute the net
   *  (Spolu bez DPH) from the entered gross and the row's VAT rate. */
  async onLineInclBlur(line: { id: number; lineTotal: number; vatRate: number }, val: string) {
    const gross = this.parseNum(val);
    if (gross == null || gross < 0) return;
    const net = this.round2(gross / (1 + line.vatRate / 100));
    if (net === line.lineTotal) return;
    try {
      this.invoice.set(await this.svc.updateLine(this.id, line.id, { lineTotal: net }));
    } catch (e: any) {
      this.error.set(e?.error ?? 'Úprava sumy s DPH zlyhala.');
    }
  }

  /** Prune a phantom/OCR-junk line during review. Instant — no modal; the
   *  row values are visible right next to the button and the whole document
   *  can be re-uploaded if something real gets removed by mistake. */
  async onDeleteLine(line: { id: number }) {
    try {
      this.invoice.set(await this.svc.deleteLine(this.id, line.id));
    } catch (e: any) {
      this.error.set(e?.error ?? 'Vymazanie riadku zlyhalo.');
    }
  }

  // ─── AI re-parse (vision model second opinion) ──────────────────

  aiRunning = signal(false);

  /** "Skúsiť AI" — replaces the draft with the vision model's reading.
   *  The reconciliation banner then shows the honest verdict. */
  async onAiReparse() {
    if (this.aiRunning()) return;
    this.aiRunning.set(true);
    this.error.set(null);
    try {
      this.invoice.set(await this.svc.aiReparse(this.id));
    } catch (e: any) {
      this.error.set(typeof e?.error === 'string' ? e.error : 'AI rozpoznanie zlyhalo.');
    } finally {
      this.aiRunning.set(false);
    }
  }

  // ─── Manual row addition (scanner missed a printed row) ────────

  /** Purchase id of the delivery list whose add-row form is open. */
  addingToDl = signal<number | null>(null);
  draftName = signal('');
  draftQty = signal('1');
  draftUnit = signal('ks');
  draftPrice = signal('');
  draftTotal = signal('');
  draftVat = signal('23');
  savingLine = signal(false);

  openAddLine(dlId: number) {
    this.addingToDl.set(dlId);
    this.draftName.set('');
    this.draftQty.set('1');
    this.draftUnit.set('ks');
    this.draftPrice.set('');
    this.draftTotal.set('');
    this.draftVat.set('23');
  }

  cancelAddLine() { this.addingToDl.set(null); }

  async saveNewLine(dlId: number) {
    const name = this.draftName().trim();
    if (!name) { this.error.set('Zadajte názov položky.'); return; }
    const num = (s: string): number | null => {
      const v = parseFloat((s || '').replace(/\s/g, '').replace(',', '.'));
      return Number.isFinite(v) ? v : null;
    };
    this.savingLine.set(true);
    this.error.set(null);
    try {
      const updated = await this.svc.addLine(this.id, dlId, {
        materialNameRaw: name,
        quantity: num(this.draftQty()) ?? 1,
        unit: this.draftUnit().trim() || 'ks',
        unitPrice: num(this.draftPrice()),
        lineTotal: num(this.draftTotal()),
        vatRate: num(this.draftVat()) ?? 23
      });
      this.invoice.set(updated);
      this.addingToDl.set(null);
    } catch (e: any) {
      this.error.set(e?.error ?? 'Pridanie riadku zlyhalo.');
    } finally {
      this.savingLine.set(false);
    }
  }

  /** Manager corrected the supplier name ("just in case" fix for OCR misreads). */
  async onSupplierNameChange(value: string) {
    const inv = this.invoice();
    const name = (value || '').trim();
    if (!inv || !name || name === inv.supplierName) return;
    try {
      this.invoice.set(await this.svc.updateSupplierName(this.id, name));
    } catch (e: any) {
      this.error.set(e?.error ?? 'Úprava dodávateľa zlyhala.');
    }
  }

  /** Manager corrected the issue date — drives the month on the Financie overview. */
  async onIssueDateChange(iso: string) {
    const inv = this.invoice();
    if (!iso || !inv || iso === (inv.issueDate || '').slice(0, 10)) return;
    try {
      this.invoice.set(await this.svc.updateIssueDate(this.id, iso));
    } catch (e: any) {
      this.error.set(e?.error ?? 'Úprava dátumu zlyhala.');
    }
  }

  // Discard flow: open modal → confirm → navigate back to list.
  onDiscard() { this.showDiscardConfirm.set(true); }

  /** Slovak message body for the discard modal — stronger wording for
   *  committed invoices because Option A also wipes the per-site material
   *  usage rows via DB cascade. */
  discardMessage = computed(() => {
    if (this.isCommitted()) {
      return 'Trvalo odstrániť uloženú faktúru?\n\n'
           + '• Záznamy zmiznú z účtovníctva (Materiál → Nákupy).\n'
           + '• Materiál priradený jednotlivým pracoviskám sa odstráni z ich spotreby.\n'
           + '• PDF zostane v archíve.';
    }
    return 'Zahodiť tento sken? Pôvodné PDF zostane v archíve, ale rozpracované riadky budú zmazané.';
  });

  async onDiscardConfirmed() {
    this.discarding.set(true);
    this.error.set(null);
    try {
      await this.svc.discard(this.id);
      this.showDiscardConfirm.set(false);
      this.router.navigate(['/admin/invoices']);
    } catch (e: any) {
      this.error.set(e?.error ?? 'Odstránenie zlyhalo.');
      this.showDiscardConfirm.set(false);
    } finally {
      this.discarding.set(false);
    }
  }

  // ── Inline edit handlers (called from template on blur) ─────────
  onQuantityBlur(line: InvoiceLine, val: string) {
    const n = this.parseNum(val);
    if (n != null && n !== line.quantity) this.onLineEdit(line, { quantity: n });
  }
  onUnitPriceBlur(line: InvoiceLine, val: string) {
    const n = this.parseNum(val);
    if (n != null && n !== line.unitPrice) this.onLineEdit(line, { unitPrice: n });
  }
  onLineTotalBlur(line: InvoiceLine, val: string) {
    const n = this.parseNum(val);
    if (n != null && n !== line.lineTotal) this.onLineEdit(line, { lineTotal: n });
  }
  onVatRateChange(line: InvoiceLine, val: string) {
    const n = this.parseNum(val);
    if (n != null && n !== line.vatRate) this.onLineEdit(line, { vatRate: n });
  }
  onReverseToggle(line: InvoiceLine, checked: boolean) {
    this.onLineEdit(line, { isReverseCharge: checked, vatRate: checked ? 0 : 23 });
  }

  // ── Formatting helpers ─────────────────────────────────────────
  formatMoney(v: number | null | undefined): string {
    if (v == null) return '—';
    return new Intl.NumberFormat('sk-SK', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(v);
  }
  formatQty(v: number): string {
    // Strip trailing zeros: 30.000 → 30; 1.5 → 1,5; 0.25 → 0,25.
    return new Intl.NumberFormat('sk-SK', { maximumFractionDigits: 3 }).format(v);
  }

  /**
   * Convert vatRate to a clean integer string so the <select> option binding
   * works regardless of whether the JSON serialises decimal(5,2) as "23" or
   * "23.00". The option values are plain strings "0" / "10" / "20" / "23".
   */
  vatRateString(v: number | null | undefined): string {
    if (v == null) return '23';
    return String(Math.round(v));
  }

  /**
   * The "Cena" column shows the list price (cena z cenníka, before discount).
   * If the parser couldn't extract it, fall back to the post-discount unit
   * price so the cell is never blank — the manager fixes it on edit.
   */
  listPriceFor(line: InvoiceLine): number {
    return line.listPriceExclVat ?? line.unitPrice;
  }

  formatPercent(v: number | null | undefined): string {
    if (v == null) return '—';
    return new Intl.NumberFormat('sk-SK', { minimumFractionDigits: 0, maximumFractionDigits: 2 }).format(v) + ' %';
  }

  /**
   * Expected line total computed locally: list × (1 − discount/100) × qty.
   * Compared against the parsed line.lineTotal. When they disagree, the
   * Spolu input gets a red border and the computed value shows underneath
   * so the manager spots OCR errors instantly.
   */
  expectedTotal(line: InvoiceLine): number {
    const list = line.listPriceExclVat ?? line.unitPrice;
    const disc = line.discountPercent ?? 0;
    const qty  = line.quantity ?? 0;
    return this.round2(list * (1 - disc / 100) * qty);
  }

  totalsMatch(line: InvoiceLine): boolean {
    // Only meaningful when we have a list price (otherwise the formula
    // collapses to unitPrice * qty which is what's already stored).
    if (line.listPriceExclVat == null || line.discountPercent == null) return true;
    // Credit / "Zľava z prenájmu" rows have list=0 by definition — the
    // formula list*(1-disc%)*qty is degenerate, suppress the warning.
    if (line.listPriceExclVat === 0) return true;
    const expected = this.expectedTotal(line);
    return Math.abs(expected - line.lineTotal) <= 0.01;
  }

  /**
   * "Cena" cell edit handler. Manager typed a new list price; the backend
   * recomputes the post-discount unit price + line total on save.
   *
   * Until the backend exposes a /lines/{id}/list-price endpoint we approximate:
   * compute the expected total and push that as both unitPrice (post-discount)
   * and lineTotal. The discount stays as-is, so visual math is consistent.
   */
  onListPriceBlur(line: InvoiceLine, val: string) {
    const newList = this.parseNum(val);
    if (newList == null || newList === (line.listPriceExclVat ?? line.unitPrice)) return;
    const disc = line.discountPercent ?? 0;
    const newUnit = this.round2(newList * (1 - disc / 100));
    const newTotal = this.round2(newUnit * (line.quantity ?? 0));
    this.onLineEdit(line, { unitPrice: newUnit, lineTotal: newTotal });
  }

  // Accept SK (comma) or invariant (dot) decimal input from the inline editor.
  private parseNum(s: string): number | null {
    if (!s) return null;
    const n = Number(s.replace(/\s/g, '').replace(',', '.'));
    return Number.isFinite(n) ? n : null;
  }
  private round2(v: number): number {
    return Math.round(v * 100) / 100;
  }

  statusLabel(status: string): string {
    switch (status) {
      case 'parsing':   return 'Spracováva sa';
      case 'review':    return 'Na kontrolu';
      case 'committed': return 'Uložená';
      case 'discarded': return 'Zahodená';
      default:          return status;
    }
  }
}
