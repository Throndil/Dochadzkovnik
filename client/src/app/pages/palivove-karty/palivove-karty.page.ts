import { Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { AlertComponent } from '../../components/alert/alert.component';
import { FuelCard, FuelCardService } from '../../services/fuel-card.service';
import { Employee, EmployeeService } from '../../services/employee.service';

/**
 * /admin/palivove-karty — registry of the company's fuel cards (F6).
 * Each card: label, note, current holder (any employee or unassigned —
 * some holders are not in the system yet). Mirrors the Odvody page UX.
 */
@Component({
  selector: 'app-palivove-karty',
  standalone: true,
  imports: [CommonModule, FormsModule, NavbarComponent, SpinnerComponent, AlertComponent],
  templateUrl: './palivove-karty.page.html'
})
export class PalivoveKartyPage implements OnInit {
  private svc = inject(FuelCardService);
  private empSvc = inject(EmployeeService);

  cards = signal<FuelCard[]>([]);
  employees = signal<Employee[]>([]);
  loading = signal(false);
  error = signal<string | null>(null);
  saving = signal(false);

  // Inline edit
  editingId = signal<number | null>(null);
  draftLabel = '';
  draftNote = '';
  draftEmployeeId: number | null = null;
  draftActive = true;

  // Add row
  adding = signal(false);
  newLabel = '';
  newNote = '';
  newEmployeeId: number | null = null;

  ngOnInit() { this.load(); }

  async load() {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.cards.set(await this.svc.list());
      this.empSvc.getAll().subscribe(list => this.employees.set(list.filter(e => e.isActive)));
    } catch (e: any) {
      this.error.set(this.errMsg(e));
    } finally {
      this.loading.set(false);
    }
  }

  startEdit(c: FuelCard) {
    this.editingId.set(c.id);
    this.draftLabel = c.label;
    this.draftNote = c.note ?? '';
    this.draftEmployeeId = c.employeeId;
    this.draftActive = c.isActive;
  }

  cancelEdit() { this.editingId.set(null); }

  async saveEdit(c: FuelCard) {
    if (!this.draftLabel.trim()) return;
    this.saving.set(true);
    try {
      const updated = await this.svc.update(c.id, {
        label: this.draftLabel.trim(),
        note: this.draftNote.trim() || null,
        employeeId: this.draftEmployeeId,
        isActive: this.draftActive
      });
      this.cards.update(arr => arr.map(x => x.id === c.id ? updated : x));
      this.editingId.set(null);
    } catch (e: any) {
      this.error.set(this.errMsg(e));
    } finally {
      this.saving.set(false);
    }
  }

  async addRow() {
    if (!this.newLabel.trim()) return;
    this.saving.set(true);
    try {
      const created = await this.svc.create({
        label: this.newLabel.trim(),
        note: this.newNote.trim() || null,
        employeeId: this.newEmployeeId,
        isActive: true
      });
      this.cards.update(arr => [...arr, created]);
      this.adding.set(false);
      this.newLabel = '';
      this.newNote = '';
      this.newEmployeeId = null;
    } catch (e: any) {
      this.error.set(this.errMsg(e));
    } finally {
      this.saving.set(false);
    }
  }

  async remove(c: FuelCard) {
    if (!confirm(`Zmazať kartu „${c.label}"?`)) return;
    try {
      await this.svc.delete(c.id);
      this.cards.update(arr => arr.filter(x => x.id !== c.id));
    } catch (e: any) {
      this.error.set(this.errMsg(e));
    }
  }

  private errMsg(e: any): string {
    return typeof e?.error === 'string' ? e.error :
           typeof e?.error?.error === 'string' ? e.error.error :
           e?.message ?? 'Neznáma chyba.';
  }
}
