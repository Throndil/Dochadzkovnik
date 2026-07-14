import { Component, signal, OnInit } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { CommanderCarPanelComponent } from '../../components/commander-car-panel/commander-car-panel.component';
import { AlertComponent } from '../../components/alert/alert.component';
import { CarService } from '../../services/car.service';
import { ApiErrorService } from '../../services/api-error.service';
import { normaliseFile } from '../../utils/image-utils';

@Component({
  selector: 'app-car-detail',
  imports: [NavbarComponent, CommanderCarPanelComponent, FormsModule, RouterLink, AlertComponent],
  templateUrl: './car-detail.page.html'
})
export class CarDetailPage implements OnInit {
  car = signal<any>(null);
  name = '';
  licensePlate = '';
  isActive = true;
  saved = signal(false);
  /** Save/photo failure — the audit found this page failed silently. */
  error = signal<string | null>(null);

  photoPreview = signal<string | null>(null);
  isDragOver = signal(false);
  private pendingPhoto: File | null = null;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private carService: CarService,
    private apiError: ApiErrorService
  ) {}

  ngOnInit() {
    const id = Number(this.route.snapshot.paramMap.get('id'));
    this.carService.get(id).subscribe(c => {
      this.car.set(c);
      this.name = c.name;
      this.licensePlate = c.licensePlate ?? '';
      this.isActive = c.isActive;
      if (c.photoUrl) this.photoPreview.set(c.photoUrl);
    });
  }

  onSave() {
    if (!this.car()) return;
    this.error.set(null);
    const id = this.car().id;
    const save$ = this.carService.update(id, {
      name: this.name,
      licensePlate: this.licensePlate || undefined,
      isActive: this.isActive
    });
    const fail = (e: any, context: string) => this.error.set(this.apiError.friendly(e, context));

    if (this.pendingPhoto) {
      const file = this.pendingPhoto;
      save$.subscribe({
        next: () => {
          this.carService.uploadPhoto(id, file).subscribe({
            next: url => {
              this.car.update((c: any) => ({ ...c, photoUrl: url }));
              this.pendingPhoto = null;
              this.saved.set(true);
              setTimeout(() => this.saved.set(false), 2000);
            },
            error: e => fail(e, 'Nahranie fotografie zlyhalo')
          });
        },
        error: e => fail(e, 'Uloženie vozidla zlyhalo')
      });
    } else {
      save$.subscribe({
        next: () => {
          this.saved.set(true);
          setTimeout(() => this.saved.set(false), 2000);
        },
        error: e => fail(e, 'Uloženie vozidla zlyhalo')
      });
    }
  }

  onFileSelected(event: Event) {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (file) normaliseFile(file).then(f => this.setPhoto(f));
  }

  onDragOver(event: DragEvent) {
    event.preventDefault();
    this.isDragOver.set(true);
  }

  onDragLeave() { this.isDragOver.set(false); }

  onDrop(event: DragEvent) {
    event.preventDefault();
    this.isDragOver.set(false);
    const file = event.dataTransfer?.files[0];
    if (file && (file.type.startsWith('image/') || file.name.match(/\.(heic|heif)$/i)))
      normaliseFile(file).then(f => this.setPhoto(f));
  }

  private setPhoto(file: File) {
    this.pendingPhoto = file;
    const reader = new FileReader();
    reader.onload = e => this.photoPreview.set(e.target?.result as string);
    reader.readAsDataURL(file);
  }

  onBack() { this.router.navigate(['/admin/cars']); }
}
