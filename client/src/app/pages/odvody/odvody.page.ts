import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { AlertComponent } from '../../components/alert/alert.component';
import { CompanyRate, CompanyRateService } from '../../services/company-rate.service';
import { StepperComponent } from '../../components/stepper/stepper.component';

/**
 * /admin/odvody — the "Odvody" page: amounts the company pays on top
 * (odvody, ubytovanie 1 €, výjazd auta 30 €…). Customer edits amounts and
 * adds his own rows; the seeded (key-bearing) rows can't be deleted because
 * the app reads them (hrubá sadzba, výjazdy).
 */
@Component({
  selector: 'app-odvody',
  standalone: true,
  imports: [CommonModule, FormsModule, NavbarComponent, SpinnerComponent, AlertComponent, StepperComponent],
  templateUrl: './odvody.page.html'
})
export class OdvodyPage implements OnInit {
  private svc = inject(CompanyRateService);

  rates = signal<CompanyRate[]>([]);
  loading = signal(false);
  error = signal<string | null>(null);

  // Inline edit
  editingId = signal<number | null>(null);
  draftLabel = '';
  draftAmount: number | null = null;
  draftUnit = '';
  saving = signal(false);

  // Add row
  adding = signal(false);
  newLabel = '';
  newAmount: number | null = null;
  newUnit = '';

  ngOnInit() { this.load(); }

  async load() {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.rates.set(await this.svc.list());
    } catch (e: any) {
      this.error.set(e?.error ?? 'Načítanie zlyhalo.');
    } finally {
      this.loading.set(false);
    }
  }

  startEdit(r: CompanyRate) {
    this.editingId.set(r.id);
    this.draftLabel = r.label;
    this.draftAmount = r.amount;
    this.draftUnit = r.unit ?? '';
  }

  cancelEdit() { this.editingId.set(null); }

  async saveEdit(r: CompanyRate) {
    if (!this.draftLabel.trim() || this.draftAmount == null || this.draftAmount < 0) return;
    this.saving.set(true);
    try {
      const updated = await this.svc.update(r.id, {
        label: this.draftLabel.trim(),
        amount: this.draftAmount,
        unit: this.draftUnit.trim() || null
      });
      this.rates.update(arr => arr.map(x => x.id === r.id ? updated : x));
      this.editingId.set(null);
    } catch (e: any) {
      this.error.set(e?.error ?? 'Uloženie zlyhalo.');
    } finally {
      this.saving.set(false);
    }
  }

  async addRow() {
    if (!this.newLabel.trim() || this.newAmount == null || this.newAmount < 0) return;
    this.saving.set(true);
    try {
      const created = await this.svc.create({
        label: this.newLabel.trim(),
        amount: this.newAmount,
        unit: this.newUnit.trim() || null
      });
      this.rates.update(arr => [...arr, created]);
      this.adding.set(false);
      this.newLabel = '';
      this.newAmount = null;
      this.newUnit = '';
    } catch (e: any) {
      this.error.set(e?.error ?? 'Pridanie zlyhalo.');
    } finally {
      this.saving.set(false);
    }
  }

  async remove(r: CompanyRate) {
    if (!confirm(`Zmazať položku „${r.label}"?`)) return;
    try {
      await this.svc.delete(r.id);
      this.rates.update(arr => arr.filter(x => x.id !== r.id));
    } catch (e: any) {
      this.error.set(e?.error ?? 'Zmazanie zlyhalo.');
    }
  }

  formatMoney(v: number): string {
    return new Intl.NumberFormat('sk-SK', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(v);
  }
}
