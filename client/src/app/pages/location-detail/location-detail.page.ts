import { Component, signal, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { LocationService, Location, LocationPhoto } from '../../services/location.service';
import { normaliseFile, compressImage, cloudinaryThumb } from '../../utils/image-utils';
import { TimeEntryService } from '../../services/time-entry.service';
import { environment } from '../../../environments/environment';

@Component({
  selector: 'app-location-detail',
  imports: [NavbarComponent, FormsModule, DatePipe],
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

  // Gallery
  galleryMonth = this.currentYearMonth();
  galleryPhotos = signal<LocationPhoto[]>([]);
  galleryLoading = signal(false);
  galleryLightbox = signal<string | null>(null);
  galleryUploadLoading = signal(false);
  readonly thumb = cloudinaryThumb;

  get galleryDownloadUrl(): string {
    const ym = this.galleryMonth;
    return `${environment.apiUrl}/locations/${this.id}/photos/download?from=${ym}&to=${ym}`;
  }

  private currentYearMonth(): string {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
  }

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private locationService: LocationService,
    private timeEntryService: TimeEntryService
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
    this.loadGallery();
  }

  loadGallery() {
    this.galleryLoading.set(true);
    const ym = this.galleryMonth;
    this.locationService.getPhotos(this.id, ym, ym).subscribe({
      next: photos => { this.galleryPhotos.set(photos); this.galleryLoading.set(false); },
      error: () => this.galleryLoading.set(false)
    });
  }

  deleteGalleryPhoto(photo: LocationPhoto, event: Event) {
    event.stopPropagation();
    if (!confirm('Odstrániť túto fotku?')) return;
    if (photo.workPhotoId) {
      // Standalone work photo — delete via /api/work-photos/{id}
      this.locationService.deleteWorkPhoto(photo.workPhotoId).subscribe(() => this.loadGallery());
    } else if (photo.timeEntryId) {
      // Photo attached to a time entry — remove via /api/time-entries/{id}/photo
      this.timeEntryService.deletePhoto(photo.timeEntryId).subscribe(() => this.loadGallery());
    }
  }

  bulkDeleteBefore() {
    const beforeDate = prompt('Odstrániť fotky pred dátumom (RRRR-MM-DD):', new Date().toISOString().split('T')[0]);
    if (!beforeDate) return;
    if (!confirm(`Odstrániť všetky fotky z tohto pracoviska pred ${beforeDate}?`)) return;
    this.locationService.bulkDeletePhotos(this.id, beforeDate).subscribe(count => {
      alert(`Odstránených ${count} fotiek.`);
      this.loadGallery();
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
    if (!file.type.startsWith('image/') && !file.name.match(/\.(heic|heif)$/i)) return;
    const normalised = await normaliseFile(file);   // HEIC → PNG before canvas decode
    const compressed = await compressImage(normalised, 1200, 0.72);
    this.locationService.uploadPhoto(this.id, compressed).subscribe(url => {
      this.location.update(l => l ? { ...l, photoUrl: url } : l);
      this.photoPreview.set(url);
    });
  }

  async onGalleryFileSelected(event: Event): Promise<void> {
    const file = (event.target as HTMLInputElement).files?.[0];
    (event.target as HTMLInputElement).value = '';
    if (!file) return;
    if (!file.type.startsWith('image/') && !file.name.match(/\.(heic|heif)$/i)) return;
    this.galleryUploadLoading.set(true);
    try {
      const normalised = await normaliseFile(file);
      const compressed = await compressImage(normalised, 1200, 0.72);
      this.locationService.uploadGalleryPhoto(this.id, compressed).subscribe({
        next: () => { this.galleryUploadLoading.set(false); this.loadGallery(); },
        error: () => this.galleryUploadLoading.set(false)
      });
    } catch {
      this.galleryUploadLoading.set(false);
    }
  }

  onBack() {
    this.router.navigate(['/admin/locations']);
  }
}
