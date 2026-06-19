import { Component, signal, computed, OnInit, OnDestroy, ChangeDetectionStrategy, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe, DecimalPipe } from '@angular/common';
import { KioskService, KioskResponse, KioskStatus, WeeklyOverview, WeeklyRow, WorkPhotoResult, MissingHoursOverview } from '../../services/kiosk.service';
import { TimeEntry } from '../../services/time-entry.service';
import { Location } from '../../services/location.service';
import { Car } from '../../services/car.service';
import { HmPipe } from '../../pipes/hm.pipe';
import { normaliseFile, fileToDataUrl, compressImage } from '../../utils/image-utils';
import { PushService } from '../../services/push.service';
import { FeatureFlagService } from '../../services/feature-flag.service';
import { MaterialPurchaseService } from '../../services/material-purchase.service';
import { WorkDiaryService } from '../../services/work-diary.service';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { NakupFlowComponent } from '../../components/nakup-flow/nakup-flow.component';

type View = 'main' | 'photo-upload' | 'my-hours';
/**
 * Clock-in modal step machine. The 'mode-pick' step is inserted between
 * 'pin' (PIN validated server-side) and 'location' (existing šichta flow)
 * when the MaterialPurchases feature flag is ON. It lets the worker choose
 * between recording šichta hours and recording a Nákup materiálu — without
 * polluting the kiosk root or duplicating the PIN entry.
 */
/**
 * 'proof-pick' and 'diary' are inserted between 'hours' and 'photo-reason'/'result'
 * when the ProofOfWorkChoices feature flag is ON. They let the worker pick
 * Fotografia / Stavebný denník / Pokračovať bez dôkazu instead of being forced
 * into the photo step. See PROOF_OF_WORK_UX_PLAN.md.
 */
type ClockStep = 'pin' | 'mode-pick' | 'location' | 'car' | 'hours' | 'proof-pick' | 'diary' | 'photo-reason' | 'result';
type WuStep = 'pin' | 'location' | 'photo' | 'result';

@Component({
  selector: 'app-kiosk',
  imports: [FormsModule, DatePipe, DecimalPipe, HmPipe, SpinnerComponent, NakupFlowComponent],
  templateUrl: './kiosk.page.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class KioskPage implements OnInit, OnDestroy {

  // ─── In-šichta Nákup capture ──────────────────────────────────────
  /** Configured trigger Location.Id (or fallback by name). When the worker picks
   *  this Location during the existing šichta flow, the result step offers a
   *  "Pokračovať s nákupom materiálu" button. Loaded on init when the flag is on. */
  triggerLocationId = signal<number | null>(null);
  /** When set, render <app-nakup-flow mode="in-shift"> and hide the rest of the
   *  kiosk UI. Carries the validated PIN. The TimeEntryId is populated only when
   *  the worker entered the Nákup flow via the post-šichta-result button (link
   *  back to the šichta record); when entered via the in-modal mode-pick step
   *  before any šichta is recorded, TimeEntryId stays null. */
  inShiftNakupContext = signal<{ pin: string; timeEntryId: number | null } | null>(null);
  /** Set in submitHours' success handler when the worker just logged hours at the
   *  trigger Location AND the flag is on. Drives the in-šichta button on the
   *  result step. Cleared by closeModal(). */
  pendingTimeEntryIdForNakup = signal<number | null>(null);

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
  /** True while the today-at-location roll-up is loading on the way straight
   *  to the hours step. Lets the tapped location tile show a spinner and the
   *  hours step render with the roll-up already in place (no late layout shift). */
  locationLoading = signal(false);
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

  // ─── Proof-of-work choice (ProofOfWorkChoices flag) ─────────────
  /** Set true when the worker explicitly picked "Pokračovať bez dôkazu". */
  proofOfWorkSkipped = signal(false);
  /** Stavebný denník body. Empty string when no diary is being submitted. */
  diaryBody = '';
  /** Optional file attached to the diary (PDF or image scan). */
  diaryAttachmentFile = signal<File | null>(null);
  diaryAttachmentPreview = signal<string | null>(null);
  /** Visible result-screen badge: 'photo' | 'diary' | 'skipped' | null. */
  proofResult = signal<'photo' | 'diary' | 'skipped' | null>(null);
  /** Auto-skip hint shown on the result screen when proof-pick was skipped
   *  because a recent proof already exists. Slovak copy, includes the prior
   *  timestamp. Cleared by closeModal. */
  autoSkipHint = signal<string | null>(null);

  // ─── Today at this location (read-only roll-up on the hours step) ─
  /** Other workers' entries at the picked Location for today. Loaded right
   *  after selectLocation. Used to give the next worker site context before
   *  they write their own note. */
  todayAtLocation = signal<Array<{
    employeeId: number;
    employeeName: string;
    clockIn: string;
    hoursWorked: number | null;
    note: string | null;
    diaryBody: string | null;
    isMine: boolean;
  }>>([]);

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

  // ─── Mode-pick step (inside the clock-in modal, post-PIN) ───────

  /**
   * "Zaznamenať šichtu" tile inside the modal — continue the existing
   * šichta flow (Location → Car → Hours → Photo → Result).
   */
  pickShiftFromModePick() {
    this.clockStep.set('location');
  }

  /**
   * "Nákup materiálu" tile inside the modal — close the šichta modal,
   * mount NakupFlow with the already-validated PIN. No TimeEntryId yet
   * (no šichta exists). The resulting MaterialPurchase has TimeEntryId=null.
   */
  pickNakupFromModePick() {
    if (!this.pin) return;
    const ctx = { pin: this.pin, timeEntryId: null as number | null };
    this.closeModal();
    this.inShiftNakupContext.set(ctx);
  }

  /**
   * Triggered from the šichta result screen's "Pokračovať s nákupom materiálu"
   * button when the worker logged hours at the trigger Location. Captures the
   * PIN + TimeEntryId BEFORE closeModal() blanks them, then mounts the post-
   * šichta Nákup flow with the link to the just-created TimeEntry.
   */
  startInShiftNakup() {
    const teId = this.pendingTimeEntryIdForNakup();
    if (teId == null || !this.pin) return;
    const ctx = { pin: this.pin, timeEntryId: teId };
    if (this.resetTimer) { clearTimeout(this.resetTimer); this.resetTimer = undefined; }
    this.closeModal();           // also clears pendingTimeEntryIdForNakup
    this.inShiftNakupContext.set(ctx);
  }

  /** NakupFlow closed (cancel or finish). Reloads the weekly overview + missing
   *  list so a freshly auto-booked šichta from the in-šichta path lands in
   *  the kiosk's grid without a manual refresh. Cheap reads; safe to call on
   *  cancel too. */
  onInShiftNakupClose() {
    this.inShiftNakupContext.set(null);
    this.loadOverview();
    this.loadMissingOverview();
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
  private mpService = inject(MaterialPurchaseService);
  private workDiaryService = inject(WorkDiaryService);
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

    // Load the trigger Location id for the post-šichta combined capture. Server
    // resolves either the configured MaterialPurchases:TriggerLocationId or
    // falls back to a case-insensitive name match against "Nákup materiálu".
    // Failure leaves triggerLocationId null — the after-šichta button just
    // doesn't appear, which is the safe default. The in-modal mode-pick step
    // (PIN → mode → location/Nákup) is independent of this — it only needs
    // the feature flag.
    if (this.flags.materialPurchases()) {
      this.mpService.getKioskConfig().subscribe({
        next: cfg => this.triggerLocationId.set(cfg?.triggerLocationId ?? null),
        error: () => this.triggerLocationId.set(null)
      });
    }

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
    // Reset per-worker signals here too: closeModal usually clears them, but
    // when one worker taps another worker's tile before the 5s result timer
    // fires, closeModal never ran. Without this reset the next worker would
    // briefly see the previous worker's today roll-up / selected location.
    this.todayAtLocation.set([]);
    this.locationLoading.set(false);
    this.selectedLocation.set(null);
    this.selectedCar.set('none');
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
    this.proofOfWorkSkipped.set(false);
    this.diaryBody = '';
    this.diaryAttachmentFile.set(null);
    this.diaryAttachmentPreview.set(null);
    this.proofResult.set(null);
    this.autoSkipHint.set(null);
    this.todayAtLocation.set([]);
    this.locationLoading.set(false);
    this.myMissingDays.set([]);
    this.pendingTimeEntryIdForNakup.set(null);
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
        // Nákup materiálu standalone kiosk flow is temporarily disabled per
        // customer request 2026-05-25 — always go straight to location after
        // PIN. To re-enable, restore the original ternary:
        //   this.clockStep.set(this.flags.materialPurchases() ? 'mode-pick' : 'location');
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
    // Fetch today's roll-up so the hours step can show peer notes. Best-effort —
    // failures leave the card empty rather than blocking the flow.
    this.todayAtLocation.set([]);
    const hasCars = this.cars().length > 0;
    // When we go via the car step, the roll-up loads in the background and is
    // ready by the time the worker reaches hours. When we skip straight to
    // hours, wait for the roll-up first so it doesn't pop in late and shove
    // the hours form down (the "bump"). The tapped tile shows a spinner.
    this.locationLoading.set(!hasCars);
    this.kioskService.getTodayAtLocation(this.pin, loc.id).subscribe({
      next: rows => {
        this.todayAtLocation.set(rows ?? []);
        if (!hasCars) { this.locationLoading.set(false); this.clockStep.set('hours'); }
      },
      error: () => {
        this.todayAtLocation.set([]);
        if (!hasCars) { this.locationLoading.set(false); this.clockStep.set('hours'); }
      }
    });
    if (hasCars) this.clockStep.set('car');
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
    // Skip the photo-or-reason step when the picked Location is the configured
    // Nákup materiálu trigger — workers are recording a shopping trip, not
    // proof-of-work, and the post-šichta result screen offers the proper
    // Nákup capture (with its own receipt photo). Asking for a šichta photo
    // here is just friction.
    const isTrigger = this.flags.materialPurchases()
      && this.triggerLocationId() !== null
      && this.selectedLocation()?.id === this.triggerLocationId();
    if (isTrigger) {
      this.submitHours();
      return;
    }
    // New flag-on path: three-tile proof-of-work picker, with an auto-skip
    // ahead of it. If the worker already attached a proof at this site in
    // the past hour, skip the picker entirely and submit with a hint.
    if (this.flags.proofOfWorkChoices()) {
      this.proofOfWorkSkipped.set(false);
      this.diaryBody = '';
      this.diaryAttachmentFile.set(null);
      this.diaryAttachmentPreview.set(null);
      const dateStr = this.selectedDate || undefined;
      this.kioskService.checkProofExists(this.pin, this.selectedLocation()!.id, dateStr).subscribe({
        next: r => {
          if (r.exists && r.at) {
            // Format the prior timestamp in HH:mm. Backend returns local TZ.
            // Note: we deliberately do NOT auto-submit here — the worker should
            // see the hint on the proof-pick screen and still pick their option.
            // Customer feedback 2026-05-25: instant auto-clock-in was too aggressive.
            const at = new Date(r.at);
            const hh = String(at.getHours()).padStart(2, '0');
            const mm = String(at.getMinutes()).padStart(2, '0');
            const what = r.source === 'diary' ? 'zápis do denníka' : 'fotku';
            this.autoSkipHint.set(`Dnes ste tu už pridali ${what} o ${hh}:${mm}.`);
          }
          this.clockStep.set('proof-pick');
        },
        error: () => {
          // Auto-skip detection is a nice-to-have; on any error, fall through to the picker.
          this.clockStep.set('proof-pick');
        }
      });
      return;
    }
    // Legacy flag-off path: existing photo-reason step unchanged.
    this.noPhotoReason = '';
    this.clockStep.set('photo-reason');
  }

  // ─── Proof-of-work step (ProofOfWorkChoices flag) ─────────────────

  /**
   * "Fotografia" tile — fall through to the existing photo-reason step so
   * the camera / gallery / no-photo-reason UI keeps working unchanged.
   */
  pickProofPhoto() {
    this.noPhotoReason = '';
    this.clockStep.set('photo-reason');
  }

  /** "Stavebný denník" tile — open the inline diary form. */
  pickProofDiary() {
    this.diaryBody = '';
    this.diaryAttachmentFile.set(null);
    this.diaryAttachmentPreview.set(null);
    this.clockStep.set('diary');
  }

  /**
   * "Pokračovať bez dôkazu" tile — confirm and submit with the skip flag
   * set. The confirm is rendered inline on the proof-pick step via a
   * `confirmingSkip` signal-less prompt; keeping component state small.
   */
  confirmSkipProof() {
    this.proofOfWorkSkipped.set(true);
    this.submitHours();
  }

  /** "Späť" from diary form back to the proof-pick tiles. */
  backToProofPick() {
    this.diaryBody = '';
    this.diaryAttachmentFile.set(null);
    this.diaryAttachmentPreview.set(null);
    this.clockStep.set('proof-pick');
  }

  async onDiaryAttachmentSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const raw = input.files?.[0];
    input.value = '';
    if (!raw) return;
    // PDFs pass through unchanged; images go through the existing pipeline
    // for HEIC normalisation + compression.
    if (raw.type === 'application/pdf') {
      this.diaryAttachmentFile.set(raw);
      this.diaryAttachmentPreview.set(null);
      return;
    }
    const normalised = await normaliseFile(raw);
    const file = await compressImage(normalised);
    const preview = await fileToDataUrl(file);
    this.diaryAttachmentFile.set(file);
    this.diaryAttachmentPreview.set(preview);
  }

  removeDiaryAttachment() {
    this.diaryAttachmentFile.set(null);
    this.diaryAttachmentPreview.set(null);
  }

  /**
   * Diary form submit. Reuses the standard submitHours pipeline: the diary
   * body is buffered on this component; submitHours fires the kiosk
   * /log-hours call AND, on success, POSTs the diary linked to the new
   * TimeEntryId. proofOfWorkSkipped stays false — a diary IS a proof.
   */
  submitDiary() {
    const body = this.diaryBody.trim();
    if (body.length === 0) return;        // disabled button on the template
    this.proofOfWorkSkipped.set(false);
    this.submitHours();
  }

  submitHours() {
    if (!this.selectedLocation()) return;
    this.clampSelectedDate(); // enforce date range even if browser skipped min/max
    this.loading.set(true);
    const car = this.selectedCar();
    const carId = car !== 'none' && car !== null ? car.id : undefined;
    const photoFiles = this.photoFiles();

    // Append a marker to the TimeEntry note so the admin Záznamy dochádzky
    // view can tell at a glance how the worker satisfied the proof-of-work step.
    // Same `|`-separator convention as the legacy "Dôvod bez foto" path.
    const usingDiary = this.diaryBody.trim().length > 0;
    let finalComment: string | undefined = this.comment || undefined;
    const suffixes: string[] = [];
    if (this.noPhotoReason) suffixes.push(`Dôvod bez foto: ${this.noPhotoReason}`);
    if (usingDiary)         suffixes.push('Stavebný denník');
    if (suffixes.length > 0) {
      const tail = suffixes.join(' | ');
      finalComment = finalComment ? `${finalComment} | ${tail}` : tail;
    }

    const skipProof = this.proofOfWorkSkipped();
    this.kioskService.logHours(
      this.pin,
      this.selectedLocation()!.id,
      this.hoursWorked,
      finalComment,
      this.selectedDate || undefined,
      carId,
      skipProof || undefined  // omit field entirely on the false path so flag-off behaviour is byte-identical
    ).subscribe({
      next: res => {
        this.response.set(res);
        this.responseError.set(false);
        this.loadOverview();
        // Worker just filled in hours — refresh the public Treba pripomenúť list
        // so peers don't keep seeing them on the "needs reminding" card.
        this.loadMissingOverview();


        // In-šichta combined Nákup capture: when the worker just logged hours at
        // the configured trigger Location AND the feature flag is on, expose the
        // resulting TimeEntryId so the result screen can offer a "Pokračovať s
        // nákupom materiálu" button. The button hands the PIN + TimeEntryId off
        // to the NakupFlow component in 'in-shift' mode.
        const pickedLocId = this.selectedLocation()?.id ?? null;
        if (
          this.flags.materialPurchases()
          && pickedLocId !== null
          && this.triggerLocationId() === pickedLocId
          && res.timeEntryId
        ) {
          this.pendingTimeEntryIdForNakup.set(res.timeEntryId);
        }

        // ProofOfWorkChoices flag-on diary path: if the worker composed a diary
        // body on the 'diary' step, POST it linked to the new TimeEntry and
        // optionally upload the attachment. Failures are non-fatal — hours are
        // already saved.
        const diaryBody = this.diaryBody.trim();
        if (diaryBody.length > 0 && res.timeEntryId) {
          const date = (this.selectedDate || new Date().toISOString().slice(0, 10));
          this.workDiaryService.createFromKiosk({
            pin: this.pin,
            locationId: this.selectedLocation()!.id,
            date,
            bodyText: diaryBody,
            timeEntryId: res.timeEntryId
          }).then(async created => {
            const attachment = this.diaryAttachmentFile();
            if (attachment) {
              try { await this.workDiaryService.uploadKioskAttachment(created.id, this.pin, attachment); } catch { /* non-fatal */ }
            }
            this.proofResult.set('diary');
            this.loading.set(false);
            this.clockStep.set('result');
            this.scheduleReset();
          }).catch(() => {
            // Hours saved, diary failed — show warning in result
            const current = this.response();
            this.response.set({ ...current!, message: current!.message + ' (denník sa nepodarilo uložiť)' });
            this.loading.set(false);
            this.clockStep.set('result');
            this.scheduleReset();
          });
          return;
        }

        if (photoFiles.length > 0 && res.timeEntryId) {
          // Upload all photos via the kiosk endpoint (no JWT needed — PIN already verified above)
          this.kioskService.uploadEntryPhotos(res.timeEntryId, this.pin, photoFiles).subscribe({
            next: () => {
              this.photoUploaded.set(true);
              this.proofResult.set('photo');
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
          if (skipProof) this.proofResult.set('skipped');
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
