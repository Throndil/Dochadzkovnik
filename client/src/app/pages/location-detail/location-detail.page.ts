import { Component, signal, computed, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { LocationService, Location, LocationPhoto } from '../../services/location.service';
import { normaliseFile, compressImage, cloudinaryThumb } from '../../utils/image-utils';
import { TimeEntryService } from '../../services/time-entry.service';

export interface PhotoGroup {
  key: string;            // unique: date__employeeName
  employeeName: string;
  date: string;           // ISO date string from API
  photos: LocationPhoto[];
}

@Component({
  selector: 'app-location-detail',
  imports: [NavbarComponent, FormsModule, DatePipe, SpinnerComponent],
  templateUrl: './location-detail.page.html'
})
export class LocationDetailPage implements OnInit {
  location = signal<Location | null>(null);
  name = '';
  address = '';
  isActive = true;
  photoPreview = signal<string | null>(null);
  isDragOver = signal(false);
  id = 0;

  // Gallery
  galleryMonth = signal(this.currentYearMonth());
  galleryPhotos = signal<LocationPhoto[]>([]);
  galleryLoading = signal(false);
  galleryUploadLoading = signal(false);
  readonly thumb = cloudinaryThumb;

  /** Photos grouped by date + employee. Each group renders as one stack card. */
  galleryGroups = computed<PhotoGroup[]>(() => {
    const map = new Map<string, PhotoGroup>();
    for (const p of this.galleryPhotos()) {
      // Normalise the date to YYYY-MM-DD so photos from the same day group together
      const day = typeof p.date === 'string' ? p.date.split('T')[0] : new Date(p.date).toISOString().split('T')[0];
      const key = `${day}__${p.employeeName}`;
      if (!map.has(key)) map.set(key, { key, employeeName: p.employeeName, date: day, photos: [] });
      map.get(key)!.photos.push(p);
    }
    return Array.from(map.values()).sort((a, b) => {
      const d = b.date.localeCompare(a.date);
      return d !== 0 ? d : a.employeeName.localeCompare(b.employeeName);
    });
  });

  // Group-scoped lightbox
  activeLightboxGroup = signal<PhotoGroup | null>(null);
  activeLightboxIdx   = signal(0);

  readonly activeLightboxPhoto = computed<LocationPhoto | null>(() => {
    const g = this.activeLightboxGroup();
    return g ? g.photos[this.activeLightboxIdx()] : null;
  });

  openGroupLightbox(group: PhotoGroup, startIndex = 0) {
    this.activeLightboxGroup.set(group);
    this.activeLightboxIdx.set(startIndex);
  }

  closeGroupLightbox() {
    this.activeLightboxGroup.set(null);
    this.activeLightboxIdx.set(0);
  }

  groupLightboxNext() {
    const g = this.activeLightboxGroup();
    if (!g) return;
    this.activeLightboxIdx.update(i => (i + 1) % g.photos.length);
  }

  groupLightboxPrev() {
    const g = this.activeLightboxGroup();
    if (!g) return;
    this.activeLightboxIdx.update(i => (i - 1 + g.photos.length) % g.photos.length);
  }

  deleteActiveLightboxPhoto(event: Event) {
    event.stopPropagation();
    const photo = this.activeLightboxPhoto();
    const group = this.activeLightboxGroup();
    if (!photo || !group) return;
    if (!confirm('Odstrániť túto fotku?')) return;

    const handleError = (err: unknown) => {
      console.error('Delete photo failed', err);
      alert('Odstránenie fotky zlyhalo. Skúste znova.');
    };
    const afterDelete = () => {
      // Optimistically remove the deleted photo so the lightbox updates immediately
      // (loadGallery below will sync with fresh server data once the HTTP call returns)
      const remaining = group.photos.filter(p => p !== photo);
      if (remaining.length === 0) {
        this.closeGroupLightbox();
      } else {
        this.activeLightboxGroup.set({ ...group, photos: remaining });
        this.activeLightboxIdx.set(Math.min(this.activeLightboxIdx(), remaining.length - 1));
      }
      this.loadGallery();
    };

    if (photo.workPhotoId) {
      this.locationService.deleteWorkPhoto(photo.workPhotoId).subscribe({ next: afterDelete, error: handleError });
    } else if (photo.timeEntryId) {
      this.timeEntryService.deletePhoto(photo.timeEntryId, photo.photoUrl).subscribe({ next: afterDelete, error: handleError });
    }
  }

  private currentYearMonth(): string {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
  }

  // locationService is public so the template can call downloadPhotosZip() directly
  constructor(
    private route: ActivatedRoute,
    private router: Router,
    public  locationService: LocationService,
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
    const ym = this.galleryMonth();
    this.locationService.getPhotos(this.id, ym, ym).subscribe({
      next: photos => {
        this.galleryPhotos.set(photos);
        this.galleryLoading.set(false);
        // Re-sync the open lightbox group with the freshly-fetched data.
        // This keeps the lightbox correct after any add/delete operation.
        const currentGroup = this.activeLightboxGroup();
        if (currentGroup) {
          const updated = this.galleryGroups().find(g => g.key === currentGroup.key);
          if (updated) {
            this.activeLightboxGroup.set(updated);
            if (this.activeLightboxIdx() >= updated.photos.length)
              this.activeLightboxIdx.set(Math.max(0, updated.photos.length - 1));
          } else {
            this.closeGroupLightbox(); // whole group was removed
          }
        }
      },
      error: () => this.galleryLoading.set(false)
    });
  }

  bulkDeleteBefore() {
    const beforeDate = prompt('Odstrániť fotky pred dátumom (RRRR-MM-DD):', new Date().toISOString().split('T')[0]);
    if (!beforeDate) return;
    if (!confirm(
      `⚠️ Pred odstránením si stiahnite archív (ZIP) — fotky budú nenávratne vymazané aj z úložiska.\n\n` +
      `Odstrániť všetky fotky z tohto pracoviska pred ${beforeDate}?`
    )) return;
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
    const normalised = await normaliseFile(file);
    const compressed = await compressImage(normalised, 1200, 0.72);
    // Show local preview immediately so the user sees instant feedback
    const reader = new FileReader();
    reader.onload = e => this.photoPreview.set(e.target?.result as string);
    reader.readAsDataURL(compressed);
    // Upload and replace preview with the final Cloudinary URL
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

    // If the admin is browsing a past month, ask when the photo was taken so it is
    // filed under the correct date (backwards compatibility).
    let takenAt: string | undefined;
    const currentYM = this.currentYearMonth();
    if (this.galleryMonth() !== currentYM) {
      // Default prompt value: first day of the selected month
      const defaultDate = `${this.galleryMonth()}-01`;
      const input = prompt(
        `Nahrávate fotku do mesiaca ${this.galleryMonth()}.\nKedy bola fotka urobená? (RRRR-MM-DD)`,
        defaultDate
      );
      if (input === null) return; // admin cancelled — abort upload
      takenAt = input.trim() || defaultDate;
    }

    this.galleryUploadLoading.set(true);
    try {
      const normalised = await normaliseFile(file);
      const compressed = await compressImage(normalised, 1200, 0.72);
      this.locationService.uploadGalleryPhoto(this.id, compressed, takenAt).subscribe({
        next: () => {
          this.galleryUploadLoading.set(false);
          // Switch gallery view to the month where the photo was stored, then reload
          const targetMonth = takenAt ? takenAt.substring(0, 7) : currentYM;
          this.galleryMonth.set(targetMonth);
          this.loadGallery();
        },
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
