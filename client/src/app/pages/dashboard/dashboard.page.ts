import { Component, signal, OnInit } from '@angular/core';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { ReportService, DailyReport } from '../../services/report.service';
import { DatePipe, DecimalPipe } from '@angular/common';

@Component({
  selector: 'app-dashboard',
  imports: [NavbarComponent, DatePipe, DecimalPipe, SpinnerComponent],
  templateUrl: './dashboard.page.html'
})
export class DashboardPage implements OnInit {
  report = signal<DailyReport | null>(null);
  loading = signal(true);

  constructor(private reportService: ReportService) {}

  ngOnInit() {
    this.reportService.getDaily().subscribe({
      next: r => { this.report.set(r); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }
}
