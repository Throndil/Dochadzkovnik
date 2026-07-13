import { Component, OnInit, signal, computed, inject } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { ModalComponent } from '../../components/modal/modal.component';
import { InvoiceService, InvoiceDocument } from '../../services/invoice.service';
import { FeatureFlagService } from '../../services/feature-flag.service';

/**
 * /admin/invoices — list of scanned supplier invoices. Manager uploads a PDF
 * here and is taken to the review page once Document AI returns. Behind the
 * InvoiceScanning feature flag. See INVOICE_SCANNING_PLAN.md.
 */
@Component({
  selector: 'app-invoices',
  standalone: true,
  imports: [CommonModule, DatePipe, RouterLink, NavbarComponent, SpinnerComponent, ModalComponent],
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
  /** '' (all) | 'invoice' | 'receipt' */
  typeFilter = signal<string>('');

  filtered = computed(() => {
    const s = this.statusFilter();
    const t = this.typeFilter();
    return this.invoices().filter(i =>
      (!s || i.status === s) && (!t || (i.documentKind ?? 'invoice') === t));
  });

  /** Row pending delete confirmation (null = modal closed). */
  deleting = signal<InvoiceDocument | null>(null);
  deleteBusy = signal(false);

  ngOnInit() {
    this.load();
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
      const msg = e?.error ?? e?.message ?? 'Skenovanie zlyhalo.';
      this.uploadError.set(typeof msg === 'string' ? msg : 'Skenovanie zlyhalo.');
    } finally {
      this.uploading.set(false);
    }
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
