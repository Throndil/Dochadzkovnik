import { Component, signal, computed, OnInit } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { ModalComponent } from '../../components/modal/modal.component';
import { EmptyStateComponent } from '../../components/empty-state/empty-state.component';
import { MachineService, Machine, CreateMachine } from '../../services/machine.service';
import { ToastService } from '../../services/toast.service';
import { ApiErrorService } from '../../services/api-error.service';
import { normaliseFile } from '../../utils/image-utils';

/**
 * /admin/stroje — machine registry of the AZ Stroje division (Fáza F0).
 * Mirrors the Vozidlá page; editing is inline (no detail page — a machine
 * has just a name + note).
 */
@Component({
  selector: 'app-stroje',
  standalone: true,
  imports: [NavbarComponent, DecimalPipe, FormsModule, SpinnerComponent, ModalComponent, EmptyStateComponent],
  templateUrl: './stroje.page.html'
})
export class StrojePage implements OnInit {
  machines = signal<Machine[]>([]);
  loading = signal(true);
  showForm = signal(false);
  newMachine: CreateMachine = { name: '', note: '' };
  newPhoto: File | null = null;
  photoPreview = signal<string | null>(null);
  isDragOver = signal(false);

  // Inline edit
  editingId = signal<number | null>(null);
  draftName = '';
  draftNote = '';
  saving = signal(false);

  /** Row pending delete confirmation (null = modal closed). */
  deleting = signal<Machine | null>(null);
  deleteBusy = signal(false);

  deleteMessage = computed(() => {
    const m = this.deleting();
    return m
      ? `Natrvalo odstrániť mašinu "${m.name}"? Záznamy šícht zostanú, ale budú bez mašiny.`
      : '';
  });

  constructor(private svc: MachineService, private toast: ToastService, private apiError: ApiErrorService) {}

  ngOnInit() { this.load(); }

  load() {
    this.loading.set(true);
    this.svc.getAll().subscribe({
      next: ms => { this.machines.set(ms); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  cancelForm() {
    this.showForm.set(false);
    this.newMachine = { name: '', note: '' };
    this.newPhoto = null;
    this.photoPreview.set(null);
    this.isDragOver.set(false);
  }

  onPhotoSelected(event: Event) {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (file) this.processPhotoFile(file);
  }

  onDragOver(event: DragEvent) { event.preventDefault(); this.isDragOver.set(true); }
  onDragLeave() { this.isDragOver.set(false); }

  onDrop(event: DragEvent) {
    event.preventDefault();
    this.isDragOver.set(false);
    const file = event.dataTransfer?.files?.[0];
    if (file) this.processPhotoFile(file);
  }

  async processPhotoFile(file: File): Promise<void> {
    if (!file.type.startsWith('image/') && !file.name.match(/\.(heic|heif)$/i)) return;
    const normalised = await normaliseFile(file);
    this.newPhoto = normalised;
    const reader = new FileReader();
    reader.onload = e => this.photoPreview.set(e.target?.result as string);
    reader.readAsDataURL(normalised);
  }

  onCreate() {
    this.svc.create({ name: this.newMachine.name, note: this.newMachine.note || undefined }).subscribe(m => {
      const finish = () => {
        this.cancelForm();
        this.toast.success('Mašina pridaná');
        this.load();
      };
      if (this.newPhoto) {
        this.svc.uploadPhoto(m.id, this.newPhoto).subscribe({
          next: () => finish(),
          error: () => finish()
        });
      } else {
        finish();
      }
    });
  }

  startEdit(m: Machine) {
    this.editingId.set(m.id);
    this.draftName = m.name;
    this.draftNote = m.note ?? '';
  }

  cancelEdit() { this.editingId.set(null); }

  saveEdit(m: Machine) {
    if (!this.draftName.trim()) return;
    this.saving.set(true);
    this.svc.update(m.id, { name: this.draftName.trim(), note: this.draftNote.trim() || null, isActive: m.isActive }).subscribe({
      next: () => {
        this.saving.set(false);
        this.editingId.set(null);
        this.toast.success('Mašina uložená');
        this.load();
      },
      error: e => {
        this.saving.set(false);
        this.toast.error(this.apiError.friendly(e, 'Uloženie mašiny zlyhalo'));
      }
    });
  }

  onToggleActive(m: Machine) {
    this.svc.toggleActive(m.id).subscribe(() => {
      this.toast.success(m.isActive ? 'Mašina deaktivovaná' : 'Mašina aktivovaná');
      this.load();
    });
  }

  onDelete(m: Machine) {
    this.deleting.set(m);
  }

  confirmDelete() {
    const m = this.deleting();
    if (!m || this.deleteBusy()) return;
    this.deleteBusy.set(true);
    this.svc.delete(m.id).subscribe({
      next: () => {
        this.toast.success('Mašina odstránená');
        this.deleting.set(null);
        this.deleteBusy.set(false);
        this.load();
      },
      error: e => {
        this.toast.error(this.apiError.friendly(e, 'Mašinu sa nepodarilo odstrániť'));
        this.deleting.set(null);
        this.deleteBusy.set(false);
      }
    });
  }
}
