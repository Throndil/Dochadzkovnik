import { Component, signal, OnInit, OnDestroy, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe, DecimalPipe } from '@angular/common';
import { KioskService, KioskResponse, KioskStatus, WeeklyOverview, WeeklyRow } from '../../services/kiosk.service';
import { TimeEntry } from '../../services/time-entry.service';
import { Location } from '../../services/location.service';
import { Car } from '../../services/car.service';
import { DatepickerDirective } from '../../directives/datepicker.directive';
import { HmPipe } from '../../pipes/hm.pipe';

type View = 'main' | 'manual' | 'my-hours';
type ClockStep = 'pin' | 'location' | 'car' | 'hours' | 'result';

@Component({
  selector: 'app-kiosk',
  imports: [FormsModule, DatePipe, DecimalPipe, HmPipe, DatepickerDirective],
  templateUrl: './kiosk.page.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class KioskPage implements OnInit, OnDestroy {

  // ─── Main view ──────────────────────────────────────────────────
  view = signal<View>('main');
  overview = signal<WeeklyOverview | null>(null);
  overviewLoading = signal(false);
  weekStart = signal<Date>(this.getMonday(new Date()));
  currentTime = signal(new Date());
  locations = signal<Location[]>([]);
  cars = signal<Car[]>([]);

  // ─── Clock-in/out flow (modal) ───────────────────────────────────
  clockStep = signal<ClockStep | null>(null);
  selectedEmployee = signal<WeeklyRow | null>(null);
  pin = '';
  pinDisplay = signal<string[]>([]); // masked dots array
  pinError = signal('');
  status = signal<KioskStatus | null>(null);
  selectedLocation = signal<Location | null>(null);
  selectedCar = signal<Car | null | 'none'>('none'); // null = not chosen yet, 'none' = no car
  hoursWorked = 8.0;
  selectedDate = '';   // YYYY-MM-DD, defaults to today when location is picked
  comment = '';
  response = signal<KioskResponse | null>(null);
  responseError = signal(false);
  loading = signal(false);

  // ─── Manual entry ────────────────────────────────────────────────
  manualPin = '';
  showManualPin = signal(false);
  manualLocationId = 0;
  manualFrom = '';
  manualTo = '';
  manualNote = '';
  manualResponse = signal<KioskResponse | null>(null);
  manualResponseError = signal(false);
  manualLoading = signal(false);

  // ─── My Hours ────────────────────────────────────────────────────
  myHoursPin = '';
  showMyHoursPin = signal(false);
  myHoursFrom = '';
  myHoursTo = '';
  myHoursEntries = signal<TimeEntry[]>([]);
  myHoursTotalHours = signal(0);
  myHoursEmployeeName = signal('');
  myHoursLoaded = signal(false);
  myHoursLoading = signal(false);

  readonly numpadDigits = ['1','2','3','4','5','6','7','8','9'];
  readonly pinSlots = [0,1,2,3,4,5];

  private clockTimeout?: ReturnType<typeof setTimeout>;
  private resetTimer?: ReturnType<typeof setTimeout>;

  constructor(private kioskService: KioskService) {}

  ngOnInit() {
    this.initDateDefaults();
    this.loadOverview();
    this.kioskService.getLocations().subscribe(locs => this.locations.set(locs));
    this.kioskService.getCars().subscribe(cars => this.cars.set(cars));
    this.scheduleTick();
  }

  /** Self-correcting clock: schedules each tick at the real next-second boundary
   *  so the display never drifts or double-skips a second. */
  private scheduleTick() {
    const msToNextSecond = 1000 - (Date.now() % 1000);
    this.clockTimeout = setTimeout(() => {
      this.currentTime.set(new Date());
      this.scheduleTick();
    }, msToNextSecond);
  }

  ngOnDestroy() {
    if (this.clockTimeout) clearTimeout(this.clockTimeout);
    if (this.resetTimer) clearTimeout(this.resetTimer);
  }

  // ─── Helpers ─────────────────────────────────────────────────────

  private getMonday(date: Date): Date {
    const d = new Date(date);
    d.setHours(0, 0, 0, 0);
    const day = d.getDay();
    const diff = day === 0 ? -6 : 1 - day;
    d.setDate(d.getDate() + diff);
    return d;
  }

  private fmt(d: Date): string {
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }

  private initDateDefaults() {
    const today = new Date();
    const monday = this.getMonday(today);
    this.myHoursFrom = this.fmt(monday);
    this.myHoursTo = this.fmt(today);
    const fmtTime = (d: Date) =>
      `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
    this.manualFrom = `${this.fmt(today)}T06:00`;
    this.manualTo = `${this.fmt(today)}T${fmtTime(today)}`;
  }

  getDayName(dateStr: string): string {
    const d = new Date(dateStr);
    const names = ['Ne', 'Po', 'Ut', 'St', 'Št', 'Pi', 'So'];
    return names[d.getDay()];
  }

  getDayNum(dateStr: string): number {
    return new Date(dateStr).getDate();
  }

  isToday(dateStr: string): boolean {
    const d = new Date(dateStr);
    const today = new Date();
    return d.getDate() === today.getDate() &&
      d.getMonth() === today.getMonth() &&
      d.getFullYear() === today.getFullYear();
  }

  isCurrentWeek(): boolean {
    const monday = this.getMonday(new Date());
    return this.weekStart().toDateString() === monday.toDateString();
  }

  // ─── Weekly overview ─────────────────────────────────────────────

  loadOverview() {
    this.overviewLoading.set(true);
    this.kioskService.getOverview(this.fmt(this.weekStart())).subscribe({
      next: data => { this.overview.set(data); this.overviewLoading.set(false); },
      error: () => this.overviewLoading.set(false)
    });
  }

  prevWeek() {
    const d = new Date(this.weekStart());
    d.setDate(d.getDate() - 7);
    this.weekStart.set(d);
    this.loadOverview();
  }

  nextWeek() {
    const d = new Date(this.weekStart());
    d.setDate(d.getDate() + 7);
    this.weekStart.set(d);
    this.loadOverview();
  }

  goToCurrentWeek() {
    this.weekStart.set(this.getMonday(new Date()));
    this.loadOverview();
  }

  // ─── Employee tile → clock-in/out flow ──────────────────────────

  selectEmployee(emp: WeeklyRow) {
    this.selectedEmployee.set(emp);
    this.pin = '';
    this.pinDisplay.set([]);
    this.pinError.set('');
    this.comment = '';
    this.response.set(null);
    this.responseError.set(false);
    this.status.set(null);
    this.clockStep.set('pin');
    if (this.resetTimer) { clearTimeout(this.resetTimer); this.resetTimer = undefined; }
  }

  onOverlayClick(event: MouseEvent) {
    if (event.target === event.currentTarget) this.closeModal();
  }

  closeModal() {
    this.clockStep.set(null);
    this.selectedEmployee.set(null);
    this.pin = '';
    this.pinDisplay.set([]);
    this.pinError.set('');
    this.comment = '';
    this.hoursWorked = 8.0;
    this.selectedDate = '';
    this.status.set(null);
    this.selectedLocation.set(null);
    this.selectedCar.set('none');
    this.response.set(null);
    this.responseError.set(false);
    if (this.resetTimer) { clearTimeout(this.resetTimer); this.resetTimer = undefined; }
  }

  // PIN numpad
  numpadPress(digit: string) {
    if (this.pin.length >= 6) return;
    this.pin += digit;
    this.pinDisplay.set(Array(this.pin.length).fill('●'));
    this.pinError.set('');
  }

  numpadDelete() {
    if (!this.pin.length) return;
    this.pin = this.pin.slice(0, -1);
    this.pinDisplay.set(Array(this.pin.length).fill('●'));
    this.pinError.set('');
  }

  submitPin() {
    if (this.pin.length < 4) {
      this.pinError.set('Zadajte aspoň 4-miestny PIN');
      return;
    }
    this.loading.set(true);
    this.pinError.set('');
    this.kioskService.getStatus(this.pin).subscribe({
      next: s => {
        this.status.set(s);
        this.loading.set(false);
        this.clockStep.set('location');
      },
      error: () => {
        this.pinError.set('Neplatný PIN');
        this.pin = '';
        this.pinDisplay.set([]);
        this.loading.set(false);
      }
    });
  }

  todayString(): string {
    const d = new Date();
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }

  selectLocation(loc: Location) {
    this.selectedLocation.set(loc);
    this.hoursWorked = 8.0;
    this.selectedDate = this.todayString();
    this.comment = '';
    this.selectedCar.set('none');
    // Go to car step if there are active cars, otherwise skip straight to hours
    this.clockStep.set(this.cars().length > 0 ? 'car' : 'hours');
  }

  selectCar(car: Car | null) {
    // null = "no car used"
    this.selectedCar.set(car ?? 'none');
    this.clockStep.set('hours');
  }

  adjustHours(delta: number) {
    const next = Math.round((this.hoursWorked + delta) * 4) / 4; // 0.25h precision
    if (next >= 0.25 && next <= 24) {
      this.hoursWorked = next;
    }
  }

  setHours(h: number) {
    this.hoursWorked = h;
  }

  submitHours() {
    if (!this.selectedLocation()) return;
    this.loading.set(true);
    const car = this.selectedCar();
    const carId = car !== 'none' && car !== null ? car.id : undefined;

    this.kioskService.logHours(
      this.pin,
      this.selectedLocation()!.id,
      this.hoursWorked,
      this.comment || undefined,
      this.selectedDate || undefined,
      carId
    ).subscribe({
      next: res => {
        this.response.set(res);
        this.responseError.set(false);
        this.loading.set(false);
        this.clockStep.set('result');
        this.scheduleReset();
        this.loadOverview();
      },
      error: err => {
        this.response.set({
          message: err.error || 'Záznam sa nepodarilo uložiť',
          employeeName: '',
          timestamp: new Date().toISOString()
        });
        this.responseError.set(true);
        this.loading.set(false);
        this.clockStep.set('result');
      }
    });
  }

  private scheduleReset() {
    this.resetTimer = setTimeout(() => this.closeModal(), 5000);
  }

  // ─── View switching ──────────────────────────────────────────────

  setView(v: View) {
    this.view.set(v);
    if (v === 'my-hours') {
      this.myHoursLoaded.set(false);
      this.myHoursEntries.set([]);
    }
    if (v === 'manual') {
      this.manualResponse.set(null);
    }
  }

  // ─── Manual entry ────────────────────────────────────────────────

  canSubmitManual(): boolean {
    return this.manualPin.length >= 4 &&
      this.manualLocationId > 0 &&
      !!this.manualFrom && !!this.manualTo &&
      this.manualFrom < this.manualTo &&
      !!this.manualNote.trim();
  }

  submitManual() {
    this.manualLoading.set(true);
    this.manualResponse.set(null);
    this.kioskService.manualEntry(
      this.manualPin, this.manualLocationId, this.manualFrom, this.manualTo,
      this.manualNote || undefined
    ).subscribe({
      next: res => {
        this.manualResponse.set(res);
        this.manualResponseError.set(false);
        this.manualLoading.set(false);
        this.loadOverview();
      },
      error: err => {
        this.manualResponse.set({
          message: err.error || 'Záznam sa nepodarilo uložiť',
          employeeName: '',
          timestamp: new Date().toISOString()
        });
        this.manualResponseError.set(true);
        this.manualLoading.set(false);
      }
    });
  }

  // ─── My Hours ────────────────────────────────────────────────────

  loadMyHours() {
    if (this.myHoursPin.length < 4 || !this.myHoursFrom || !this.myHoursTo) return;
    this.myHoursLoading.set(true);
    this.kioskService.getMyHours(this.myHoursPin, this.myHoursFrom, this.myHoursTo).subscribe({
      next: entries => {
        this.myHoursEntries.set(entries);
        this.myHoursTotalHours.set(entries.reduce((s, e) => s + (e.hoursWorked ?? 0), 0));
        this.myHoursEmployeeName.set(entries.length > 0 ? entries[0].employeeName : '');
        this.myHoursLoaded.set(true);
        this.myHoursLoading.set(false);
      },
      error: () => {
        this.myHoursEntries.set([]);
        this.myHoursLoaded.set(true);
        this.myHoursLoading.set(false);
      }
    });
  }
}
