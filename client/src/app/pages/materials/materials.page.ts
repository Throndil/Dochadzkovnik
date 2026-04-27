import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { MaterialService, Material, CreateMaterial, UpdateMaterial } from '../../services/material.service';

@Component({
  selector: 'app-materials',
  imports: [NavbarComponent, FormsModule],
  templateUrl: './materials.page.html'
})
export class MaterialsPage implements OnInit {
  materials = signal<Material[]>([]);
  showForm  = signal(false);
  newMaterial: CreateMaterial = { name: '', unit: '', pricePerUnit: 0 };
  errorMsg = signal('');

  // Inline edit state
  editingId = signal<number | null>(null);
  editForm: UpdateMaterial = { name: '', unit: '', pricePerUnit: 0, isActive: true };

  // Common units the customer can pick from in one click
  unitPresets = ['vrece', 'kg', 'l', 'm²', 'm³', 'ks', 'bm'];

  constructor(private materialSvc: MaterialService) {}

  ngOnInit() { this.load(); }

  load() {
    this.materialSvc.getCatalogue().subscribe({
      next: ms => this.materials.set(ms),
      error: () => this.errorMsg.set('Nepodarilo sa načítať materiály.')
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
      error: e => this.errorMsg.set(typeof e?.error === 'string' ? e.error : 'Vytvorenie zlyhalo.')
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
      error: e => this.errorMsg.set(typeof e?.error === 'string' ? e.error : 'Úprava zlyhala.')
    });
  }

  onToggleActive(m: Material) {
    this.materialSvc.toggleMaterialActive(m.id).subscribe({ next: () => this.load() });
  }

  onDelete(m: Material) {
    if (!confirm(`Odstrániť materiál "${m.name}"? Ak bol už použitý, bude iba deaktivovaný.`)) return;
    this.materialSvc.deleteMaterial(m.id).subscribe({
      next: res => { if (res?.message) alert(res.message); this.load(); },
      error: () => this.errorMsg.set('Odstránenie zlyhalo.')
    });
  }

  pickUnit(u: string) { this.newMaterial.unit = u; }

  /** Slovak EUR formatting — used by the template. */
  formatEur(value: number): string {
    return new Intl.NumberFormat('sk-SK', { style: 'currency', currency: 'EUR', minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value || 0);
  }
}
