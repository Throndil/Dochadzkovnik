import { Component, ElementRef, OnInit, signal, computed, inject, viewChild } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { ModalComponent } from '../../components/modal/modal.component';
import { InvoiceService, InvoiceDocument, ScanStatus } from '../../services/invoice.service';
import { FeatureFlagService } from '../../services/feature-flag.service';
import { DivisionService } from '../../services/division.service';
import { DatepickerDirective } from '../../directives/datepicker.directive';

/**
 * /admin/invoices — list of scanned supplier invoices. Manager uploads a PDF
 * here and is taken to the review page once Document AI returns. Behind the
 * InvoiceScanning feature flag. See INVOICE_SCANNING_PLAN.md.
 */
@Component({
  selector: 'app-invoices',
  standalone: true,
  imports: [CommonModule, DatePipe, RouterLink, NavbarComponent, SpinnerComponent, ModalComponent, DatepickerDirective],
  templateUrl: './invoices.page.html'
})
export class InvoicesPage implements OnInit {
  private svc = inject(InvoiceService);
  private router = inject(Router);
  private flags = inject(FeatureFlagService);
  division = inject(DivisionService);

  /** Show the second "Naskenovať mobilom" button only when both flags are on. */
  showCameraButton = computed(() => this.flags.invoiceScanning() && this.flags.invoiceCameraScan());

  invoices = signal<InvoiceDocument[]>([]);
  loading = signal(false);
  error = signal<string | null>(null);

  uploading = signal(false);
  uploadError = signal<string | null>(null);

  /** Holds the freshly-uploaded invoice while the success modal is showing.
   *  Cleared when the manager hits "Otvoriť" → navigates to the review page. */
  uploadedInvoice = signal<InvoiceDocument | null>(null);

  statusFilter = signal<string>('');
  /** Free-text lookup: dodávateľ / IČO / číslo dokladu (diacritics-insensitive). */
  searchText = signal<string>('');

  private static norm(s: string | null | undefined): string {
    return (s ?? '').normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase();
  }
  /** Inclusive date range (yyyy-MM-dd). Empty string = unbounded on that end.
   *  Default: previous + current month — the working set the customer lives in. */
  dateFrom = signal<string>(InvoicesPage.defaultFrom());
  dateTo = signal<string>(InvoicesPage.defaultTo());
  /** Which date the range filters on: 'issue' (printed on doc) | 'scan' (uploadedAt). */
  dateBasis = signal<string>('issue');

  private static isoDay(d: Date): string {
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }
  private static defaultFrom(): string {
    const n = new Date();
    return InvoicesPage.isoDay(new Date(n.getFullYear(), n.getMonth() - 1, 1));
  }
  private static defaultTo(): string {
    const n = new Date();
    return InvoicesPage.isoDay(new Date(n.getFullYear(), n.getMonth() + 1, 0));
  }

  /** Clear the range → show every document. */
  showAll() {
    this.dateFrom.set('');
    this.dateTo.set('');
  }

  private static readonly SK_MONTHS = ['Január', 'Február', 'Marec', 'Apríl', 'Máj', 'Jún', 'Júl', 'August', 'September', 'Október', 'November', 'December'];

  /** Human label of the active range: "Jún – Júl 2026" for whole months,
   *  otherwise the explicit day range. */
  rangeLabel = computed(() => {
    const from = this.dateFrom();
    const to = this.dateTo();
    const fmt = (iso: string) => {
      const [y, m, d] = iso.split('-').map(Number);
      return `${String(d).padStart(2, '0')}.${String(m).padStart(2, '0')}.${y}`;
    };
    if (!from && !to) return 'celé obdobie';
    if (!from) return `do ${fmt(to)}`;
    if (!to) return `od ${fmt(from)}`;
    // Whole-calendar-month span → month names instead of day soup.
    const [fy, fm, fd] = from.split('-').map(Number);
    const [ty, tm, td] = to.split('-').map(Number);
    if (fd === 1 && td === new Date(ty, tm, 0).getDate()) {
      const a = InvoicesPage.SK_MONTHS[fm - 1];
      const b = InvoicesPage.SK_MONTHS[tm - 1];
      if (fy === ty && fm === tm) return `${a} ${fy}`;
      if (fy === ty) return `${a} – ${b} ${fy}`;
      return `${a} ${fy} – ${b} ${ty}`;
    }
    return `${fmt(from)} – ${fmt(to)}`;
  });

  /** Documents of the ACTIVE DIVISION (Fáza D) — everything on this page is
   *  division-scoped; the navbar burger switches context. Legacy docs with
   *  no division field count as profistav. */
  private divisionDocs = computed(() => {
    const d = this.division.active();
    return this.invoices().filter(i => (i.division || 'profistav') === d);
  });

  /** Date-range filtering only — feeds both the columns AND the KPI tiles
   *  (customer: "suma na kontrolu sa neupravuje ak sa upraví dátum" — the
   *  tiles must follow the picked range). */
  private dateFiltered = computed(() => {
    const from = this.dateFrom();
    const to = this.dateTo();
    const basis = this.dateBasis();
    return this.divisionDocs().filter(i => {
      // yyyy-MM-dd compares lexicographically = chronologically; slice drops any ISO time.
      const key = ((basis === 'scan' ? i.uploadedAt : i.issueDate) ?? '').slice(0, 10);
      if (from && key < from) return false;
      if (to && key > to) return false;
      return true;
    });
  });

  // ─── Month strip: Príjem / Výdaj / Rozdiel (division, Fáza D) ───
  /** Month shown in the strip (YYYY-MM), independent of the range filter. */
  stripMonth = signal<string>(new Date().toISOString().slice(0, 7));

  /** Strip navigation ALSO filters the list to that month — the manager
   *  browses money flow and the month's documents together. */
  shiftStripMonth(delta: -1 | 1) {
    const [y, m] = this.stripMonth().split('-').map(Number);
    const d = new Date(y, m - 1 + delta, 1);
    this.stripMonth.set(`${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`);
    this.applyStripMonthRange();
  }

  /** Sync the date-range filter to the strip's calendar month. */
  private applyStripMonthRange() {
    const [y, m] = this.stripMonth().split('-').map(Number);
    this.dateFrom.set(InvoicesPage.isoDay(new Date(y, m - 1, 1)));
    this.dateTo.set(InvoicesPage.isoDay(new Date(y, m, 0)));
  }

  /** Jump back to the default view: current month in the strip, previous +
   *  current month in the list. */
  resetToCurrent() {
    this.stripMonth.set(new Date().toISOString().slice(0, 7));
    this.dateFrom.set(InvoicesPage.defaultFrom());
    this.dateTo.set(InvoicesPage.defaultTo());
  }

  stripMonthLabel = computed(() => {
    const [y, m] = this.stripMonth().split('-').map(Number);
    return `${InvoicesPage.SK_MONTHS[m - 1]} ${y}`;
  });

  /** Non-discarded division docs issued in the strip month. */
  private stripDocs = computed(() => {
    const month = this.stripMonth();
    return this.divisionDocs().filter(i =>
      i.status !== 'discarded' && (i.issueDate ?? '').slice(0, 7) === month);
  });

  stripIncome = computed(() =>
    this.stripDocs().filter(i => i.direction === 'income').reduce((s, i) => s + (i.totalInclVat || 0), 0));
  stripExpense = computed(() =>
    this.stripDocs().filter(i => i.direction !== 'income').reduce((s, i) => s + (i.totalInclVat || 0), 0));
  stripDiff = computed(() => this.stripIncome() - this.stripExpense());

  /** + status + search filters, shared by both the Faktúry and Bločky columns. */
  private baseFiltered = computed(() => {
    const s = this.statusFilter();
    const q = InvoicesPage.norm(this.searchText().trim());
    let rows = this.dateFiltered();
    if (s) rows = rows.filter(i => i.status === s);
    if (q) {
      rows = rows.filter(i =>
        InvoicesPage.norm(i.supplierName).includes(q)
        || InvoicesPage.norm(i.invoiceNumber).includes(q)
        || InvoicesPage.norm(i.supplierIco).includes(q));
    }
    return rows;
  });

  /** Documents visible after all filters — shown next to the range label. */
  shownCount = computed(() => this.baseFiltered().length);

  /** Left column. Legacy rows with no documentKind are treated as invoices. */
  invoicesFiltered = computed(() =>
    this.baseFiltered().filter(i => (i.documentKind ?? 'invoice') !== 'receipt'));

  /** Right column. */
  receiptsFiltered = computed(() =>
    this.baseFiltered().filter(i => (i.documentKind ?? 'invoice') === 'receipt'));

  // ─── Overview tiles ───
  // Follow the DATE range (customer expectation) but NOT the status filter —
  // the tiles describe statuses, so status-filtering them would be circular.
  pendingCount = computed(() => this.dateFiltered().filter(i => i.status === 'review').length);
  pendingSum = computed(() =>
    this.dateFiltered().filter(i => i.status === 'review').reduce((s, i) => s + (i.totalInclVat || 0), 0));
  committedCount = computed(() => this.dateFiltered().filter(i => i.status === 'committed').length);
  addedTodayCount = computed(() => {
    const today = new Date().toDateString();
    return this.dateFiltered().filter(i => i.uploadedAt && new Date(i.uploadedAt).toDateString() === today).length;
  });

  /** Row pending delete confirmation (null = modal closed). */
  deleting = signal<InvoiceDocument | null>(null);
  deleteBusy = signal(false);

  /** Pipeline health → persistent banner (quota spent / outage). */
  scanStatus = signal<ScanStatus | null>(null);

  scanStatusResetLabel = computed(() => {
    const until = this.scanStatus()?.aiExhaustedUntil;
    if (!until) return '';
    const d = new Date(until);
    const time = d.toLocaleTimeString('sk-SK', { hour: '2-digit', minute: '2-digit' });
    const today = new Date().toDateString() === d.toDateString();
    return today ? `dnes o ${time}` : `zajtra o ${time}`;
  });

  private route = inject(ActivatedRoute);

  ngOnInit() {
    // Arriving from the Súhrn division card carries the month being viewed
    // there (?mesiac=YYYY-MM) — open strip + list on that month, not the
    // default range.
    const mesiac = this.route.snapshot.queryParamMap.get('mesiac');
    if (mesiac && /^\d{4}-\d{2}$/.test(mesiac)) {
      this.stripMonth.set(mesiac);
      this.applyStripMonthRange();
    }
    this.load();
    this.svc.getScanStatus().then(s => this.scanStatus.set(s)).catch(() => {});
  }

  async load() {
    this.loading.set(true);
    this.error.set(null);
    try {
      const rows = await this.svc.list();
      this.invoices.set(rows);
    } catch (e: any) {
      this.error.set(e?.error ?? 'Nepodarilo sa načítať faktúry.');
    } finally {
      this.loading.set(false);
    }
  }

  async onFile(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;

    this.uploading.set(true);
    this.uploadError.set(null);
    try {
      const created = await this.svc.upload(file, this.division.active());
      // Reload the list so the new invoice shows up underneath the modal,
      // then surface a success modal — the manager hits "Otvoriť" to review.
      await this.load();
      this.uploadedInvoice.set(created);
    } catch (e: any) {
      this.uploadError.set(this.svc.friendlyError(e, 'Skenovanie zlyhalo.'));
    } finally {
      this.uploading.set(false);
    }
  }

  private pdfUploadRef = viewChild<ElementRef<HTMLInputElement>>('pdfUpload');

  /** "Nahrať ďalšiu" in the success modal — closes it and reopens the
   *  file picker so batch-uploading a stack of invoices is two taps each. */
  onUploadAnother() {
    this.uploadedInvoice.set(null);
    setTimeout(() => this.pdfUploadRef()?.nativeElement.click());
  }

  /** Triggered from the success modal's OK button. */
  onOpenUploaded() {
    const inv = this.uploadedInvoice();
    this.uploadedInvoice.set(null);
    if (inv) this.router.navigate(['/admin/invoices', inv.id]);
  }

  /** Message for the delete-confirm modal — stronger wording on committed docs. */
  deleteMessage = computed(() => {
    const inv = this.deleting();
    if (!inv) return '';
    const base = `Faktúra ${inv.invoiceNumber} (${this.formatMoney(inv.totalInclVat)} €) bude natrvalo vymazaná.`;
    return inv.status === 'committed'
      ? `${base} Je už uložená — vymazaním zmiznú aj jej materiálové nákupy z prehľadov.`
      : base;
  });

  async confirmDelete() {
    const inv = this.deleting();
    if (!inv || this.deleteBusy()) return;
    this.deleteBusy.set(true);
    try {
      await this.svc.discard(inv.id);
      this.invoices.set(this.invoices().filter(i => i.id !== inv.id));
      this.deleting.set(null);
    } catch (e: any) {
      this.error.set(e?.error ?? 'Vymazanie zlyhalo.');
      this.deleting.set(null);
    } finally {
      this.deleteBusy.set(false);
    }
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

  statusClass(status: string): string {
    switch (status) {
      case 'review':    return 'bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300';
      case 'committed': return 'bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300';
      case 'discarded': return 'bg-slate-100 text-slate-600 dark:bg-slate-700 dark:text-slate-300';
      case 'parsing':   return 'bg-blue-100 text-blue-800 dark:bg-blue-900/40 dark:text-blue-300';
      default:          return 'bg-slate-100 text-slate-600';
    }
  }

  /** Left-edge accent colour on each card, by status. */
  statusAccentClass(status: string): string {
    switch (status) {
      case 'review':    return 'border-amber-400';
      case 'committed': return 'border-emerald-400';
      default:          return 'border-slate-300 dark:border-slate-600';
    }
  }

  /** Documents uploaded without a readable číslo (blank papers) carry a
   *  synthesized BEZ-CISLA-… number — flagged with a "Ručný doklad" chip. */
  isManualDoc(inv: InvoiceDocument): boolean {
    return (inv.invoiceNumber || '').startsWith('BEZ-CISLA');
  }

  /** 1–2 letter supplier monogram for the card avatar. */
  initials(name: string): string {
    const words = (name || '').split(/[\s.,-]+/).filter(w => w.length >= 2 && !/^(spol|sro|as)$/i.test(w));
    if (words.length === 0) return '?';
    if (words.length === 1) return words[0].slice(0, 2).toUpperCase();
    return (words[0][0] + words[1][0]).toUpperCase();
  }

  // SK number formatter for the table totals.
  formatMoney(v: number): string {
    return new Intl.NumberFormat('sk-SK', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(v);
  }
}
