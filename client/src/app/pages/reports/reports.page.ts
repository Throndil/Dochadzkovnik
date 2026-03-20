import { Component, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { ReportService } from '../../services/report.service';
import { TimeEntry } from '../../services/time-entry.service';
import { EmployeeService, Employee } from '../../services/employee.service';
import { LocationService, Location } from '../../services/location.service';
import { DatepickerDirective } from '../../directives/datepicker.directive';
import { HmPipe } from '../../pipes/hm.pipe';

@Component({
  selector: 'app-reports',
  imports: [NavbarComponent, FormsModule, DatePipe, HmPipe, DatepickerDirective],
  templateUrl: './reports.page.html'
})
export class ReportsPage implements OnInit {
  entries = signal<TimeEntry[]>([]);
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
    private locationService: LocationService
  ) {}

  ngOnInit() {
    const today = new Date();
    const weekAgo = new Date(today);
    weekAgo.setDate(weekAgo.getDate() - 7);
    this.from = weekAgo.toISOString().split('T')[0];
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
    this.reportService.getSummary(this.getFilters()).subscribe(entries => {
      this.entries.set(entries);
      this.totalHours.set(
        entries.reduce((sum, e) => sum + (e.hoursWorked ?? 0), 0)
      );
    });
  }

  exportCsv() {
    this.reportService.exportCsv(this.getFilters()).subscribe(blob => {
      const url = window.URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'time-report.csv';
      a.click();
      window.URL.revokeObjectURL(url);
    });
  }
}
