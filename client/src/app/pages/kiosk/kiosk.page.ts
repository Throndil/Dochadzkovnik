import { Component, signal, computed, OnInit, OnDestroy, ChangeDetectionStrategy, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe, DecimalPipe } from '@angular/common';
import { KioskService, KioskResponse, KioskStatus, WeeklyOverview, WeeklyRow, WorkPhotoResult, MissingHoursOverview } from '../../services/kiosk.service';
import { TimeEntry } from '../../services/time-entry.service';
import { Location } from '../../services/location.service';
import { Car } from '../../services/car.service';
import { DatepickerDirective } from '../../directives/datepicker.directive';
import { HmPipe } from '../../pipes/hm.pipe';
import { normaliseFile, fileToDataUrl, compressImage } from '../../utils/image-utils';
import { PushService } from '../../services/push.service';
import { FeatureFlagService } from '../../services/feature-flag.service';

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
  private pushService = inject(PushService);
  /** Exposed to template so every notification surface can be hidden when the
   *  Notifications feature flag is off in the customer's environment. */
  flags = inject(FeatureFlagService);

  // ─── "Treba pripomenúť" — missing-hours dashboard (Option A + D) ──
  /** Public list of workers with no entry for past 2 days. Visible to everyone. */
  missingOverview = signal<MissingHoursOverview | null>(null);
  /** Personal missing days for the worker who just entered their PIN. */
  myMissingDays = signal<string[]>([]);

  // Push notification state
  notificationSupported = signal(false);
  pushPermission = signal<NotificationPermission>('default');
  showNotificationPrompt = signal(false);
  subscribingToPush = signal(false);
  pushSubscribePin = '';
  pushSubscribePinDisplay = signal<string[]>([]);
  pushError = signal('');

  // Decline-notifications flow
  showDeclineForm = signal(false);
  declineReason = '';
  declinePin = '';
  declinePinDisplay = signal<string[]>([]);
  decliningNotifications = signal(false);
  declineError = signal('');
  declineStep = signal<'reason' | 'pin'>('reason');

  // Inline push-subscribe (button on the location step inside the clock-in modal).
  // Reuses the already-validated PIN — no second prompt.
  inlinePushBusy = signal(false);
  inlinePushDone = signal(false);
  inlinePushError = signal('');
  /** True when this browser has an active push subscription registered with the backend. */
  deviceSubscribed = signal(false);

  constructor(private kioskService: KioskService) {}

  ngOnInit() {
    this.initDateDefaults();
    this.loadOverview();
    this.loadMissingOverview();
    this.kioskService.getLocations().subscribe(locs => this.locations.set(locs));
    this.kioskService.getCars().subscribe(cars => this.cars.set(cars));
    this.scheduleTick();

    // Initialize push notification support — only when the Notifications feature
    // flag is enabled. Customer-facing prod ships with this off, so the kiosk
    // shows zero push UI until the superadmin flips it on.
    if (this.flags.notifications()) {
      this.notificationSupported.set(this.pushService.isSupported());
      this.pushPermission.set(this.pushService.currentPermission());

      // Show prompt if not yet granted and not denied
      if (this.notificationSupported() && this.pushPermission() === 'default') {
        this.showNotificationPrompt.set(true);
      } else if (this.notificationSupported() && this.pushPermission() === 'granted') {
        // Already subscribed, hide the prompt
        this.showNotificationPrompt.set(false);
        // Check if an active push subscription exists for this device
        navigator.serviceWorker.ready
          .then(reg => reg.pushManager.getSubscription())
          .then(sub => this.deviceSubscribed.set(!!sub))
          .catch(() => { /* ignore — deviceSubscribed stays false */ });
      }
    }
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

  /** Loads the public "Treba pripomenúť" list — pure read, safe to call anytime. */
  loadMissingOverview() {
    this.kioskService.getMissingHoursOverview().subscribe({
      next: data => this.missingOverview.set(data),
      error: () => this.missingOverview.set(null)
    });
  }

  /** Format yyyy-MM-dd as Slovak short date "d. M.". */
  formatMissingDate(dateStr: string): string {
    const [y, m, d] = dateStr.split('-').map(Number);
    return `${d}. ${m}.`;
  }

  /** Format yyyy-MM-dd as Slovak short date with weekday: "Pi 24. 4.". */
  formatMissingDateLong(dateStr: string): string {
    const [y, m, d] = dateStr.split('-').map(Number);
    const date = new Date(y, m - 1, d);
    const names = ['Ne', 'Po', 'Ut', 'St', 'Št', 'Pi', 'So'];
    return `${names[date.getDay()]} ${d}. ${m}.`;
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
    this.inlinePushDone.set(false);
    this.inlinePushError.set('');
    this.inlinePushBusy.set(false);
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
    this.myMissingDays.set([]);
    // Refresh the public Treba pripomenúť list — the worker may have just filled hours.
    this.loadMissingOverview();
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
        // Fetch personal missing days for the red banner shown on the location step.
        // Best-effort: if it fails we just don't show the banner.
        this.kioskService.getMyMissingDays(this.pin).subscribe({
          next: r => this.myMissingDays.set(r.missingDates ?? []),
          error: () => this.myMissingDays.set([])
        });
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
        // Worker just filled in hours — refresh the public Treba pripomenúť list
        // so peers don't keep seeing them on the "needs reminding" card.
        this.loadMissingOverview();

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

  // ─── Push Notifications (Povoliť upozornenia) ─────────────────────

  async startNotificationSubscribe(employeeId: number) {
    this.pushError.set('');
    this.pushSubscribePin = '';
    this.pushSubscribePinDisplay.set([]);
  }

  pushAddPinDigit(digit: string) {
    if (this.pushSubscribePin.length >= 6) return;
    this.pushSubscribePin += digit;
    this.pushSubscribePinDisplay.update(arr => [...arr, '●']);
  }

  pushDeletePin() {
    this.pushSubscribePin = this.pushSubscribePin.slice(0, -1);
    this.pushSubscribePinDisplay.update(arr => arr.slice(0, -1));
  }

  async pushClearPin() {
    this.pushSubscribePin = '';
    this.pushSubscribePinDisplay.set([]);
  }

  async pushSubscribe() {
    if (this.pushSubscribePin.length < 4) {
      this.pushError.set('PIN musí mať aspoň 4 číslice.');
      return;
    }

    // Find employee with this PIN by asking the backend (which will verify it)
    this.subscribingToPush.set(true);
    this.pushError.set('');

    try {
      // We need the employee ID — but we've already verified the PIN via the overview
      // For now, we'll extract it from selectedEmployee (set when the worker taps "Povoliť upozornenia")
      const emp = this.selectedEmployee();
      if (!emp || !emp.employeeId) {
        this.pushError.set('Neznámy zamestnanec. Skúste znova.');
        this.subscribingToPush.set(false);
        return;
      }

      const success = await this.pushService.requestPermissionAndSubscribe(emp.employeeId, this.pushSubscribePin);
      if (success) {
        // Hide the notification prompt after successful subscription
        this.showNotificationPrompt.set(false);
        this.pushPermission.set('granted');
        // Auto-close after 2 seconds
        setTimeout(() => {
          this.selectedEmployee.set(null);
          this.pushSubscribePin = '';
          this.pushSubscribePinDisplay.set([]);
        }, 2000);
      } else {
        this.pushError.set('Nepodarilo sa prihlásiť na upozornenia. Skúste znova.');
      }
    } catch (error) {
      this.pushError.set('Chyba pri prihlášení. Skúste znova.');
      console.error('Push subscribe error:', error);
    } finally {
      this.subscribingToPush.set(false);
    }
  }

  closeNotificationPrompt() {
    this.showNotificationPrompt.set(false);
    this.selectedEmployee.set(null);
    this.pushSubscribePin = '';
    this.pushSubscribePinDisplay.set([]);
    this.pushError.set('');
  }

  /** One-tap push subscribe used on the location step of the clock-in modal.
   *  Reuses the PIN the worker just typed — no second PIN entry needed. */
  async subscribeInline() {
    const emp = this.selectedEmployee();
    if (!emp?.employeeId || !this.pin) {
      this.inlinePushError.set('Skúste znova zadať PIN.');
      return;
    }
    if (!this.pushService.isSupported()) {
      this.inlinePushError.set('Tento prehliadač upozornenia nepodporuje.');
      return;
    }
    this.inlinePushBusy.set(true);
    this.inlinePushError.set('');
    try {
      const ok = await this.pushService.requestPermissionAndSubscribe(emp.employeeId, this.pin);
      if (ok) {
        this.pushPermission.set('granted');
        this.showNotificationPrompt.set(false);
        this.deviceSubscribed.set(true);
        this.inlinePushDone.set(true);
      } else {
        this.inlinePushError.set('Nepodarilo sa prihlásiť. Povoľte upozornenia v prehliadači a skúste znova.');
      }
    } catch (e) {
      console.error('Inline push subscribe failed:', e);
      this.inlinePushError.set('Chyba pri prihlášení.');
    } finally {
      this.inlinePushBusy.set(false);
    }
  }

  /** Unsubscribe this device from push notifications. */
  async unsubscribeInline() {
    this.inlinePushBusy.set(true);
    this.inlinePushError.set('');
    try {
      const reg = await navigator.serviceWorker.ready;
      const sub = await reg.pushManager.getSubscription();
      if (sub) {
        await this.pushService.unsubscribe(sub.endpoint);
        await sub.unsubscribe();
      }
      this.deviceSubscribed.set(false);
    } catch (e) {
      console.error('Inline push unsubscribe failed:', e);
      this.inlinePushError.set('Chyba pri deaktivácii. Skúste znova.');
    } finally {
      this.inlinePushBusy.set(false);
    }
  }

  openInstallGuide() {
    const isIOS = /iPhone|iPad|iPod/.test(navigator.userAgent);

    let url = '/Sichtovnica_Android_Sprievodca.pdf';
    if (isIOS) {
      url = '/Sichtovnica_iOS_Sprievodca.pdf';
    }

    window.open(url, '_blank');
  }

  // ─── Decline notifications flow ──────────────────────────────────

  openDeclineForm() {
    this.declineReason = '';
    this.declinePin = '';
    this.declinePinDisplay.set([]);
    this.declineError.set('');
    this.declineStep.set('reason');
    this.showDeclineForm.set(true);
  }

  closeDeclineForm() {
    this.showDeclineForm.set(false);
    this.declineReason = '';
    this.declinePin = '';
    this.declinePinDisplay.set([]);
    this.declineError.set('');
  }

  declinePinPress(digit: string) {
    if (this.declinePin.length >= 6) return;
    this.declinePin += digit;
    this.declinePinDisplay.set(Array(this.declinePin.length).fill('●'));
    this.declineError.set('');
  }

  declinePinDelete() {
    if (!this.declinePin.length) return;
    this.declinePin = this.declinePin.slice(0, -1);
    this.declinePinDisplay.set(Array(this.declinePin.length).fill('●'));
  }

  proceedToDeclinePin() {
    if (!this.declineReason.trim()) {
      this.declineError.set('Prosím, zadajte dôvod.');
      return;
    }
    this.declineError.set('');
    this.declineStep.set('pin');
  }

  submitDecline() {
    if (this.declinePin.length < 4) {
      this.declineError.set('Zadajte aspoň 4-miestny PIN');
      return;
    }
    this.decliningNotifications.set(true);
    this.declineError.set('');
    this.kioskService.declineNotifications(this.declinePin, this.declineReason.trim()).subscribe({
      next: () => {
        this.decliningNotifications.set(false);
        this.showDeclineForm.set(false);
        this.showNotificationPrompt.set(false);
      },
      error: () => {
        this.decliningNotifications.set(false);
        this.declineError.set('Neplatný PIN alebo chyba. Skúste znova.');
      }
    });
  }
}
