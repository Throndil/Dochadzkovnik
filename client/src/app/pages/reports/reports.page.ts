import { Component, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { AlertComponent } from '../../components/alert/alert.component';
import { EmptyStateComponent } from '../../components/empty-state/empty-state.component';
import { ReportService } from '../../services/report.service';
import { TimeEntry } from '../../services/time-entry.service';
import { EmployeeService, Employee } from '../../services/employee.service';
import { LocationService, Location } from '../../services/location.service';
import { ApiErrorService } from '../../services/api-error.service';
import { DatepickerDirective } from '../../directives/datepicker.directive';
import { HmPipe } from '../../pipes/hm.pipe';

@Component({
  selector: 'app-reports',
  imports: [NavbarComponent, FormsModule, DatePipe, HmPipe, DatepickerDirective, SpinnerComponent, AlertComponent, EmptyStateComponent],
  templateUrl: './reports.page.html'
})
export class ReportsPage implements OnInit {
  entries = signal<TimeEntry[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);
  /** Busy state for the CSV export button. */
  exporting = signal(false);
  employees = signal<Employee[]>([]);
  locations = signal<Location[]>([]);
  totalHours = signal(0);
  from = '';
  to = '';
  employeeId = 0;
  locationId = 0;

  constructor(
    private reportService: ReportService,
    private employeeService: EmployeeService,
    private locationService: LocationService,
    private apiError: ApiErrorService
  ) {}

  ngOnInit() {
    const today = new Date();
    const monthStart = new Date(today.getFullYear(), today.getMonth(), 1);
    this.from = monthStart.toISOString().split('T')[0];
    this.to = today.toISOString().split('T')[0];

    this.employeeService.getAll().subscribe(e => this.employees.set(e));
    this.locationService.getAll().subscribe(l => this.locations.set(l));
    this.load();
  }

  private getFilters() {
    const filters: any = {};
    if (this.from) filters.from = this.from;
    if (this.to) filters.to = this.to;
    if (this.employeeId) filters.employeeId = this.employeeId;
    if (this.locationId) filters.locationId = this.locationId;
    return filters;
  }

  load() {
    this.loading.set(true);
    this.error.set(null);
    this.reportService.getSummary(this.getFilters()).subscribe({
      next: entries => {
        this.entries.set(entries);
        this.totalHours.set(
          entries.reduce((sum, e) => sum + (e.hoursWorked ?? 0), 0)
        );
        this.loading.set(false);
      },
      error: e => {
        this.error.set(this.apiError.friendly(e, 'Načítanie reportu zlyhalo'));
        this.loading.set(false);
      },
    });
  }

  exportCsv() {
    this.exporting.set(true);
    this.reportService.exportCsv(this.getFilters()).subscribe(blob => {
      const url = window.URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'vykaz-hodin.csv';
      a.click();
      window.URL.revokeObjectURL(url);
    });
    // The download has no completion callback the busy state could reliably
    // hook into (the subscribe above never fires on error), so clear it after
    // a bounded ~4s delay — a bounded visual busy beats a permanently dead
    // button.
    setTimeout(() => this.exporting.set(false), 4000);
  }
}
