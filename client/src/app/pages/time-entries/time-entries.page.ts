import { Component, signal, computed, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { TimeEntryService, TimeEntry } from '../../services/time-entry.service';
import { ReportService } from '../../services/report.service';
import { EmployeeService, Employee } from '../../services/employee.service';
import { LocationService, Location } from '../../services/location.service';
import { CarService, Car } from '../../services/car.service';
import { DatepickerDirective } from '../../directives/datepicker.directive';
import { TimepickerDirective } from '../../directives/timepicker.directive';
import { HmPipe } from '../../pipes/hm.pipe';
import { normaliseFile, fileToDataUrl, compressImage } from '../../utils/image-utils';

@Component({
  selector: 'app-time-entries',
  imports: [NavbarComponent, FormsModule, DatePipe, HmPipe, DatepickerDirective, TimepickerDirective],
  templateUrl: './time-entries.page.html'
})
export class TimeEntriesPage implements OnInit {
  entries = signal<TimeEntry[]>([]);
  employees = signal<Employee[]>([]);
  locations = signal<Location[]>([]);
  cars = signal<Car[]>([]);
  showAddForm = signal(false);
  editingEntry = signal<TimeEntry | null>(null);
  newEntry = { employeeId: 0, locationId: 0, carId: 0, clockInDate: '', clockInTime: '', clockOutDate: '', clockOutTime: '', note: '' };
  editForm = { carId: 0, clockInDate: '', clockInTime: '', clockOutDate: '', clockOutTime: '', note: '' };
  from = '';
  to = '';
  filterEmployeeId = 0;
  filterLocationId = 0;

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
    const today = new Date();
    const monthStart = new Date(today.getFullYear(), today.getMonth(), 1);
    const fmt = (d: Date) => `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
    this.from = fmt(monthStart);
    this.to = fmt(today);

    this.employeeService.getAll().subscribe(e => this.employees.set(e));
    this.locationService.getAll().subscribe(l => this.locations.set(l));
    this.carService.getAll().subscribe(c => this.cars.set(c.filter(x => x.isActive)));
    const params = this.route.snapshot.queryParams;
    if (params['employeeId']) this.filterEmployeeId = +params['employeeId'];
    if (params['locationId']) this.filterLocationId = +params['locationId'];
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
    this.reportService.exportXlsx(this.getFilters()).subscribe(blob => {
      const url = window.URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'zaznamy-dochadzky.xlsx';
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
      const fmt = (d: Date) => `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
      const fmtTime = (d: Date) => `${String(d.getHours()).padStart(2,'0')}:${String(d.getMinutes()).padStart(2,'0')}`;
      this.newEntry = { employeeId: 0, locationId: 0, carId: 0, clockInDate: fmt(today), clockInTime: fmtTime(today), clockOutDate: '', clockOutTime: '', note: '' };
      this.showAddForm.set(true);
    }
  }

  onCreate() {
    if (!this.newEntry.employeeId || !this.newEntry.locationId || !this.newEntry.clockInDate) return;
    const dto: any = {
      employeeId: this.newEntry.employeeId,
      locationId: this.newEntry.locationId,
      carId: this.newEntry.carId || undefined,
      clockIn: `${this.newEntry.clockInDate}T${this.newEntry.clockInTime || '00:00'}:00`,
      note: this.newEntry.note || undefined
    };
    if (this.newEntry.clockOutDate) {
      dto.clockOut = `${this.newEntry.clockOutDate}T${this.newEntry.clockOutTime || '00:00'}:00`;
      if (dto.clockOut <= dto.clockIn) { alert('Odchod musí byť po príchode.'); return; }
    }
    const photoFile = this.newPhotoFile();
    this.showAddForm.set(false);
    this.timeEntryService.create(dto).subscribe(created => {
      const finish = () => {
        this.newPhotoFile.set(null);
        this.newPhotoPreview.set(null);
        this.newEntry = { employeeId: 0, locationId: 0, carId: 0, clockInDate: '', clockInTime: '', clockOutDate: '', clockOutTime: '', note: '' };
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
    const toLocal = (iso: string) => {
      const d = new Date(iso);
      const p = (n: number) => n.toString().padStart(2, '0');
      return `${d.getFullYear()}-${p(d.getMonth()+1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
    };
    const [ciDate, ciTime] = toLocal(entry.clockIn).split('T');
    const [coDate, coTime] = entry.clockOut ? toLocal(entry.clockOut).split('T') : ['', ''];
    this.editForm = { carId: entry.carId ?? 0, clockInDate: ciDate, clockInTime: ciTime, clockOutDate: coDate, clockOutTime: coTime, note: entry.note || '' };
    this.editPhotoFile.set(null);
    this.editPhotoPreview.set(entry.photoUrl ?? null);
    this.editingEntry.set(entry);
    this.showAddForm.set(false);
  }

  onUpdate() {
    const entry = this.editingEntry();
    if (!entry || !this.editForm.clockInDate) return;
    const dto: any = {
      carId: this.editForm.carId || undefined,
      clockIn: `${this.editForm.clockInDate}T${this.editForm.clockInTime || '00:00'}:00`,
      note: this.editForm.note || undefined
    };
    if (this.editForm.clockOutDate) {
      dto.clockOut = `${this.editForm.clockOutDate}T${this.editForm.clockOutTime || '00:00'}:00`;
      if (dto.clockOut <= dto.clockIn) { alert('Odchod musí byť po príchode.'); return; }
    }
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
