import { Component, signal, computed, OnInit, OnDestroy, ChangeDetectionStrategy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe, DecimalPipe } from '@angular/common';
import { KioskService, KioskResponse, KioskStatus, WeeklyOverview, WeeklyRow, WorkPhotoResult } from '../../services/kiosk.service';
import { TimeEntry } from '../../services/time-entry.service';
import { Location } from '../../services/location.service';
import { Car } from '../../services/car.service';
import { DatepickerDirective } from '../../directives/datepicker.directive';
import { HmPipe } from '../../pipes/hm.pipe';
import { normaliseFile, fileToDataUrl, compressImage } from '../../utils/image-utils';

type View = 'main' | 'photo-upload' | 'my-hours';
type ClockStep = 'pin' | 'location' | 'car' | 'hours' | 'photo-reason' | 'result';
type WuStep = 'pin' | 'location' | 'photo' | 'result';

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
  dateClampWarning = signal(false);  // shown when the date was silently clamped
  comment = '';
  response = signal<KioskResponse | null>(null);
  responseError = signal(false);
  loading = signal(false);

  // ─── Photo (hours modal) ─────────────────────────────────────────
  photoFiles = signal<File[]>([]);
  photoPreviews = signal<string[]>([]);
  photoUploaded = signal(false); // shown on result step
  noPhotoReason = '';             // filled on photo-reason step when skipping photo
  readonly MAX_PHOTOS = 5;

  // ─── Work photo upload (Nahrať fotografiu tab) ──────────────────
  wuStep = signal<WuStep | null>(null);
  wuPin = '';
  wuPinDisplay = signal<string[]>([]);
  wuPinError = signal('');
  wuSelectedLocation = signal<Location | null>(null);
  wuPhotoFiles = signal<File[]>([]);
  wuPhotoPreviews = signal<string[]>([]);
  wuLoading = signal(false);
  wuResult = signal<WorkPhotoResult | null>(null);
  wuResults = signal<WorkPhotoResult[]>([]);
  wuResultError = signal('');
  wuUploadedAt = signal<Date | null>(null);
  readonly MAX_WU_PHOTOS = 5;

  // ─── My Hours ────────────────────────────────────────────────────
  myHoursPin = '';
  showMyHoursPin = signal(false);
  myHoursMonth = '';   // YYYY-MM — drives the date range
  myHoursFrom = '';
  myHoursTo = '';
  myHoursEntries = signal<TimeEntry[]>([]);
  myHoursTotalHours = signal(0);
  myHoursEmployeeName = signal('');
  myHoursLoaded = signal(false);
  myHoursLoading = signal(false);
  /** Backend error when loading own hours (e.g. "Neplatný PIN"). Empty string = no error. */
  myHoursError = signal('');

  // Multi-photo lightbox for "Moje hodiny" — prev/next through all photos of a row.
  myHoursLightboxPhotos = signal<string[]>([]);
  myHoursLightboxIdx = signal(0);
  myHoursLightboxPhoto = computed(() => this.myHoursLightboxPhotos()[this.myHoursLightboxIdx()] ?? null);

  openMyHoursLightbox(photos: string[], startIdx = 0) {
    if (!photos.length) return;
    this.myHoursLightboxPhotos.set(photos);
    this.myHoursLightboxIdx.set(Math.min(Math.max(0, startIdx), photos.length - 1));
  }
  closeMyHoursLightbox() {
    this.myHoursLightboxPhotos.set([]);
    this.myHoursLightboxIdx.set(0);
  }
  myHoursLightboxNext() {
    const len = this.myHoursLightboxPhotos().length;
    if (len < 2) return;
    this.myHoursLightboxIdx.set((this.myHoursLightboxIdx() + 1) % len);
  }
  myHoursLightboxPrev() {
    const len = this.myHoursLightboxPhotos().length;
    if (len < 2) return;
    this.myHoursLightboxIdx.set((this.myHoursLightboxIdx() - 1 + len) % len);
  }

  /** Returns the cover photo URL for a location by id, or null if not found. */
  getLocationPhoto(locationId: number): string | null {
    return this.locations().find(l => l.id === locationId)?.photoUrl ?? null;
  }

  /** Parse a photoUrl that may be a single URL or comma-separated list of URLs. */
  parsePhotoUrls(photoUrl?: string | null): string[] {
    if (!photoUrl) return [];
    return photoUrl.split(',').map(u => u.trim()).filter(u => u.length > 0);
  }

  /** Returns the first URL from a possibly comma-separated photoUrl. */
  firstPhotoUrl(photoUrl?: string | null): string | null {
    const urls = this.parsePhotoUrls(photoUrl);
    return urls.length > 0 ? urls[0] : null;
  }

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
    const ym = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}`;
    this.myHoursMonth = ym;
    this.setMyHoursFromMonth(ym);
  }

  /** Sets myHoursFrom/To to the full first–last day of the given YYYY-MM month. */
  setMyHoursFromMonth(ym: string) {
    const [y, m] = ym.split('-').map(Number);
    this.myHoursFrom = this.fmt(new Date(y, m - 1, 1));
    this.myHoursTo   = this.fmt(new Date(y, m, 0));     // day 0 of next month = last day
  }

  /** Called from the month input's (change) event — updates range and auto-reloads if already showing. */
  onMyHoursMonthChange(ym: string) {
    this.setMyHoursFromMonth(ym);
    if (this.myHoursLoaded()) this.loadMyHours();
  }

  myHoursPrevMonth() {
    const [y, m] = this.myHoursMonth.split('-').map(Number);
    const prev = new Date(y, m - 2, 1); // m-2 because months are 0-indexed
    this.myHoursMonth = `${prev.getFullYear()}-${String(prev.getMonth() + 1).padStart(2, '0')}`;
    this.setMyHoursFromMonth(this.myHoursMonth);
    if (this.myHoursLoaded()) this.loadMyHours();
  }

  myHoursNextMonth() {
    const [y, m] = this.myHoursMonth.split('-').map(Number);
    const next = new Date(y, m, 1);
    this.myHoursMonth = `${next.getFullYear()}-${String(next.getMonth() + 1).padStart(2, '0')}`;
    this.setMyHoursFromMonth(this.myHoursMonth);
    if (this.myHoursLoaded()) this.loadMyHours();
  }

  getDayName(dateStr: string): string {
    const d = new Date(dateStr);
    const names = ['Ne', 'Po', 'Ut', 'St', 'Št', 'Pi', 'So'];
    return names[d.getDay()];
  }

  getDayNum(dateStr: string): number {
    return new Date(dateStr).getDate();
  }

  /** Total completed hours for today. Returns -1 if today is not in the current week view. */
  getTodayHours(row: WeeklyRow): number {
    const todayData = row.days.find(d => this.isToday(d.date));
    if (!todayData) return -1;
    return todayData.entries.reduce((sum, e) => sum + e.hours, 0);
  }

  /** Returns the full Tailwind class string for an employee tile based on today's hours. */
  getTileClass(row: WeeklyRow): string {
    const base = 'border rounded-xl p-3 flex flex-col items-center gap-2 transition-colors active:scale-95 cursor-pointer';
    const hours = this.getTodayHours(row);
    if (hours < 0)   // today not in the viewed week — neutral
      return `${base} bg-white dark:bg-slate-800 hover:bg-slate-100 dark:hover:bg-slate-700 border-slate-300 dark:border-slate-600 hover:border-amber-500`;
    if (hours === 0) // no hours at all — red
      return `${base} bg-red-50 dark:bg-red-950/30 hover:bg-red-100 dark:hover:bg-red-900/40 border-red-400 dark:border-red-500 hover:border-red-400`;
    if (hours >= 8)  // full day — green
      return `${base} bg-green-50 dark:bg-green-950/30 hover:bg-green-100 dark:hover:bg-green-900/40 border-green-400 dark:border-green-500 hover:border-green-400`;
    // partial hours (0 < h < 8) — neutral
    return `${base} bg-white dark:bg-slate-800 hover:bg-slate-100 dark:hover:bg-slate-700 border-slate-300 dark:border-slate-600 hover:border-amber-500`;
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
    this.photoFiles.set([]);
    this.photoPreviews.set([]);
    this.photoUploaded.set(false);
    this.noPhotoReason = '';
    if (this.resetTimer) { clearTimeout(this.resetTimer); this.resetTimer = undefined; }
  }

  async onPhotoSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const rawFiles = Array.from(input.files ?? []);
    if (!rawFiles.length) return;
    input.value = '';

    const current = this.photoFiles();
    const slots = this.MAX_PHOTOS - current.length;
    if (slots <= 0) return;
    const toProcess = rawFiles.slice(0, slots);

    const newFiles: File[] = [];
    const newPreviews: string[] = [];
    for (const raw of toProcess) {
      const normalised = await normaliseFile(raw);
      const file = await compressImage(normalised);
      const preview = await fileToDataUrl(file);
      newFiles.push(file);
      newPreviews.push(preview);
    }

    this.photoFiles.update(f => [...f, ...newFiles]);
    this.photoPreviews.update(p => [...p, ...newPreviews]);
    // Discard any no-photo reason once a photo is provided
    this.noPhotoReason = '';
  }

  removePhoto(index: number) {
    this.photoFiles.update(f => f.filter((_, i) => i !== index));
    this.photoPreviews.update(p => p.filter((_, i) => i !== index));
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
        // Verify the PIN belongs to the employee whose tile was tapped
        if (s.employeeId !== this.selectedEmployee()?.employeeId) {
          this.pinError.set('Nesprávny PIN');
          this.pin = '';
          this.pinDisplay.set([]);
          this.loading.set(false);
          return;
        }
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

  twoDaysAgoString(): string {
    const d = new Date();
    d.setDate(d.getDate() - 2);
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }

  /** Clamps selectedDate to [twoDaysAgo … today]. Called on (change) and before submit.
   *  Some mobile browsers allow typing or scrolling outside the min/max range. */
  clampSelectedDate() {
    const min = this.twoDaysAgoString();
    const max = this.todayString();
    if (!this.selectedDate || this.selectedDate < min) {
      this.selectedDate = min;
      this.showDateClampWarning();
    } else if (this.selectedDate > max) {
      this.selectedDate = max;
      this.showDateClampWarning();
    }
  }

  private showDateClampWarning() {
    this.dateClampWarning.set(true);
    setTimeout(() => this.dateClampWarning.set(false), 3500);
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

  /** Called from the hours step "Ďalej" button. */
  proceedFromHours() {
    this.clampSelectedDate();
    if (!this.selectedLocation()) return;
    // If no photo taken yet, require the worker to take one or give a reason
    if (this.photoFiles().length === 0) {
      this.noPhotoReason = '';
      this.clockStep.set('photo-reason');
      return;
    }
    this.submitHours();
  }

  submitHours() {
    if (!this.selectedLocation()) return;
    this.clampSelectedDate(); // enforce date range even if browser skipped min/max
    this.loading.set(true);
    const car = this.selectedCar();
    const carId = car !== 'none' && car !== null ? car.id : undefined;
    const photoFiles = this.photoFiles();

    // If a no-photo reason was given, append it to the note
    const finalComment = this.noPhotoReason
      ? (this.comment ? `${this.comment} | Dôvod bez foto: ${this.noPhotoReason}` : `Dôvod bez foto: ${this.noPhotoReason}`)
      : (this.comment || undefined);

    this.kioskService.logHours(
      this.pin,
      this.selectedLocation()!.id,
      this.hoursWorked,
      finalComment,
      this.selectedDate || undefined,
      carId
    ).subscribe({
      next: res => {
        this.response.set(res);
        this.responseError.set(false);
        this.loadOverview();

        if (photoFiles.length > 0 && res.timeEntryId) {
          // Upload all photos via the kiosk endpoint (no JWT needed — PIN already verified above)
          this.kioskService.uploadEntryPhotos(res.timeEntryId, this.pin, photoFiles).subscribe({
            next: () => {
              this.photoUploaded.set(true);
              this.loading.set(false);
              this.clockStep.set('result');
              this.scheduleReset();
            },
            error: () => {
              // Hours saved, photos failed — show warning in result
              const current = this.response();
              this.response.set({ ...current!, message: current!.message + ' (foto sa nepodarilo nahrať)' });
              this.loading.set(false);
              this.clockStep.set('result');
              this.scheduleReset();
            }
          });
        } else {
          this.loading.set(false);
          this.clockStep.set('result');
          this.scheduleReset();
        }
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
    if (v === 'photo-upload') {
      this.wuReset();
      this.wuStep.set('pin');
    }
  }

  // ─── Work photo upload flow ──────────────────────────────────────

  wuReset() {
    this.wuPin = '';
    this.wuPinDisplay.set([]);
    this.wuPinError.set('');
    this.wuSelectedLocation.set(null);
    this.wuPhotoFiles.set([]);
    this.wuPhotoPreviews.set([]);
    this.wuLoading.set(false);
    this.wuResult.set(null);
    this.wuResults.set([]);
    this.wuResultError.set('');
    this.wuUploadedAt.set(null);
  }

  wuNumpadPress(digit: string) {
    if (this.wuPin.length >= 6) return;
    this.wuPin += digit;
    this.wuPinDisplay.set(Array(this.wuPin.length).fill('●'));
    this.wuPinError.set('');
  }

  wuNumpadDelete() {
    if (!this.wuPin.length) return;
    this.wuPin = this.wuPin.slice(0, -1);
    this.wuPinDisplay.set(Array(this.wuPin.length).fill('●'));
    this.wuPinError.set('');
  }

  wuSubmitPin() {
    if (this.wuPin.length < 4) {
      this.wuPinError.set('Zadajte aspoň 4-miestny PIN');
      return;
    }
    this.wuLoading.set(true);
    this.wuPinError.set('');
    this.kioskService.getStatus(this.wuPin).subscribe({
      next: () => {
        this.wuLoading.set(false);
        this.wuStep.set('location');
      },
      error: () => {
        this.wuPinError.set('Neplatný PIN');
        this.wuPin = '';
        this.wuPinDisplay.set([]);
        this.wuLoading.set(false);
      }
    });
  }

  wuSelectLocation(loc: Location) {
    this.wuSelectedLocation.set(loc);
    this.wuStep.set('photo');
  }

  async onWuPhotoSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const rawFiles = Array.from(input.files ?? []);
    if (!rawFiles.length) return;
    input.value = '';

    const current = this.wuPhotoFiles();
    const slots = this.MAX_WU_PHOTOS - current.length;
    if (slots <= 0) return;
    const toProcess = rawFiles.slice(0, slots);

    const newFiles: File[] = [];
    const newPreviews: string[] = [];
    for (const raw of toProcess) {
      const normalised = await normaliseFile(raw);
      const file = await compressImage(normalised);
      const preview = await fileToDataUrl(file);
      newFiles.push(file);
      newPreviews.push(preview);
    }

    this.wuPhotoFiles.update(f => [...f, ...newFiles]);
    this.wuPhotoPreviews.update(p => [...p, ...newPreviews]);
  }

  wuRemovePhoto(index: number) {
    this.wuPhotoFiles.update(f => f.filter((_, i) => i !== index));
    this.wuPhotoPreviews.update(p => p.filter((_, i) => i !== index));
  }

  wuSubmitPhoto() {
    const files = this.wuPhotoFiles();
    const loc = this.wuSelectedLocation();
    if (!files.length || !loc) return;
    this.wuLoading.set(true);
    this.wuResultError.set('');
    this.wuResults.set([]);

    const uploadNext = (index: number) => {
      if (index >= files.length) {
        const results = this.wuResults();
        if (results.length > 0) {
          this.wuResult.set(results[results.length - 1]);
          this.wuUploadedAt.set(new Date());
        }
        this.wuLoading.set(false);
        this.wuStep.set('result');
        setTimeout(() => {
          this.wuReset();
          this.wuStep.set('pin');
        }, 7000);
        return;
      }
      this.kioskService.uploadWorkPhoto(this.wuPin, loc.id, files[index]).subscribe({
        next: result => {
          this.wuResults.update(r => [...r, result]);
          uploadNext(index + 1);
        },
        error: err => {
          this.wuResultError.set(err.error || 'Foto sa nepodarilo nahrať');
          this.wuLoading.set(false);
        }
      });
    };

    uploadNext(0);
  }

  // ─── My Hours ────────────────────────────────────────────────────

  loadMyHours() {
    if (this.myHoursPin.length < 4 || !this.myHoursFrom || !this.myHoursTo) return;
    this.myHoursLoading.set(true);
    this.myHoursError.set('');
    this.kioskService.getMyHours(this.myHoursPin, this.myHoursFrom, this.myHoursTo).subscribe({
      next: entries => {
        this.myHoursEntries.set(entries);
        this.myHoursTotalHours.set(entries.reduce((s, e) => s + (e.hoursWorked ?? 0), 0));
        this.myHoursEmployeeName.set(entries.length > 0 ? entries[0].employeeName : '');
        this.myHoursLoaded.set(true);
        this.myHoursLoading.set(false);
      },
      error: err => {
        this.myHoursEntries.set([]);
        // Surface the backend error message so workers know *why* no hours
        // are shown (e.g. wrong PIN, deactivated account) instead of silently
        // seeing an empty table.
        const msg = typeof err?.error === 'string' ? err.error : 'Nepodarilo sa načítať hodiny.';
        this.myHoursError.set(msg);
        this.myHoursLoaded.set(true);
        this.myHoursLoading.set(false);
      }
    });
  }
}
