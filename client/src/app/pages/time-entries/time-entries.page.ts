import { Component, signal, computed, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe, DecimalPipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { TimeEntryService, TimeEntry } from '../../services/time-entry.service';
import { ReportService } from '../../services/report.service';
import { EmployeeService, Employee } from '../../services/employee.service';
import { LocationService, Location } from '../../services/location.service';
import { CarService, Car } from '../../services/car.service';
import { DatepickerDirective } from '../../directives/datepicker.directive';
import { HmPipe } from '../../pipes/hm.pipe';
import { normaliseFile, fileToDataUrl, compressImage } from '../../utils/image-utils';

@Component({
  selector: 'app-time-entries',
  imports: [NavbarComponent, FormsModule, DatePipe, DecimalPipe, HmPipe, DatepickerDirective],
  templateUrl: './time-entries.page.html'
})
export class TimeEntriesPage implements OnInit {
  entries = signal<TimeEntry[]>([]);
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

  // Photo state
  newPhotoFile = signal<File | null>(null);
  newPhotoPreview = signal<string | null>(null);
  editPhotoFile = signal<File | null>(null);
  editPhotoPreview = signal<string | null>(null);

  // Lightbox — supports prev/next across all photos of an entry
  lightboxPhotos = signal<string[]>([]);
  lightboxIdx    = signal(0);
  lightboxPhoto  = computed(() => this.lightboxPhotos()[this.lightboxIdx()] ?? null);

  totalHours = computed(() =>
    this.entries().reduce((sum, e) => sum + (e.hoursWorked ?? 0), 0)
  );

  constructor(
    private timeEntryService: TimeEntryService,
    private reportService: ReportService,
    private employeeService: EmployeeService,
    private locationService: LocationService,
    private carService: CarService,
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
    this.timeEntryService.getAll(this.getFilters()).subscribe(e => this.entries.set(e));
  }

  exportCsv() {
    this.reportService.exportCsv(this.getFilters()).subscribe(blob => {
      const url = window.URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'zaznamy-dochadzky.csv';
      a.click();
      window.URL.revokeObjectURL(url);
    });
  }

  exportXlsx() {
    const now = new Date();
    const pad = (n: number) => String(n).padStart(2, '0');
    const date = `${pad(now.getDate())}.${pad(now.getMonth() + 1)}.${now.getFullYear()}`;
    const fromDate = this.from ? new Date(this.from) : now;
    const month = `${pad(fromDate.getMonth() + 1)}.${fromDate.getFullYear()}`;
    this.reportService.exportXlsx(this.getFilters()).subscribe(blob => {
      const url = window.URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `Downloaded-${date}_${month}.xlsx`;
      a.click();
      window.URL.revokeObjectURL(url);
    });
  }

  onDelete(entry: TimeEntry) {
    if (confirm('Odstrániť záznam dochádzky pre ' + entry.employeeName + '?')) {
      this.timeEntryService.delete(entry.id).subscribe(() => this.load());
    }
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

  async onNewPhotoSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const raw = input.files?.[0];
    if (!raw) return;
    input.value = '';
    const normalised = await normaliseFile(raw);
    const file = await compressImage(normalised);
    this.newPhotoFile.set(file);
    this.newPhotoPreview.set(await fileToDataUrl(file));
  }

  removeNewPhoto() {
    this.newPhotoFile.set(null);
    this.newPhotoPreview.set(null);
  }

  async onEditPhotoSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const raw = input.files?.[0];
    if (!raw) return;
    input.value = '';
    const normalised = await normaliseFile(raw);
    const file = await compressImage(normalised);
    this.editPhotoFile.set(file);
    this.editPhotoPreview.set(await fileToDataUrl(file));
  }

  onEditDeletePhoto() {
    const entry = this.editingEntry();
    if (!entry) return;
    if (!confirm('Odstrániť foto z tohto záznamu?')) return;
    this.timeEntryService.deletePhoto(entry.id).subscribe(() => {
      this.editPhotoFile.set(null);
      this.editPhotoPreview.set(null);
      this.load();
    });
  }

  cancelAll() {
    if (this.showAddForm() || this.editingEntry()) {
      this.showAddForm.set(false);
      this.editingEntry.set(null);
      this.newPhotoFile.set(null);
      this.newPhotoPreview.set(null);
      this.editPhotoFile.set(null);
      this.editPhotoPreview.set(null);
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
      alert('Zadajte počet hodín.');
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
    const photoFile = this.newPhotoFile();
    this.showAddForm.set(false);
    this.timeEntryService.create(dto).subscribe(created => {
      const finish = () => {
        this.newPhotoFile.set(null);
        this.newPhotoPreview.set(null);
        this.newEntry = { employeeId: 0, locationId: 0, carId: 0, date: '', hoursWorked: 8, note: '' };
        this.load();
      };
      if (photoFile) {
        this.timeEntryService.uploadPhoto(created.id, photoFile).subscribe({ next: finish, error: finish });
      } else {
        finish();
      }
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
    this.editPhotoFile.set(null);
    this.editPhotoPreview.set(entry.photoUrl ?? null);
    this.editingEntry.set(entry);
    this.showAddForm.set(false);
  }

  onUpdate() {
    const entry = this.editingEntry();
    if (!entry || !this.editForm.date) return;
    if (!this.editForm.hoursWorked || this.editForm.hoursWorked <= 0) {
      alert('Zadajte počet hodín.');
      return;
    }
    const { clockIn, clockOut } = this.buildClockWindow(this.editForm.date, this.editForm.hoursWorked);
    const dto: any = {
      carId: this.editForm.carId || undefined,
      clockIn,
      clockOut,
      note: this.editForm.note || undefined
    };
    const photoFile = this.editPhotoFile();
    this.timeEntryService.update(entry.id, dto).subscribe(() => {
      const finish = () => {
        this.editingEntry.set(null);
        this.editPhotoFile.set(null);
        this.editPhotoPreview.set(null);
        this.load();
      };
      if (photoFile) {
        this.timeEntryService.uploadPhoto(entry.id, photoFile).subscribe({ next: finish, error: finish });
      } else {
        finish();
      }
    });
  }
}
