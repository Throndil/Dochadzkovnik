import { Component, signal, OnInit } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { forkJoin } from 'rxjs';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { EmployeeService, Employee, CreateEmployee } from '../../services/employee.service';
import { TimeEntryService } from '../../services/time-entry.service';
import { DatepickerDirective } from '../../directives/datepicker.directive';
import { HmPipe } from '../../pipes/hm.pipe';

export interface EmployeeHours {
  employeeId: number;
  name: string;
  totalHours: number;
  entryCount: number;
}

@Component({
  selector: 'app-employees',
  imports: [NavbarComponent, RouterLink, FormsModule, HmPipe, DatepickerDirective, SpinnerComponent],
  templateUrl: './employees.page.html'
})
export class EmployeesPage implements OnInit {
  employees = signal<Employee[]>([]);
  loading = signal(true);
  showForm = signal(false);
  newEmployee: CreateEmployee = { firstName: '', lastName: '', pin: '', phoneNumber: '', address: '', city: '' };
  newEmployeePhoto: File | null = null;
  photoPreview = signal<string | null>(null);
  isDragOver = signal(false);

  summaryFrom = '';
  summaryTo = '';
  hoursSummary = signal<EmployeeHours[]>([]);
  summaryLoading = signal(true);
  summaryTotalHours = signal(0);
  summaryTotalEntries = signal(0);

  constructor(
    private employeeService: EmployeeService,
    private timeEntryService: TimeEntryService
  ) {}

  ngOnInit() {
    const today = new Date();
    const monday = new Date(today);
    const daysToMonday = today.getDay() === 0 ? 6 : today.getDay() - 1;
    monday.setDate(today.getDate() - daysToMonday);
    const sunday = new Date(monday);
    sunday.setDate(monday.getDate() + 6);
    const fmt = (d: Date) => `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
    this.summaryFrom = fmt(monday);
    this.summaryTo = fmt(sunday);
    this.load();
    this.loadSummary();
  }

  load() {
    this.loading.set(true);
    this.employeeService.getAll().subscribe({
      next: emps => { this.employees.set(emps); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  cancelForm() {
    this.showForm.set(false);
    this.newEmployee = { firstName: '', lastName: '', pin: '', phoneNumber: '', address: '', city: '' };
    this.newEmployeePhoto = null;
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
    this.newEmployeePhoto = resized;
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
    this.employeeService.create(this.newEmployee).subscribe({
      next: emp => {
        const finish = () => {
          this.cancelForm();
          this.load();
          this.loadSummary();
        };
        if (this.newEmployeePhoto) {
          this.employeeService.uploadPhoto(emp.id, this.newEmployeePhoto).subscribe(finish);
        } else {
          finish();
        }
      },
      error: err => {
        if (err.status === 409) {
          alert('Tento PIN je už priradený inému zamestnancovi. Prosím zvoľte iný PIN.');
        }
      }
    });
  }

  generatePin() {
    this.employeeService.generateUniquePin().subscribe(result => this.newEmployee.pin = result.pin);
  }

  onSetPin(emp: Employee) {
    const pin = prompt(`Nový PIN pre ${emp.firstName} ${emp.lastName} (4-6 číslic):`);
    if (pin === null) return;
    if (!/^\d{4,6}$/.test(pin)) { alert('PIN musí mať 4-6 číslic (iba čísla).'); return; }
    this.employeeService.setPin(emp.id, pin).subscribe({
      error: err => {
        if (err.status === 409) {
          alert('Tento PIN je už priradený inému zamestnancovi. Prosím zvoľte iný PIN.');
        }
      }
    });
  }

  onToggleActive(emp: Employee) {
    this.employeeService.toggleActive(emp.id).subscribe(() => {
      this.load();
      this.loadSummary();
    });
  }

  onHardDelete(emp: Employee) {
    if (confirm('Natrvalo odstrániť ' + emp.firstName + ' ' + emp.lastName + ' a VŠETKY ich záznamy dochádzky? Toto sa nedá vrátiť.')) {
      this.employeeService.hardDelete(emp.id).subscribe(() => this.load());
    }
  }

  loadSummary() {
    this.summaryLoading.set(true);
    const filters: any = {};
    if (this.summaryFrom) filters.from = this.summaryFrom;
    if (this.summaryTo) filters.to = this.summaryTo;
    forkJoin({
      entries: this.timeEntryService.getAll(filters),
      employees: this.employeeService.getAll()
    }).subscribe({
      next: ({ entries, employees }) => {
        const activeIds = new Set(employees.filter(e => e.isActive).map(e => e.id));
        const map = new Map<number, EmployeeHours>();
        for (const e of entries) {
          if (!activeIds.has(e.employeeId)) continue;
          if (!map.has(e.employeeId)) {
            map.set(e.employeeId, { employeeId: e.employeeId, name: e.employeeName, totalHours: 0, entryCount: 0 });
          }
          const row = map.get(e.employeeId)!;
          row.totalHours += e.hoursWorked ?? 0;
          row.entryCount++;
        }
        const rows = [...map.values()].sort((a, b) => a.name.localeCompare(b.name));
        this.hoursSummary.set(rows);
        this.summaryTotalHours.set(rows.reduce((s, r) => s + r.totalHours, 0));
        this.summaryTotalEntries.set(rows.reduce((s, r) => s + r.entryCount, 0));
        this.summaryLoading.set(false);
      },
      error: () => this.summaryLoading.set(false),
    });
  }
}
