import { Component, signal, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { LocationService, Location } from '../../services/location.service';

@Component({
  selector: 'app-location-detail',
  imports: [NavbarComponent, FormsModule],
  templateUrl: './location-detail.page.html'
})
export class LocationDetailPage implements OnInit {
  location = signal<Location | null>(null);
  name = '';
  address = '';
  isActive = true;
  photoPreview = signal<string | null>(null);
  isDragOver = signal(false);
  private id = 0;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private locationService: LocationService
  ) {}

  ngOnInit() {
    this.id = Number(this.route.snapshot.paramMap.get('id'));
    this.locationService.get(this.id).subscribe(loc => {
      this.location.set(loc);
      this.name = loc.name;
      this.address = loc.address ?? '';
      this.isActive = loc.isActive;
      this.photoPreview.set(loc.photoUrl ?? null);
    });
  }

  onSave() {
    this.locationService.update(this.id, {
      name: this.name,
      address: this.address,
      isActive: this.isActive
    }).subscribe(() => this.router.navigate(['/admin/locations']));
  }

  onFileSelected(event: Event) {
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
    this.locationService.uploadPhoto(this.id, resized).subscribe(url => {
      this.location.update(l => l ? { ...l, photoUrl: url } : l);
      this.photoPreview.set(url);
    });
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

  onBack() {
    this.router.navigate(['/admin/locations']);
  }
}
