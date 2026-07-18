import { Component, signal, computed, OnInit, inject } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { LocationManagePanelComponent } from '../../components/location-manage-panel/location-manage-panel.component';
import { LocationService, Location, LocationPhoto, LocationPnl, DailyLogDay } from '../../services/location.service';
import { normaliseFile, compressImage, cloudinaryThumb } from '../../utils/image-utils';
import { TimeEntryService, TimeEntry } from '../../services/time-entry.service';
import { MaterialService, MaterialUsage } from '../../services/material.service';
import { WorkDiaryService, WorkDiary } from '../../services/work-diary.service';
import { FeatureFlagService } from '../../services/feature-flag.service';
import { AuthService } from '../../services/auth.service';
import { ToastService } from '../../services/toast.service';
import { ApiErrorService } from '../../services/api-error.service';
import { MonthPickerComponent } from '../../components/month-picker/month-picker.component';

export interface PhotoGroup {
  key: string;            // unique: date__employeeName
  employeeName: string;
  date: string;           // ISO date string from API
  photos: LocationPhoto[];
}

@Component({
  selector: 'app-location-detail',
  imports: [NavbarComponent, FormsModule, DatePipe, SpinnerComponent, LocationManagePanelComponent, RouterLink, MonthPickerComponent],
  templateUrl: './location-detail.page.html'
})
export class LocationDetailPage implements OnInit {
  location = signal<Location | null>(null);

  /** Location shown in the material-management slide-over (null = closed).
   *  The same panel the Pracoviská list opens via "Spravovať" — available
   *  here too so managing materials doesn't require going back to the list. */
  manageLocation = signal<Location | null>(null);

  onManageMaterial() { this.manageLocation.set(this.location()); }
  onManagePanelClose() {
    this.manageLocation.set(null);
    this.loadMaterials();   // the panel may have changed this month's usage
  }
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
      this.toast.error('Odstránenie fotky zlyhalo. Skúste znova.');
    };
    const afterDelete = () => {
      this.toast.success('Fotka odstránená');
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

  // ─── Materials used at this Pracovisko ──────────────────────────
  private materialService = inject(MaterialService);
  materials = signal<MaterialUsage[]>([]);
  materialsLoading = signal(false);

  /** Sum of LineCost across the loaded materials list. */
  materialsTotalCost = computed(() => this.materials().reduce((s, m) => s + (m.lineCost || 0), 0));

  loadMaterials() {
    const ym = this.galleryMonth();
    const [y, m] = ym.split('-').map(Number);
    const lastDay = new Date(y, m, 0).getDate();
    const from = `${ym}-01`;
    const to   = `${ym}-${String(lastDay).padStart(2, '0')}`;
    this.materialsLoading.set(true);
    this.materialService.getUsages(this.id, from, to).subscribe({
      next: rows => { this.materials.set(rows); this.materialsLoading.set(false); },
      error: () => this.materialsLoading.set(false),
    });
  }

  // ─── Worker hours summary at this Pracovisko ────────────────────
  hoursEntries = signal<TimeEntry[]>([]);
  hoursLoading = signal(false);

  /** Aggregate hours by employee. Open entries (no clockOut) are skipped. */
  hoursByEmployee = computed(() => {
    const map = new Map<number, { name: string; photoUrl?: string; hours: number; shifts: number }>();
    for (const t of this.hoursEntries()) {
      if (t.hoursWorked == null) continue;
      const row = map.get(t.employeeId) ?? { name: t.employeeName, photoUrl: t.employeePhotoUrl, hours: 0, shifts: 0 };
      row.hours += t.hoursWorked;
      row.shifts += 1;
      map.set(t.employeeId, row);
    }
    return Array.from(map.entries())
      .map(([id, v]) => ({ employeeId: id, ...v }))
      .sort((a, b) => b.hours - a.hours);
  });

  hoursTotal = computed(() => this.hoursByEmployee().reduce((s, r) => s + r.hours, 0));

  loadHours() {
    const ym = this.galleryMonth();
    const [y, m] = ym.split('-').map(Number);
    const lastDay = new Date(y, m, 0).getDate();
    const from = `${ym}-01`;
    const to   = `${ym}-${String(lastDay).padStart(2, '0')}`;
    this.hoursLoading.set(true);
    this.timeEntryService.getAll({ from, to, locationId: this.id }).subscribe({
      next: rows => { this.hoursEntries.set(rows); this.hoursLoading.set(false); },
      error: () => this.hoursLoading.set(false),
    });
  }

  // Format helpers shared by the two new sections.
  formatMoney(v: number): string {
    return new Intl.NumberFormat('sk-SK', { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(v);
  }
  formatQty(v: number): string {
    return new Intl.NumberFormat('sk-SK', { maximumFractionDigits: 3 }).format(v);
  }
  formatHours(v: number): string {
    return new Intl.NumberFormat('sk-SK', { minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(v);
  }

  // ─── Stavebný denník section (ProofOfWorkChoices flag) ────────────
  flags = inject(FeatureFlagService);
  private workDiaryService = inject(WorkDiaryService);
  diaries = signal<WorkDiary[]>([]);
  diariesLoading = signal(false);
  /** Id of the diary whose body is currently expanded. Null = all collapsed. */
  expandedDiaryId = signal<number | null>(null);

  toggleDiary(id: number) {
    this.expandedDiaryId.set(this.expandedDiaryId() === id ? null : id);
  }

  loadDiaries() {
    if (!this.flags.proofOfWorkChoices()) return;
    const ym = this.galleryMonth();           // "YYYY-MM"
    const from = `${ym}-01`;
    // Use the JS Date math to compute the last day of the month robustly.
    const [y, m] = ym.split('-').map(Number);
    const lastDay = new Date(y, m, 0).getDate();
    const to = `${ym}-${String(lastDay).padStart(2, '0')}`;
    this.diariesLoading.set(true);
    this.workDiaryService.list({ from, to, locationId: this.id }).then(rows => {
      this.diaries.set(rows);
      this.diariesLoading.set(false);
    }).catch(() => this.diariesLoading.set(false));
  }

  deleteDiary(d: WorkDiary) {
    if (!confirm(`Odstrániť záznam z denníka zo dňa ${new Date(d.date).toLocaleDateString('sk-SK')}?`)) return;
    this.workDiaryService.delete(d.id).then(() => {
      this.diaries.update(arr => arr.filter(x => x.id !== d.id));
      if (this.expandedDiaryId() === d.id) this.expandedDiaryId.set(null);
      this.toast.success('Záznam z denníka odstránený');
    }).catch(() => this.toast.error('Záznam sa nepodarilo odstrániť'));
  }

  // ─── Náklady a zisk / P&L (PayrollAndPnL flag) ──────────────────
  auth = inject(AuthService);
  private toast = inject(ToastService);
  private apiError = inject(ApiErrorService);
  /** Card renders only for the flag or superadmin — same gate as the Mzdy link. */
  pnlVisible = computed(() => this.flags.payrollAndPnL() || this.auth.isSuperAdmin());
  pnl = signal<LocationPnl | null>(null);
  pnlLoading = signal(false);
  /** Collapsible breakdowns: collapsed by default on mobile, expanded on md+. */
  labourExpanded = signal(typeof window === 'undefined' || window.innerWidth >= 768);
  materialExpanded = signal(typeof window === 'undefined' || window.innerWidth >= 768);

  // Inline Zmluvná hodnota edit
  editingContract = signal(false);
  contractDraft = '';
  savingContract = signal(false);

  /** Profit margin in % (profit / revenue). Null when profit or revenue is missing. */
  pnlMargin = computed(() => {
    const p = this.pnl();
    if (!p || p.profit == null || !p.revenue) return null;
    return Math.round((p.profit / p.revenue) * 100);
  });

  loadPnl() {
    if (!this.pnlVisible()) return;
    const ym = this.galleryMonth();
    const [y, m] = ym.split('-').map(Number);
    const lastDay = new Date(y, m, 0).getDate();
    const from = `${ym}-01`;
    const to   = `${ym}-${String(lastDay).padStart(2, '0')}`;
    this.pnlLoading.set(true);
    this.locationService.getPnl(this.id, from, to).subscribe({
      next: data => { this.pnl.set(data); this.pnlLoading.set(false); },
      error: () => this.pnlLoading.set(false),
    });
  }

  downloadPnlExcel() {
    const ym = this.galleryMonth();
    const [y, m] = ym.split('-').map(Number);
    const lastDay = new Date(y, m, 0).getDate();
    const from = `${ym}-01`;
    const to   = `${ym}-${String(lastDay).padStart(2, '0')}`;
    this.locationService.downloadPnlExcel(this.id, from, to, this.location()?.name ?? 'pracovisko');
  }

  startContractEdit() {
    const v = this.pnl()?.location.contractValue;
    this.contractDraft = v != null ? String(v) : '';
    this.editingContract.set(true);
  }

  cancelContractEdit() {
    this.editingContract.set(false);
  }

  saveContractValue() {
    const raw = this.contractDraft.trim().replace(/\s/g, '').replace(',', '.');
    const value = raw === '' ? null : Number(raw);
    if (value !== null && (isNaN(value) || value < 0)) {
      this.toast.error('Zadajte platnú sumu v €.');
      return;
    }
    this.savingContract.set(true);
    this.locationService.updateContractValue(this.id, value).subscribe({
      next: () => {
        this.savingContract.set(false);
        this.editingContract.set(false);
        this.toast.success('Zmluvná hodnota uložená');
        this.loadPnl();
      },
      error: () => {
        this.savingContract.set(false);
        this.toast.error('Uloženie zmluvnej hodnoty zlyhalo. Skúste znova.');
      }
    });
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
    this.loadDiaries();
    this.loadMaterials();
    this.loadHours();
    this.loadPnl();
    this.loadDailyLog();
  }

  // ─── Zložka pracoviska — denník podľa dátumu (P1) ───────────────
  /** Týždeň (default) | Deň | Vlastné. */
  logMode = signal<'week' | 'day' | 'range'>('week');
  /** Monday of the viewed week (YYYY-MM-DD). */
  logWeekStart = signal(this.mondayOf(new Date()));
  logDay = signal(this.isoToday());
  logFrom = signal(this.mondayOf(new Date()));
  logTo = signal(this.isoToday());
  logDays = signal<DailyLogDay[]>([]);
  logLoading = signal(false);
  /** Collapsed day keys (dates start expanded). */
  collapsedLogDays = signal<Set<string>>(new Set());

  logRangeLabel = computed(() => {
    const { from, to } = this.logRange();
    return from === to ? this.formatIsoDay(from) : `${this.formatIsoDay(from)} – ${this.formatIsoDay(to)}`;
  });

  private logRange(): { from: string; to: string } {
    switch (this.logMode()) {
      case 'day':   return { from: this.logDay(), to: this.logDay() };
      case 'range': return { from: this.logFrom(), to: this.logTo() };
      default: {
        const from = this.logWeekStart();
        const [y, m, d] = from.split('-').map(Number);
        return { from, to: this.isoDate(new Date(y, m - 1, d + 6)) };
      }
    }
  }

  loadDailyLog() {
    const { from, to } = this.logRange();
    if (!/^\d{4}-\d{2}-\d{2}$/.test(from) || !/^\d{4}-\d{2}-\d{2}$/.test(to) || from > to) return;
    this.logLoading.set(true);
    this.locationService.getDailyLog(this.id, from, to).subscribe({
      next: days => { this.logDays.set(days); this.logLoading.set(false); },
      error: () => this.logLoading.set(false),
    });
  }

  setLogMode(mode: 'week' | 'day' | 'range') {
    if (this.logMode() === mode) return;
    this.logMode.set(mode);
    this.loadDailyLog();
  }

  shiftLogWeek(delta: -1 | 1) {
    const [y, m, d] = this.logWeekStart().split('-').map(Number);
    this.logWeekStart.set(this.isoDate(new Date(y, m - 1, d + delta * 7)));
    this.loadDailyLog();
  }

  onLogDayChange(value: string) {
    this.logDay.set(value);
    this.loadDailyLog();
  }

  onLogRangeChange(which: 'from' | 'to', value: string) {
    (which === 'from' ? this.logFrom : this.logTo).set(value);
    this.loadDailyLog();
  }

  toggleLogDay(date: string) {
    this.collapsedLogDays.update(set => {
      const next = new Set(set);
      if (next.has(date)) next.delete(date); else next.add(date);
      return next;
    });
  }

  /** Slovak weekday + date, e.g. "piatok 18.07.2026". */
  logDayLabel(iso: string): string {
    const [y, m, d] = iso.split('T')[0].split('-').map(Number);
    const date = new Date(y, m - 1, d);
    const weekday = date.toLocaleDateString('sk-SK', { weekday: 'long' });
    return `${weekday} ${this.formatIsoDay(iso.split('T')[0])}`;
  }

  logDayHours(day: DailyLogDay): number {
    return day.shifts.reduce((s, x) => s + (x.hours ?? 0), 0);
  }

  private mondayOf(d: Date): string {
    return this.isoDate(new Date(d.getFullYear(), d.getMonth(), d.getDate() - ((d.getDay() + 6) % 7)));
  }
  private isoDate(d: Date): string {
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }
  private isoToday(): string {
    return this.isoDate(new Date());
  }
  private formatIsoDay(iso: string): string {
    const [y, m, d] = iso.split('-').map(Number);
    return `${String(d).padStart(2, '0')}.${String(m).padStart(2, '0')}.${y}`;
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
    this.locationService.bulkDeletePhotos(this.id, beforeDate).subscribe({
      next: count => {
        this.toast.success(`Odstránených ${count} fotiek.`);
        this.loadGallery();
      },
      error: () => this.toast.error('Hromadné odstránenie zlyhalo')
    });
  }

  onSave() {
    this.locationService.update(this.id, {
      name: this.name,
      address: this.address,
      isActive: this.isActive
    }).subscribe({
      next: () => {
        this.toast.success('Zmeny uložené');
        this.router.navigate(['/admin/locations']);
      },
      error: e => this.toast.error(this.apiError.friendly(e, 'Uloženie pracoviska zlyhalo'))
    });
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
