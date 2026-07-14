import { Component, signal, OnInit } from '@angular/core';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { AlertComponent } from '../../components/alert/alert.component';
import { ReportService, DailyReport } from '../../services/report.service';
import { ApiErrorService } from '../../services/api-error.service';
import { DatePipe, DecimalPipe } from '@angular/common';

@Component({
  selector: 'app-dashboard',
  imports: [NavbarComponent, DatePipe, DecimalPipe, SpinnerComponent, AlertComponent],
  templateUrl: './dashboard.page.html'
})
export class DashboardPage implements OnInit {
  report = signal<DailyReport | null>(null);
  loading = signal(true);
  error = signal<string | null>(null);

  constructor(private reportService: ReportService, private apiError: ApiErrorService) {}

  ngOnInit() {
    this.error.set(null);
    this.reportService.getDaily().subscribe({
      next: r => { this.report.set(r); this.loading.set(false); },
      error: e => {
        this.error.set(this.apiError.friendly(e, 'Načítanie prehľadu zlyhalo'));
        this.loading.set(false);
      },
    });
  }
}
