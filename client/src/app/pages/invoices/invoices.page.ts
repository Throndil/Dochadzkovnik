import { Component, ElementRef, OnInit, signal, computed, inject, viewChild } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { ModalComponent } from '../../components/modal/modal.component';
import { InvoiceService, InvoiceDocument, ScanStatus } from '../../services/invoice.service';
import { FeatureFlagService } from '../../services/feature-flag.service';
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
  /** Inclusive date range (yyyy-MM-dd). Empty string = unbounded on that end. */
  dateFrom = signal<string>('');
  dateTo = signal<string>('');
  /** Which date the range filters on: 'issue' (printed on doc) | 'scan' (uploadedAt). */
  dateBasis = signal<string>('issue');

  /** Status + date-range filtering, shared by both the Faktúry and Bločky columns. */
  private baseFiltered = computed(() => {
    const s = this.statusFilter();
    const from = this.dateFrom();
    const to = this.dateTo();
    const basis = this.dateBasis();
    return this.invoices().filter(i => {
      if (s && i.status !== s) return false;
      // yyyy-MM-dd compares lexicographically = chronologically; slice drops any ISO time.
      const key = ((basis === 'scan' ? i.uploadedAt : i.issueDate) ?? '').slice(0, 10);
      if (from && key < from) return false;
      if (to && key > to) return false;
      return true;
    });
  });

  /** Left column. Legacy rows with no documentKind are treated as invoices. */
  invoicesFiltered = computed(() =>
    this.baseFiltered().filter(i => (i.documentKind ?? 'invoice') !== 'receipt'));

  /** Right column. */
  receiptsFiltered = computed(() =>
    this.baseFiltered().filter(i => (i.documentKind ?? 'invoice') === 'receipt'));

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

  ngOnInit() {
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
      const created = await this.svc.upload(file);
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

  // SK number formatter for the table totals.
  formatMoney(v: number): string {
    return new Intl.NumberFormat('sk-SK', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(v);
  }
}
