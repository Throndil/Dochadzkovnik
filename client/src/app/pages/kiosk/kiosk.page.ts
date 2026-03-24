import { Component, signal, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { KioskService, KioskResponse, KioskStatus } from '../../services/kiosk.service';
import { TimeEntry } from '../../services/time-entry.service';
import { Location } from '../../services/location.service';
import { DatepickerDirective } from '../../directives/datepicker.directive';
import { HmPipe } from '../../pipes/hm.pipe';

@Component({
  selector: 'app-kiosk',
  imports: [FormsModule, DatePipe, HmPipe, DatepickerDirective],
  templateUrl: './kiosk.page.html'
})
export class KioskPage implements OnInit {
  pin = '';
  selectedLocationId = 0;
  comment = '';
  mode = signal<'live' | 'manual' | 'my-hours'>('live');
  showPin = signal(false);
  locations = signal<Location[]>([]);
  response = signal<KioskResponse | null>(null);
  responseError = signal(false);
  status = signal<KioskStatus | null>(null);
  loading = signal(false);

  // Manual entry fields
  manualLocationId = 0;
  manualFrom = '';
  manualTo = '';
  manualNote = '';

  // My Hours fields
  myHoursFrom = '';
  myHoursTo = '';
  myHoursEntries = signal<TimeEntry[]>([]);
  myHoursTotalHours = signal(0);
  myHoursEmployeeName = signal('');
  myHoursLoaded = signal(false);

  constructor(private kioskService: KioskService) {}

  ngOnInit() {
    const today = new Date();
    const monday = new Date(today);
    const daysToMonday = today.getDay() === 0 ? 6 : today.getDay() - 1;
    monday.setDate(today.getDate() - daysToMonday);
    const fmt = (d: Date) => `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
    const fmtTime = (d: Date) => `${String(d.getHours()).padStart(2,'0')}:${String(d.getMinutes()).padStart(2,'0')}`;
    this.myHoursFrom = fmt(monday);
    this.myHoursTo = fmt(today);
    this.manualFrom = `${fmt(today)}T06:00`;
    this.manualTo = `${fmt(today)}T${fmtTime(today)}`;
    this.kioskService.getLocations().subscribe(locs => this.locations.set(locs));
  }

  setMode(m: 'live' | 'manual' | 'my-hours') {
    this.mode.set(m);
    this.pin = '';
    this.selectedLocationId = 0;
    this.comment = '';
    this.manualLocationId = 0;
    this.manualNote = '';
    this.response.set(null);
    this.responseError.set(false);
    this.status.set(null);
    this.myHoursLoaded.set(false);
  }

  canSubmit() {
    return this.pin.length >= 4 && this.selectedLocationId > 0;
  }

  canClockOut() {
    return this.pin.length >= 4 && !!this.comment.trim();
  }

  canSubmitManual() {
    return this.pin.length >= 4 && this.manualLocationId > 0 && !!this.manualFrom && !!this.manualTo && this.manualFrom < this.manualTo && !!this.manualNote.trim();
  }

  onPinChange() {
    this.response.set(null);
    this.status.set(null);
    if (this.pin.length >= 4) {
      this.kioskService.getStatus(this.pin).subscribe({
        next: s => this.status.set(s),
        error: () => this.status.set(null)
      });
    }
  }

  clockIn() {
    this.loading.set(true);
    this.kioskService.clockIn(this.pin, this.selectedLocationId).subscribe({
      next: res => {
        this.response.set(res);
        this.responseError.set(false);
        this.loading.set(false);
        this.resetAfterDelay();
      },
      error: (err) => {
        this.response.set({ message: err.error || 'Prihlásenie zlyhalo', employeeName: '', timestamp: new Date().toISOString() });
        this.responseError.set(true);
        this.loading.set(false);
      }
    });
  }

  clockOut() {
    this.loading.set(true);
    this.kioskService.clockOut(this.pin, this.comment || undefined).subscribe({
      next: res => {
        this.response.set(res);
        this.responseError.set(false);
        this.loading.set(false);
        this.resetAfterDelay();
      },
      error: (err) => {
        this.response.set({ message: err.error || 'Odhlásenie zlyhalo', employeeName: '', timestamp: new Date().toISOString() });
        this.responseError.set(true);
        this.loading.set(false);
      }
    });
  }

  private resetAfterDelay() {
    setTimeout(() => {
      this.pin = '';
      this.selectedLocationId = 0;
      this.comment = '';
      this.manualLocationId = 0;
      this.manualFrom = '';
      this.manualTo = '';
      this.manualNote = '';
      this.response.set(null);
      this.status.set(null);
    }, 5000);
  }

  loadMyHours() {
    if (this.pin.length < 4 || !this.myHoursFrom || !this.myHoursTo) return;
    this.loading.set(true);
    this.kioskService.getMyHours(this.pin, this.myHoursFrom, this.myHoursTo).subscribe({
      next: entries => {
        this.myHoursEntries.set(entries);
        this.myHoursTotalHours.set(entries.reduce((s, e) => s + (e.hoursWorked ?? 0), 0));
        this.myHoursEmployeeName.set(entries.length > 0 ? entries[0].employeeName : '');
        this.myHoursLoaded.set(true);
        this.loading.set(false);
      },
      error: () => {
        this.myHoursEntries.set([]);
        this.myHoursLoaded.set(true);
        this.loading.set(false);
      }
    });
  }

  submitManual() {
    this.loading.set(true);
    this.kioskService.manualEntry(
      this.pin, this.manualLocationId, this.manualFrom, this.manualTo,
      this.manualNote || undefined
    ).subscribe({
      next: res => {
        this.response.set(res);
        this.responseError.set(false);
        this.loading.set(false);
        this.resetAfterDelay();
      },
      error: (err) => {
        this.response.set({ message: err.error || 'Manual entry failed', employeeName: '', timestamp: new Date().toISOString() });
        this.responseError.set(true);
        this.loading.set(false);
      }
    });
  }
}
