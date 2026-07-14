import { Component, signal, computed, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe, DecimalPipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { ModalComponent } from '../../components/modal/modal.component';
import { TimeEntryService, TimeEntry } from '../../services/time-entry.service';
import { ApiErrorService } from '../../services/api-error.service';
import { ReportService } from '../../services/report.service';
import { EmployeeService, Employee } from '../../services/employee.service';
import { LocationService, Location } from '../../services/location.service';
import { CarService, Car } from '../../services/car.service';
import { ToastService } from '../../services/toast.service';
import { DatepickerDirective } from '../../directives/datepicker.directive';
import { HmPipe } from '../../pipes/hm.pipe';
import { normaliseFile, fileToDataUrl, compressImage } from '../../utils/image-utils';

@Component({
  selector: 'app-time-entries',
  imports: [NavbarComponent, FormsModule, DatePipe, DecimalPipe, HmPipe, DatepickerDirective, SpinnerComponent, ModalComponent],
  templateUrl: './time-entries.page.html'
})
export class TimeEntriesPage implements OnInit {
  entries = signal<TimeEntry[]>([]);
  loading = signal(true);
  employees = signal<Employee[]>([]);
  locations = signal<Location[]>([]);
  cars = signal<Car[]>([]);
  showAddForm = signal(false);
  editingEntry = signal<TimeEntry | null>(null);
  // Admin add/edit forms are hours-based (like the kiosk "log hours" flow),
  // not clockIn/clockOut timestamps. The backend still stores clockIn + clockOut,
  // so we back-calculate them on submit.
  newEntry = { employeeId: 0, locationId: 0, carId: 0, date: '', hoursWorked: 8, note: '' };
  editForm = { carId: 0, date: '', hoursWorked: 8, note: '' };
  from = '';
  to = '';
  filterEmployeeId = 0;
  filterLocationId = 0;
  dateRangeMode: 'month' | 'week' | 'custom' = 'month';

  /** Quick-pick hours presets shown as chips under the +/- control. */
  readonly hoursPresets = [0.5, 1, 2, 4, 5, 5.5, 6, 7, 7.5, 8, 9, 10];

  // Photo state — both forms support multiple photos.
  // For the edit form: existing saved photos live on `editingEntry().photoUrl` (comma-separated)
  // and are removed via DELETE /photo?url=...; staged-but-not-yet-uploaded photos live in
  // editPhotoFiles/Previews and are uploaded after the entry update succeeds.
  newPhotoFiles    = signal<File[]>([]);
  newPhotoPreviews = signal<string[]>([]);
  editPhotoFiles    = signal<File[]>([]);
  editPhotoPreviews = signal<string[]>([]);

  // Lightbox — supports prev/next across all photos of an entry
  lightboxPhotos = signal<string[]>([]);
  lightboxIdx    = signal(0);
  lightboxPhoto  = computed(() => this.lightboxPhotos()[this.lightboxIdx()] ?? null);

  totalHours = computed(() =>
    this.entries().reduce((sum, e) => sum + (e.hoursWorked ?? 0), 0)
  );

  /** Employees the admin is allowed to create NEW entries for.
   *  The filter dropdown still uses the full list so historical entries for
   *  deactivated employees remain searchable, but the add form must not offer
   *  inactive employees — the kiosk only resolves active employees by PIN,
   *  so any entry booked against an inactive employee is invisible to them. */
  activeEmployees = computed(() => this.employees().filter(e => e.isActive));

  constructor(
    private timeEntryService: TimeEntryService,
    private reportService: ReportService,
    private employeeService: EmployeeService,
    private locationService: LocationService,
    private carService: CarService,
    private toast: ToastService,
    private apiError: ApiErrorService,
    private route: ActivatedRoute
  ) {}

  ngOnInit() {
    this.setMonthRange();

    this.employeeService.getAll().subscribe(e => this.employees.set(e));
    this.locationService.getAll().subscribe(l => this.locations.set(l));
    this.carService.getAll().subscribe(c => this.cars.set(c.filter(x => x.isActive)));
    const params = this.route.snapshot.queryParams;
    if (params['employeeId']) this.filterEmployeeId = +params['employeeId'];
    if (params['locationId']) this.filterLocationId = +params['locationId'];
    this.load();
  }

  private fmtDate(d: Date): string {
    return `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
  }

  /** Set date range to the full current month. */
  private setMonthRange() {
    const today = new Date();
    const monthStart = new Date(today.getFullYear(), today.getMonth(), 1);
    const monthEnd = new Date(today.getFullYear(), today.getMonth() + 1, 0); // last day of month
    this.from = this.fmtDate(monthStart);
    this.to = this.fmtDate(monthEnd);
  }

  /** Set date range to the current week (Monday–Sunday). */
  private setWeekRange() {
    const today = new Date();
    const day = today.getDay();
    const diffToMonday = day === 0 ? -6 : 1 - day;
    const monday = new Date(today);
    monday.setDate(today.getDate() + diffToMonday);
    const sunday = new Date(monday);
    sunday.setDate(monday.getDate() + 6);
    this.from = this.fmtDate(monday);
    this.to = this.fmtDate(sunday);
  }

  /** Quick-switch between month / week / custom date range. */
  setDateRangeMode(mode: 'month' | 'week') {
    this.dateRangeMode = mode;
    if (mode === 'month') this.setMonthRange();
    else this.setWeekRange();
    this.load();
  }

  /** Called when the user manually changes the date inputs — switches to custom mode. */
  onDateManualChange() {
    this.dateRangeMode = 'custom';
    this.load();
  }

  private getFilters() {
    const filters: any = {};
    if (this.from) filters.from = this.from;
    if (this.to) filters.to = this.to;
    if (this.filterEmployeeId) filters.employeeId = this.filterEmployeeId;
    if (this.filterLocationId) filters.locationId = this.filterLocationId;
    return filters;
  }

  load() {
    this.loading.set(true);
    this.timeEntryService.getAll(this.getFilters()).subscribe({
      next: e => { this.entries.set(e); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  /** Busy flag shared by both export buttons — these subscriptions DO
   *  complete (blob download), so the state resets on real completion. */
  exporting = signal(false);

  exportCsv() {
    this.exporting.set(true);
    this.reportService.exportCsv(this.getFilters()).subscribe({
      next: blob => {
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = 'zaznamy-dochadzky.csv';
        a.click();
        window.URL.revokeObjectURL(url);
        this.exporting.set(false);
      },
      error: e => {
        this.exporting.set(false);
        this.toast.error(this.apiError.friendly(e, 'Export CSV zlyhal'));
      }
    });
  }

  exportXlsx() {
    const now = new Date();
    const pad = (n: number) => String(n).padStart(2, '0');
    const date = `${pad(now.getDate())}.${pad(now.getMonth() + 1)}.${now.getFullYear()}`;
    const fromDate = this.from ? new Date(this.from) : now;
    const month = `${pad(fromDate.getMonth() + 1)}.${fromDate.getFullYear()}`;
    this.exporting.set(true);
    this.reportService.exportXlsx(this.getFilters()).subscribe({
      next: blob => {
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `Stiahnuté-${date}_${month}.xlsx`;
        a.click();
        window.URL.revokeObjectURL(url);
        this.exporting.set(false);
      },
      error: e => {
        this.exporting.set(false);
        this.toast.error(this.apiError.friendly(e, 'Export Excel zlyhal'));
      }
    });
  }

  /** Entry pending delete confirmation (null = modal closed). */
  deletingEntry = signal<TimeEntry | null>(null);
  deleteBusy = signal(false);

  onDelete(entry: TimeEntry) {
    this.deletingEntry.set(entry);
  }

  confirmDeleteEntry() {
    const entry = this.deletingEntry();
    if (!entry || this.deleteBusy()) return;
    this.deleteBusy.set(true);
    this.timeEntryService.delete(entry.id).subscribe({
      next: () => {
        this.deleteBusy.set(false);
        this.deletingEntry.set(null);
        this.toast.success('Záznam dochádzky odstránený');
        this.load();
      },
      error: e => {
        this.deleteBusy.set(false);
        this.deletingEntry.set(null);
        this.toast.error(this.apiError.friendly(e, 'Záznam sa nepodarilo odstrániť'));
      }
    });
  }

  /** Parse a photoUrl that may be a single URL or comma-separated list of URLs. */
  parsePhotoUrls(photoUrl?: string | null): string[] {
    if (!photoUrl) return [];
    return photoUrl.split(',').map(u => u.trim()).filter(u => u.length > 0);
  }

  /** Returns the first URL from a possibly comma-separated photoUrl. */
  firstPhotoUrl(photoUrl?: string | null): string | null {
    const urls = this.parsePhotoUrls(photoUrl);
    return urls.length > 0 ? urls[0] : null;
  }

  openLightbox(photos: string[], startIdx = 0) {
    if (!photos.length) return;
    this.lightboxPhotos.set(photos);
    this.lightboxIdx.set(startIdx);
  }

  closeLightbox() {
    this.lightboxPhotos.set([]);
    this.lightboxIdx.set(0);
  }

  lightboxNext() {
    const len = this.lightboxPhotos().length;
    if (len < 2) return;
    this.lightboxIdx.set((this.lightboxIdx() + 1) % len);
  }

  lightboxPrev() {
    const len = this.lightboxPhotos().length;
    if (len < 2) return;
    this.lightboxIdx.set((this.lightboxIdx() - 1 + len) % len);
  }

  /** Multi-file picker handler for the add form. Each file is normalised + compressed
   *  in turn and appended to the staged previews. The same input can be used multiple
   *  times — value is reset each time so the same file can be re-selected if needed. */
  async onNewPhotosSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const raws = Array.from(input.files ?? []);
    input.value = '';
    if (!raws.length) return;
    for (const raw of raws) {
      const normalised = await normaliseFile(raw);
      const file = await compressImage(normalised);
      const preview = await fileToDataUrl(file);
      this.newPhotoFiles.update(arr => [...arr, file]);
      this.newPhotoPreviews.update(arr => [...arr, preview]);
    }
  }

  removeNewPhotoAt(idx: number) {
    this.newPhotoFiles.update(arr => arr.filter((_, i) => i !== idx));
    this.newPhotoPreviews.update(arr => arr.filter((_, i) => i !== idx));
  }

  /** Multi-file picker handler for the edit form. Adds to the *staged* list — saved
   *  photos on the entry are managed via onEditDeleteExistingPhoto(url). */
  async onEditPhotosSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const raws = Array.from(input.files ?? []);
    input.value = '';
    if (!raws.length) return;
    for (const raw of raws) {
      const normalised = await normaliseFile(raw);
      const file = await compressImage(normalised);
      const preview = await fileToDataUrl(file);
      this.editPhotoFiles.update(arr => [...arr, file]);
      this.editPhotoPreviews.update(arr => [...arr, preview]);
    }
  }

  removeEditStagedPhotoAt(idx: number) {
    this.editPhotoFiles.update(arr => arr.filter((_, i) => i !== idx));
    this.editPhotoPreviews.update(arr => arr.filter((_, i) => i !== idx));
  }

  /** Delete one already-saved photo from the entry being edited (server-side). */
  onEditDeleteExistingPhoto(url: string) {
    const entry = this.editingEntry();
    if (!entry) return;
    if (!confirm('Odstrániť túto fotku?')) return;
    this.timeEntryService.deletePhoto(entry.id, url).subscribe({
      next: () => {
        const remaining = this.parsePhotoUrls(entry.photoUrl).filter(u => u !== url);
        this.editingEntry.set({ ...entry, photoUrl: remaining.length ? remaining.join(',') : undefined });
        this.toast.success('Fotka odstránená');
        this.load();
      },
      error: () => this.toast.error('Fotku sa nepodarilo odstrániť')
    });
  }

  cancelAll() {
    if (this.showAddForm() || this.editingEntry()) {
      this.showAddForm.set(false);
      this.editingEntry.set(null);
      this.newPhotoFiles.set([]);
      this.newPhotoPreviews.set([]);
      this.editPhotoFiles.set([]);
      this.editPhotoPreviews.set([]);
    } else {
      const today = new Date();
      this.newEntry = { employeeId: 0, locationId: 0, carId: 0, date: this.fmtDate(today), hoursWorked: 8, note: '' };
      this.showAddForm.set(true);
    }
  }

  /** Adjust the staged hours for the add form by ±0.5h, clamped to [0.5, 24]. */
  adjustNewHours(delta: number) {
    const next = Math.round((this.newEntry.hoursWorked + delta) * 2) / 2;
    this.newEntry.hoursWorked = Math.min(24, Math.max(0.5, next));
  }
  setNewHours(h: number) {
    this.newEntry.hoursWorked = Math.min(24, Math.max(0.5, h));
  }

  /** Adjust the staged hours for the edit form by ±0.5h, clamped to [0.5, 24]. */
  adjustEditHours(delta: number) {
    const next = Math.round((this.editForm.hoursWorked + delta) * 2) / 2;
    this.editForm.hoursWorked = Math.min(24, Math.max(0.5, next));
  }
  setEditHours(h: number) {
    this.editForm.hoursWorked = Math.min(24, Math.max(0.5, h));
  }

  /**
   * Build clockIn/clockOut ISO-ish strings from a (date, hoursWorked) pair,
   * using the same convention as the kiosk log-hours endpoint:
   *   - today  → clockOut = now
   *   - past   → clockOut = date at 17:00
   * then clockIn = clockOut − hoursWorked.
   */
  private buildClockWindow(dateStr: string, hoursWorked: number): { clockIn: string; clockOut: string } {
    const [y, m, d] = dateStr.split('-').map(n => parseInt(n, 10));
    const todayStr = this.fmtDate(new Date());
    const isToday = dateStr === todayStr;
    const clockOut = isToday
      ? new Date()
      : new Date(y, (m ?? 1) - 1, d ?? 1, 17, 0, 0, 0);
    const clockIn = new Date(clockOut.getTime() - hoursWorked * 60 * 60 * 1000);
    const iso = (dt: Date) => {
      const p = (n: number) => String(n).padStart(2, '0');
      return `${dt.getFullYear()}-${p(dt.getMonth()+1)}-${p(dt.getDate())}T${p(dt.getHours())}:${p(dt.getMinutes())}:${p(dt.getSeconds())}`;
    };
    return { clockIn: iso(clockIn), clockOut: iso(clockOut) };
  }

  onCreate() {
    if (!this.newEntry.employeeId || !this.newEntry.locationId || !this.newEntry.date) return;
    if (!this.newEntry.hoursWorked || this.newEntry.hoursWorked <= 0) {
      this.toast.error('Zadajte počet hodín.');
      return;
    }
    const { clockIn, clockOut } = this.buildClockWindow(this.newEntry.date, this.newEntry.hoursWorked);
    const dto: any = {
      employeeId: this.newEntry.employeeId,
      locationId: this.newEntry.locationId,
      carId: this.newEntry.carId || undefined,
      clockIn,
      clockOut,
      note: this.newEntry.note || undefined
    };
    const photoFiles = this.newPhotoFiles();
    this.showAddForm.set(false);
    this.timeEntryService.create(dto).subscribe(async created => {
      // Upload each staged photo sequentially so the backend appends them in order.
      // If one upload fails we still try the rest — partial failure is better than total.
      for (const file of photoFiles) {
        try { await firstValueFrom(this.timeEntryService.uploadPhoto(created.id, file)); }
        catch { /* swallow individual upload error and continue */ }
      }
      this.newPhotoFiles.set([]);
      this.newPhotoPreviews.set([]);
      this.newEntry = { employeeId: 0, locationId: 0, carId: 0, date: '', hoursWorked: 8, note: '' };
      this.toast.success('Záznam dochádzky pridaný');
      this.load();
    });
  }

  onEdit(entry: TimeEntry) {
    // Derive the local date + total hours from the existing entry so the
    // manager sees the same value they'd enter on the kiosk.
    const clockInDate = new Date(entry.clockIn);
    const dateStr = this.fmtDate(clockInDate);
    const hours = entry.hoursWorked && entry.hoursWorked > 0
      ? Math.round(entry.hoursWorked * 2) / 2
      : 8;
    this.editForm = {
      carId: entry.carId ?? 0,
      date: dateStr,
      hoursWorked: hours,
      note: entry.note || ''
    };
    // Existing photos remain on the entry (`editingEntry().photoUrl`); start with no
    // staged additions.
    this.editPhotoFiles.set([]);
    this.editPhotoPreviews.set([]);
    this.editingEntry.set(entry);
    this.showAddForm.set(false);
  }

  onUpdate() {
    const entry = this.editingEntry();
    if (!entry || !this.editForm.date) return;
    if (!this.editForm.hoursWorked || this.editForm.hoursWorked <= 0) {
      this.toast.error('Zadajte počet hodín.');
      return;
    }
    const { clockIn, clockOut } = this.buildClockWindow(this.editForm.date, this.editForm.hoursWorked);
    const dto: any = {
      carId: this.editForm.carId || undefined,
      clockIn,
      clockOut,
      note: this.editForm.note || undefined
    };
    const photoFiles = this.editPhotoFiles();
    this.timeEntryService.update(entry.id, dto).subscribe(async () => {
      for (const file of photoFiles) {
        try { await firstValueFrom(this.timeEntryService.uploadPhoto(entry.id, file)); }
        catch { /* swallow individual upload error and continue */ }
      }
      this.editingEntry.set(null);
      this.editPhotoFiles.set([]);
      this.editPhotoPreviews.set([]);
      this.toast.success('Záznam dochádzky upravený');
      this.load();
    });
  }
}
