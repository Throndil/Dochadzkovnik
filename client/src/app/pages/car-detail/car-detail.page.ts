import { Component, signal, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { CarService } from '../../services/car.service';
import { normaliseFile } from '../../utils/image-utils';

@Component({
  selector: 'app-car-detail',
  imports: [NavbarComponent, FormsModule],
  templateUrl: './car-detail.page.html'
})
export class CarDetailPage implements OnInit {
  car = signal<any>(null);
  name = '';
  licensePlate = '';
  isActive = true;
  saved = signal(false);

  photoPreview = signal<string | null>(null);
  isDragOver = signal(false);
  private pendingPhoto: File | null = null;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private carService: CarService
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
    const id = this.car().id;
    const save$ = this.carService.update(id, {
      name: this.name,
      licensePlate: this.licensePlate || undefined,
      isActive: this.isActive
    });

    if (this.pendingPhoto) {
      const file = this.pendingPhoto;
      save$.subscribe(() => {
        this.carService.uploadPhoto(id, file).subscribe(url => {
          this.car.update((c: any) => ({ ...c, photoUrl: url }));
          this.pendingPhoto = null;
          this.saved.set(true);
          setTimeout(() => this.saved.set(false), 2000);
        });
      });
    } else {
      save$.subscribe(() => {
        this.saved.set(true);
        setTimeout(() => this.saved.set(false), 2000);
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
