import { Component, signal, computed, OnInit } from '@angular/core';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { AlertComponent } from '../../components/alert/alert.component';
import { ReportService, DailyReport } from '../../services/report.service';
import { ApiErrorService } from '../../services/api-error.service';
import { AuthService } from '../../services/auth.service';
import { DatePipe, DecimalPipe } from '@angular/common';
import { tagTint, nameInitials } from '../../utils/tag-color';

@Component({
  selector: 'app-dashboard',
  imports: [NavbarComponent, DatePipe, DecimalPipe, SpinnerComponent, AlertComponent],
  templateUrl: './dashboard.page.html'
})
export class DashboardPage implements OnInit {
  report = signal<DailyReport | null>(null);
  loading = signal(true);
  error = signal<string | null>(null);

  readonly today = new Date();
  tagTint = tagTint;
  initials = nameInitials;

  greeting = computed(() => {
    const h = this.today.getHours();
    const base = h < 9 ? 'Dobré ráno' : h < 18 ? 'Dobrý deň' : 'Dobrý večer';
    const name = this.auth.displayName().trim().split(/\s+/)[0];
    return name ? `${base}, ${name}` : base;
  });

  constructor(private reportService: ReportService, private apiError: ApiErrorService, private auth: AuthService) {}

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
