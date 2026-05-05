import { Component, signal, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { CarService, Car, CreateCar } from '../../services/car.service';
import { normaliseFile } from '../../utils/image-utils';

@Component({
  selector: 'app-cars',
  imports: [NavbarComponent, RouterLink, FormsModule, SpinnerComponent],
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

  constructor(private carService: CarService) {}

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
    this.carService.toggleActive(car.id).subscribe(() => this.load());
  }

  onDelete(car: Car) {
    if (confirm(`Natrvalo odstrániť vozidlo "${car.name}"? Záznamy dochádzky zostanú, ale budú bez vozidla.`)) {
      this.carService.delete(car.id).subscribe(() => this.load());
    }
  }
}
