import { Component, signal, OnInit } from '@angular/core';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { ReportService, DailyReport } from '../../services/report.service';
import { DatePipe, DecimalPipe } from '@angular/common';

@Component({
  selector: 'app-dashboard',
  imports: [NavbarComponent, DatePipe, DecimalPipe],
  templateUrl: './dashboard.page.html'
})
export class DashboardPage implements OnInit {
  report = signal<DailyReport | null>(null);

  constructor(private reportService: ReportService) {}

  ngOnInit() {
    this.reportService.getDaily().subscribe(r => this.report.set(r));
  }
}
