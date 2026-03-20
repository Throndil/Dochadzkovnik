import { Component, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { TimeEntryService, TimeEntry } from '../../services/time-entry.service';
import { EmployeeService, Employee } from '../../services/employee.service';
import { LocationService, Location } from '../../services/location.service';
import { DatepickerDirective } from '../../directives/datepicker.directive';
import { HmPipe } from '../../pipes/hm.pipe';

@Component({
  selector: 'app-time-entries',
  imports: [NavbarComponent, FormsModule, DatePipe, HmPipe, DatepickerDirective],
  templateUrl: './time-entries.page.html'
})
export class TimeEntriesPage implements OnInit {
  entries = signal<TimeEntry[]>([]);
  employees = signal<Employee[]>([]);
  locations = signal<Location[]>([]);
  showAddForm = signal(false);
  editingEntry = signal<TimeEntry | null>(null);
  newEntry = { employeeId: 0, locationId: 0, clockInDate: '', clockInTime: '', clockOutDate: '', clockOutTime: '', note: '' };
  editForm = { clockInDate: '', clockInTime: '', clockOutDate: '', clockOutTime: '', note: '' };
  from = '';
  to = '';
  filterEmployeeId = 0;
  filterLocationId = 0;

  constructor(
    private timeEntryService: TimeEntryService,
    private employeeService: EmployeeService,
    private locationService: LocationService,
    private route: ActivatedRoute
  ) {}

  ngOnInit() {
    const today = new Date();
    const monday = new Date(today);
    const daysToMonday = today.getDay() === 0 ? 6 : today.getDay() - 1;
    monday.setDate(today.getDate() - daysToMonday);
    const sunday = new Date(monday);
    sunday.setDate(monday.getDate() + 6);
    const fmt = (d: Date) => `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
    this.from = fmt(monday);
    this.to = fmt(sunday);

    this.employeeService.getAll().subscribe(e => this.employees.set(e));
    this.locationService.getAll().subscribe(l => this.locations.set(l));
    const params = this.route.snapshot.queryParams;
    if (params['employeeId']) this.filterEmployeeId = +params['employeeId'];
    if (params['locationId']) this.filterLocationId = +params['locationId'];
    this.load();
  }

  load() {
    const filters: any = {};
    if (this.from) filters.from = this.from;
    if (this.to) filters.to = this.to;
    if (this.filterEmployeeId) filters.employeeId = this.filterEmployeeId;
    if (this.filterLocationId) filters.locationId = this.filterLocationId;
    this.timeEntryService.getAll(filters).subscribe(e => this.entries.set(e));
  }

  onDelete(entry: TimeEntry) {
    if (confirm('Odstrániť záznam dochádzky pre ' + entry.employeeName + '?')) {
      this.timeEntryService.delete(entry.id).subscribe(() => this.load());
    }
  }

  cancelAll() {
    if (this.showAddForm() || this.editingEntry()) {
      this.showAddForm.set(false);
      this.editingEntry.set(null);
    } else {
      const today = new Date();
      const fmt = (d: Date) => `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
      const fmtTime = (d: Date) => `${String(d.getHours()).padStart(2,'0')}:${String(d.getMinutes()).padStart(2,'0')}`;
      this.newEntry = { employeeId: 0, locationId: 0, clockInDate: fmt(today), clockInTime: fmtTime(today), clockOutDate: '', clockOutTime: '', note: '' };
      this.showAddForm.set(true);
    }
  }

  onCreate() {
    if (!this.newEntry.employeeId || !this.newEntry.locationId || !this.newEntry.clockInDate) return;
    const dto: any = {
      employeeId: this.newEntry.employeeId,
      locationId: this.newEntry.locationId,
      clockIn: `${this.newEntry.clockInDate}T${this.newEntry.clockInTime || '00:00'}:00`,
      note: this.newEntry.note || undefined
    };
    if (this.newEntry.clockOutDate) {
      dto.clockOut = `${this.newEntry.clockOutDate}T${this.newEntry.clockOutTime || '00:00'}:00`;
      if (dto.clockOut <= dto.clockIn) { alert('Odchod musí byť po príchode.'); return; }
    }
    this.timeEntryService.create(dto).subscribe(() => {
      this.showAddForm.set(false);
      this.newEntry = { employeeId: 0, locationId: 0, clockInDate: '', clockInTime: '', clockOutDate: '', clockOutTime: '', note: '' };
      this.load();
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
    this.editForm = { clockInDate: ciDate, clockInTime: ciTime, clockOutDate: coDate, clockOutTime: coTime, note: entry.note || '' };
    this.editingEntry.set(entry);
    this.showAddForm.set(false);
  }

  onUpdate() {
    const entry = this.editingEntry();
    if (!entry || !this.editForm.clockInDate) return;
    const dto: any = {
      clockIn: `${this.editForm.clockInDate}T${this.editForm.clockInTime || '00:00'}:00`,
      note: this.editForm.note || undefined
    };
    if (this.editForm.clockOutDate) {
      dto.clockOut = `${this.editForm.clockOutDate}T${this.editForm.clockOutTime || '00:00'}:00`;
      if (dto.clockOut <= dto.clockIn) { alert('Odchod musí byť po príchode.'); return; }
    }
    this.timeEntryService.update(entry.id, dto).subscribe(() => {
      this.editingEntry.set(null);
      this.load();
    });
  }
}
