import { Component, signal, computed, OnInit, inject } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { LocationService, Location, CreateLocation } from '../../services/location.service';
import { MaterialService } from '../../services/material.service';
import { ToastService } from '../../services/toast.service';
import { LocationManagePanelComponent } from '../../components/location-manage-panel/location-manage-panel.component';

@Component({
  selector: 'app-locations',
  imports: [NavbarComponent, RouterLink, FormsModule, LocationManagePanelComponent, SpinnerComponent, NgTemplateOutlet],
  templateUrl: './locations.page.html'
})
export class LocationsPage implements OnInit {
  locations = signal<Location[]>([]);
  loading = signal(true);

  /** Active sites always visible; the customer's deactivated ones collapse
   *  behind a counter toggle so the page stays scannable. */
  showInactive = signal(false);
  activeLocations = computed(() => this.locations().filter(l => l.isActive));
  inactiveLocations = computed(() => this.locations().filter(l => !l.isActive));
  showForm = signal(false);
  newLocation: CreateLocation = { name: '', address: '' };
  newLocationPhoto: File | null = null;
  photoPreview = signal<string | null>(null);
  isDragOver = signal(false);

  // The location currently shown in the manage slide-over panel (null = closed)
  manageLocation = signal<Location | null>(null);

  // ─── Cross-Pracoviská export ─────────────────────────────────────
  private materialService = inject(MaterialService);
  /** Year-month filter for the export, e.g. "2026-05". Empty = whole history. */
  exportMonth = signal<string>(this.currentYearMonth());

  /** Trigger the all-locations Excel download. Snake_case to match SK preferences. */
  onExportAll() {
    const ym = this.exportMonth();
    if (ym) {
      const [y, m] = ym.split('-').map(Number);
      const lastDay = new Date(y, m, 0).getDate();
      this.materialService.downloadAllLocationsExcel(`${ym}-01`, `${ym}-${String(lastDay).padStart(2, '0')}`);
    } else {
      this.materialService.downloadAllLocationsExcel();
    }
  }

  private currentYearMonth(): string {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
  }

  private toast = inject(ToastService);

  constructor(private locationService: LocationService) {}

  onManage(loc: Location) { this.manageLocation.set(loc); }
  onPanelClose()           { this.manageLocation.set(null); }

  ngOnInit() {
    this.load();
  }

  load() {
    this.loading.set(true);
    this.locationService.getAll().subscribe({
      next: locs => { this.locations.set(locs); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  cancelForm() {
    this.showForm.set(false);
    this.newLocation = { name: '', address: '' };
    this.newLocationPhoto = null;
    this.photoPreview.set(null);
    this.isDragOver.set(false);
  }

  onPhotoSelected(event: Event) {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (file) this.processPhotoFile(file);
  }

  onDragOver(event: DragEvent) {
    event.preventDefault();
    this.isDragOver.set(true);
  }

  onDragLeave() {
    this.isDragOver.set(false);
  }

  onDrop(event: DragEvent) {
    event.preventDefault();
    this.isDragOver.set(false);
    const file = event.dataTransfer?.files?.[0];
    if (file) this.processPhotoFile(file);
  }

  async processPhotoFile(file: File): Promise<void> {
    if (!file.type.startsWith('image/')) return;
    const resized = await this.resizeImage(file);
    this.newLocationPhoto = resized;
    const reader = new FileReader();
    reader.onload = e => this.photoPreview.set(e.target?.result as string);
    reader.readAsDataURL(resized);
  }

  private resizeImage(file: File, maxDimension = 800): Promise<File> {
    return new Promise(resolve => {
      const img = new Image();
      const objectUrl = URL.createObjectURL(file);
      img.onload = () => {
        URL.revokeObjectURL(objectUrl);
        const scale = Math.min(1, maxDimension / Math.max(img.width, img.height));
        const w = Math.round(img.width * scale);
        const h = Math.round(img.height * scale);
        const canvas = document.createElement('canvas');
        canvas.width = w;
        canvas.height = h;
        canvas.getContext('2d')!.drawImage(img, 0, 0, w, h);
        canvas.toBlob(
          blob => resolve(new File([blob!], file.name.replace(/\.[^.]+$/, '.jpg'), { type: 'image/jpeg' })),
          'image/jpeg',
          0.85
        );
      };
      img.src = objectUrl;
    });
  }

  onCreate() {
    this.locationService.create(this.newLocation).subscribe(loc => {
      const finish = () => {
        this.cancelForm();
        this.toast.success('Pracovisko pridané');
        this.load();
      };
      if (this.newLocationPhoto) {
        this.locationService.uploadPhoto(loc.id, this.newLocationPhoto).subscribe({
          next: () => finish(),
          error: () => finish()   // close and reload even if photo upload fails
        });
      } else {
        finish();
      }
    });
  }

  onToggleActive(loc: Location) {
    this.locationService.toggleActive(loc.id).subscribe(() => {
      this.toast.success(loc.isActive ? 'Pracovisko deaktivované' : 'Pracovisko aktivované');
      this.load();
    });
  }

  onHardDelete(loc: Location) {
    if (confirm('Natrvalo odstrániť pracovisko ' + loc.name + ' a VŠETKY jeho záznamy dochádzky? Toto sa nedá vrátiť.')) {
      this.locationService.hardDelete(loc.id).subscribe({
        next: () => { this.toast.success('Pracovisko odstránené'); this.load(); },
        error: () => this.toast.error('Pracovisko sa nepodarilo odstrániť')
      });
    }
  }
}
