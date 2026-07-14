import { Component, signal, computed, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { ModalComponent } from '../../components/modal/modal.component';
import { EmptyStateComponent } from '../../components/empty-state/empty-state.component';
import { CarService, Car, CreateCar } from '../../services/car.service';
import { ToastService } from '../../services/toast.service';
import { ApiErrorService } from '../../services/api-error.service';
import { normaliseFile } from '../../utils/image-utils';

@Component({
  selector: 'app-cars',
  imports: [NavbarComponent, RouterLink, FormsModule, SpinnerComponent, ModalComponent, EmptyStateComponent],
  templateUrl: './cars.page.html'
})
export class CarsPage implements OnInit {
  cars = signal<Car[]>([]);
  loading = signal(true);
  showForm = signal(false);
  newCar: CreateCar = { name: '', licensePlate: '' };
  newCarPhoto: File | null = null;
  photoPreview = signal<string | null>(null);
  isDragOver = signal(false);

  /** Row pending delete confirmation (null = modal closed). */
  deleting = signal<Car | null>(null);
  deleteBusy = signal(false);

  deleteMessage = computed(() => {
    const car = this.deleting();
    return car
      ? `Natrvalo odstrániť vozidlo "${car.name}"? Záznamy dochádzky zostanú, ale budú bez vozidla.`
      : '';
  });

  constructor(private carService: CarService, private toast: ToastService, private apiError: ApiErrorService) {}

  ngOnInit() { this.load(); }

  load() {
    this.loading.set(true);
    this.carService.getAll().subscribe({
      next: cars => { this.cars.set(cars); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  cancelForm() {
    this.showForm.set(false);
    this.newCar = { name: '', licensePlate: '' };
    this.newCarPhoto = null;
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
    this.newCarPhoto = normalised;
    const reader = new FileReader();
    reader.onload = e => this.photoPreview.set(e.target?.result as string);
    reader.readAsDataURL(normalised);
  }

  onCreate() {
    this.carService.create({ name: this.newCar.name, licensePlate: this.newCar.licensePlate || undefined }).subscribe(car => {
      const finish = () => {
        this.cancelForm();
        this.toast.success('Vozidlo pridané');
        this.load();
      };
      if (this.newCarPhoto) {
        this.carService.uploadPhoto(car.id, this.newCarPhoto).subscribe({
          next: () => finish(),
          error: () => finish()
        });
      } else {
        finish();
      }
    });
  }

  onToggleActive(car: Car) {
    this.carService.toggleActive(car.id).subscribe(() => {
      this.toast.success(car.isActive ? 'Vozidlo deaktivované' : 'Vozidlo aktivované');
      this.load();
    });
  }

  onDelete(car: Car) {
    this.deleting.set(car);
  }

  confirmDelete() {
    const car = this.deleting();
    if (!car || this.deleteBusy()) return;
    this.deleteBusy.set(true);
    this.carService.delete(car.id).subscribe({
      next: () => {
        this.toast.success('Vozidlo odstránené');
        this.deleting.set(null);
        this.deleteBusy.set(false);
        this.load();
      },
      error: e => {
        this.toast.error(this.apiError.friendly(e, 'Vozidlo sa nepodarilo odstrániť'));
        this.deleting.set(null);
        this.deleteBusy.set(false);
      }
    });
  }
}
