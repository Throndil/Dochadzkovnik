import { Component, signal, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { LocationService, Location, CreateLocation } from '../../services/location.service';

@Component({
  selector: 'app-locations',
  imports: [NavbarComponent, RouterLink, FormsModule],
  templateUrl: './locations.page.html'
})
export class LocationsPage implements OnInit {
  locations = signal<Location[]>([]);
  showForm = signal(false);
  newLocation: CreateLocation = { name: '', address: '' };
  newLocationPhoto: File | null = null;
  photoPreview = signal<string | null>(null);
  isDragOver = signal(false);

  constructor(private locationService: LocationService) {}

  ngOnInit() {
    this.load();
  }

  load() {
    this.locationService.getAll().subscribe(locs => this.locations.set(locs));
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
    this.locationService.toggleActive(loc.id).subscribe(() => this.load());
  }

  onHardDelete(loc: Location) {
    if (confirm('Natrvalo odstrániť pracovisko ' + loc.name + ' a VŠETKY jeho záznamy dochádzky? Toto sa nedá vrátiť.')) {
      this.locationService.hardDelete(loc.id).subscribe(() => this.load());
    }
  }
}
