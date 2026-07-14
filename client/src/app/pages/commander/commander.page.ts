import {
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  OnDestroy,
  OnInit,
  signal,
  viewChild,
} from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import * as L from 'leaflet';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { SpinnerComponent } from '../../components/spinner/spinner.component';
import { AuthService } from '../../services/auth.service';
import { FeatureFlagService } from '../../services/feature-flag.service';
import {
  CommanderError,
  CommanderFleetVehicleStats,
  CommanderPosition,
  CommanderRideBucket,
  CommanderRideDetail,
  CommanderRideSummary,
  CommanderService,
  CommanderSnappedRoute,
  CommanderTacho,
  CommanderVehicle,
} from '../../services/commander.service';

/**
 * Commander dashboard.
 *
 * Two views, switched via a tab toggle:
 *   - Detail   sidebar (vehicle list) + cards + big map
 *   - Prehľad  flat table + small map (matches Commander's own "Prehľad" tab)
 *
 * The map is Leaflet on OpenStreetMap tiles. No API key, no Google billing.
 * One Leaflet instance is reused across both tabs by tearing it down and
 * mounting onto the active tab's container — Leaflet doesn't support moving
 * its container, so we accept a brief tile reload on tab switch.
 *
 * Data freshness:
 *   - /vehicles is cached server-side for 24h (per the Commander spec's "call
 *     it e.g. once a day" guidance). So `lastCommunication` is up to 24h
 *     stale; the Pozícia card and table column instead use `gpsTime` from
 *     /last-positions (cached 10s) which is the relevant freshness signal.
 *   - /current-tacho/{id} is fetched on row select, not cached.
 *
 * Status derivation (sidebar dots, table badges, Stav vozidla card):
 *   green  Ide          ignitionOn && speedKmh > 0
 *   amber  Beží         ignitionOn && speedKmh == 0  (idling)
 *   slate  Stojí        !ignitionOn
 *   red    Bez signálu  gpsTime older than 7 days
 *   slate  Bez údajov   no position record at all
 */

type ViewMode = 'detail' | 'prehlad';

type VehicleStatusKind = 'moving' | 'idle' | 'parked' | 'stale' | 'unknown';

interface VehicleStatus {
  kind: VehicleStatusKind;
  label: string;
  /** Tailwind background class for a status dot, e.g. "bg-green-500". */
  dotClass: string;
}

@Component({
  selector: 'app-commander',
  imports: [NavbarComponent, SpinnerComponent, DatePipe, DecimalPipe],
  templateUrl: './commander.page.html',
})
export class CommanderPage implements OnInit, OnDestroy {
  private commander = inject(CommanderService);
  flags = inject(FeatureFlagService);
  auth = inject(AuthService);

  enabled = computed(() => this.flags.commanderIntegration() || this.auth.isSuperAdmin());

  view = signal<ViewMode>('detail');

  loading = signal(false);
  error = signal<CommanderError | null>(null);

  vehicles = signal<CommanderVehicle[]>([]);
  positionsById = signal<Record<string, CommanderPosition>>({});
  /**
   * Per-vehicle stats batch keyed by vehicleId. Loaded lazily — only when
   * the user opens the Prehlad tab — because the underlying /fleet-stats
   * endpoint fans out to N×~4 Commander calls (tacho + paged /rides per
   * vehicle). Detail-only sessions never pay that cost.
   */
  fleetStatsById = signal<Record<string, CommanderFleetVehicleStats>>({});
  fleetStatsLoading = signal(false);
  /**
   * Resolved street/place labels per vehicleId, fetched lazily via the
   * /api/commander/reverse-geocode proxy (OpenRouteService Pelias geocoder).
   * Used in the Prehlad / Detail "Poloha" cells and the live-position map
   * tooltip when Commander itself didn't return an address.
   *
   * Deduped client-side by rounded coords (3 decimals ≈ 110 m) — a vehicle
   * moving 5 m doesn't trigger a new HTTP call. The backend caches at the
   * same precision for 7 days, so a hit on either side is cheap.
   */
  addressesByVehicleId = signal<Record<string, string>>({});
  /**
   * Coord rounding state: vehicleId → "lat3,lon3" of the last call we made,
   * so positions that round to the same cell don't refire. Reset by load().
   */
  private lastResolvedCoordKeyByVehicleId = new Map<string, string>();

  selectedVehicleId = signal<string | null>(null);
  selectedTacho = signal<CommanderTacho | null>(null);
  selectedTachoLoading = signal(false);
  selectedRideSummary = signal<CommanderRideSummary | null>(null);
  selectedRideSummaryLoading = signal(false);
  selectedRecentRides = signal<CommanderRideDetail[]>([]);
  selectedRecentRidesLoading = signal(false);

  /**
   * When non-null, the map enters "ride mode" — instead of one marker at the
   * vehicle's current position it shows green start + red stop markers and
   * fits bounds to both. Cleared by selecting a different vehicle or clicking
   * "Späť na živú polohu".
   */
  selectedRideId = signal<string | null>(null);

  selectedRide = computed<CommanderRideDetail | null>(() => {
    const id = this.selectedRideId();
    if (!id) return null;
    return this.selectedRecentRides().find(r => r.rideId === id) ?? null;
  });

  /**
   * Road-snapped polyline for the currently-selected ride, fetched lazily
   * via the backend /api/commander/snap-route endpoint. null while the
   * fetch is in flight, while no ride is selected, or whenever snapping
   * isn't available (no API key, ORS outage, "no route found"). The map
   * draws a dashed straight line in those cases — same as before snapping
   * was added.
   */
  selectedRideSnapped = signal<CommanderSnappedRoute | null>(null);

  // Live position polling. When `liveRefresh` is on we re-fetch /last-positions
  // every 10 s, matching the backend's cache TTL so we burn zero extra
  // Commander API quota. Customer asked for tighter live updates (originally
  // 30 s); 10 s is still well within the 300-req/window rate limit (one
  // /last-positions call per 10 s = 6/min, plus on-select tacho/rides).
  // Marker updates flow through the same effect that drives initial map
  // render, so no separate redraw logic is needed.
  liveRefresh = signal(false);
  lastRefreshAt = signal<Date | null>(null);
  private refreshTimerId?: ReturnType<typeof setInterval>;
  private static readonly LIVE_REFRESH_INTERVAL_MS = 10_000;

  selectedVehicle = computed(() => {
    const id = this.selectedVehicleId();
    return id ? this.vehicles().find(v => v.vehicleId === id) ?? null : null;
  });

  selectedPosition = computed(() => {
    const id = this.selectedVehicleId();
    return id ? this.positionsById()[id] ?? null : null;
  });

  statusCounts = computed(() => {
    const counts: Record<VehicleStatusKind, number> = {
      moving: 0, idle: 0, parked: 0, stale: 0, unknown: 0,
    };
    const map = this.positionsById();
    for (const v of this.vehicles()) {
      counts[this.statusFor(map[v.vehicleId]).kind]++;
    }
    return counts;
  });

  // Map containers — one per tab. The active tab's element is non-null;
  // the inactive one's is undefined because Angular has removed it from the DOM.
  mapDetailContainer = viewChild<ElementRef<HTMLDivElement>>('mapDetail');
  mapPrehladContainer = viewChild<ElementRef<HTMLDivElement>>('mapPrehlad');

  private leafletMap?: L.Map;
  /** Single marker for live-position mode. Removed on tear-down or switch to ride mode. */
  private leafletMarker?: L.Marker;
  /** Pair of markers for ride mode: [start, stop]. Removed on tear-down or switch to live mode. */
  private leafletRideMarkers?: [L.Marker, L.Marker];
  /**
   * Dashed line connecting the ride's start and stop. Deliberately dashed so it
   * reads as "approximate" — the Commander API doesn't expose the actual driven
   * route, so this is a straight-line indicator only.
   */
  private leafletRidePolyline?: L.Polyline;

  // Tracks what the map is currently centred on so we can decide when to
  // pan/zoom. Vehicle / ride changes use a hard `setView` (snaps + resets
  // zoom). When live mode is on, position changes do a smooth `panTo` so
  // the map "follows the dot" without resetting the user's zoom level.
  // null after tearDownMap.
  private lastRenderedVehicleId: string | null = null;
  private lastRenderedRideId: string | null = null;
  private lastRenderedLat: number | null = null;
  private lastRenderedLon: number | null = null;
  /**
   * Remember whether the last ride render used a snapped polyline. When the
   * snap response arrives mid-ride we want to refit the bounds to the full
   * real path; without this flag we'd compare current-vs-last by ride id
   * only and miss the upgrade.
   */
  private lastRenderedSnapped: CommanderSnappedRoute | null = null;

  constructor() {
    // Single effect handles map lifecycle: which container to mount onto,
    // what mode to render (live vs ride), and when to tear down. Re-runs
    // whenever any of the read signals change.
    effect(() => {
      const v = this.view();
      const el = v === 'detail'
        ? this.mapDetailContainer()?.nativeElement
        : this.mapPrehladContainer()?.nativeElement;
      const ride = this.selectedRide();
      const livePos = this.selectedPosition();
      // Read selectedRideSnapped() so the effect re-runs (and the polyline
      // upgrades from dashed to solid) the moment the snap response arrives.
      const snapped = this.selectedRideSnapped();
      this.syncMap(el, ride, livePos, snapped);
    });

    // Lazy fleet-stats: only load the heavy /fleet-stats endpoint when the
    // user is on the Prehlad tab and we haven't loaded it yet (or it was
    // cleared by an Obnoviť). Saves N×4 Commander calls per page open for
    // Detail-only sessions, which is the typical access pattern.
    effect(() => {
      const view = this.view();
      const hasStats = Object.keys(this.fleetStatsById()).length > 0;
      const loading = this.fleetStatsLoading();
      if (view === 'prehlad' && !hasStats && !loading && this.enabled()
          && this.vehicles().length > 0) {
        this.triggerFleetStatsLoad();
      }
    });

    // View-aware reverse geocoding. Detail tab only needs the selected
    // vehicle's address (shown on the location card + map tooltip); Prehlad
    // tab needs every visible row. This effect re-runs on every position
    // update, every vehicle selection, every tab switch — the
    // maybeResolveAddressFor() dedupe map keeps the cost cheap.
    effect(() => {
      const view = this.view();
      const positions = this.positionsById();
      const selectedId = this.selectedVehicleId();
      if (view === 'prehlad') {
        for (const p of Object.values(positions)) {
          this.maybeResolveAddressFor(p);
        }
      } else if (selectedId) {
        const p = positions[selectedId];
        if (p) this.maybeResolveAddressFor(p);
      }
    });
  }

  ngOnInit() {
    if (this.enabled()) this.load();
  }

  ngOnDestroy() {
    this.stopLiveRefresh();
    this.tearDownMap();
  }

  toggleLiveRefresh() {
    if (this.liveRefresh()) this.stopLiveRefresh();
    else this.startLiveRefresh();
  }

  private startLiveRefresh() {
    this.stopLiveRefresh();
    this.liveRefresh.set(true);
    // Fire one refresh immediately so the user sees activity instantly.
    this.refreshPositionsOnly();
    this.refreshTimerId = setInterval(
      () => this.refreshPositionsOnly(),
      CommanderPage.LIVE_REFRESH_INTERVAL_MS,
    );
  }

  private stopLiveRefresh() {
    this.liveRefresh.set(false);
    if (this.refreshTimerId !== undefined) {
      clearInterval(this.refreshTimerId);
      this.refreshTimerId = undefined;
    }
  }

  /**
   * Re-fetches /last-positions only (not /vehicles, those rarely change and
   * are cached 24h on the backend per spec). Errors are swallowed so a single
   * transient failure doesn't tear down live mode — the next tick will retry.
   *
   * Reverse-geocoding is NOT invoked from here; the view-aware effect in the
   * constructor reads positionsById() and triggers maybeResolveAddressFor()
   * for the right set of vehicles based on which tab is active.
   */
  private refreshPositionsOnly() {
    if (!this.enabled()) return;
    this.commander.getLastPositions().subscribe({
      next: positions => {
        const map: Record<string, CommanderPosition> = {};
        for (const p of positions) {
          if (p.vehicleId) map[p.vehicleId] = p;
        }
        this.positionsById.set(map);
        this.lastRefreshAt.set(new Date());
      },
      error: () => {
        // Skip this tick. Don't tear down — give the next interval a chance.
      },
    });
  }

  /**
   * Resolve one vehicle's coords to an address label if we haven't already
   * for that 3-decimal coord cell (≈ 110 m). Called by the view-aware
   * reverse-geocoding effect — never directly. Dedupe map keeps cost
   * negligible across re-runs.
   *
   * Resolution failures are silent — the address simply stays at the last
   * good value (or absent), and the UI falls back to coords as it does
   * when no API key is configured.
   */
  private maybeResolveAddressFor(p: CommanderPosition) {
    if (!p.vehicleId || p.latitude == null || p.longitude == null) return;
    const key = `${p.latitude.toFixed(3)},${p.longitude.toFixed(3)}`;
    if (this.lastResolvedCoordKeyByVehicleId.get(p.vehicleId) === key) return;
    this.lastResolvedCoordKeyByVehicleId.set(p.vehicleId, key);
    const vehicleId = p.vehicleId;
    this.commander.reverseGeocode(p.latitude, p.longitude).subscribe(label => {
      if (!label) return;
      this.addressesByVehicleId.update(curr => ({ ...curr, [vehicleId]: label }));
    });
  }

  /** Look up the latest resolved address for a vehicle, or null. */
  resolvedAddressFor(vehicleId: string | null | undefined): string | null {
    if (!vehicleId) return null;
    return this.addressesByVehicleId()[vehicleId] ?? null;
  }

  load() {
    if (!this.enabled()) return;
    this.loading.set(true);
    this.error.set(null);
    this.vehicles.set([]);
    this.positionsById.set({});
    // Drop any cached fleet stats so the lazy effect re-fires when the
    // user is on / switches to the Prehlad tab.
    this.fleetStatsById.set({});
    this.addressesByVehicleId.set({});
    this.lastResolvedCoordKeyByVehicleId.clear();
    this.selectedVehicleId.set(null);
    this.selectedTacho.set(null);

    // Fast path: only fetch what's needed to paint the page. fleet-stats
    // (heavy) is loaded lazily by an effect when the Prehlad tab is opened.
    // Reverse-geocoding is also driven by an effect — only the visible
    // vehicle(s) get resolved, not every vehicle on every load.
    forkJoin({
      vehicles: this.commander.getVehicles(),
      positions: this.commander
        .getLastPositions()
        .pipe(catchError(() => of([] as CommanderPosition[]))),
    }).subscribe({
      next: ({ vehicles, positions }) => {
        const sorted = [...vehicles].sort((a, b) =>
          (a.name ?? '').localeCompare(b.name ?? '', 'sk'),
        );
        this.vehicles.set(sorted);
        const map: Record<string, CommanderPosition> = {};
        for (const p of positions) {
          if (p.vehicleId) map[p.vehicleId] = p;
        }
        this.positionsById.set(map);
        this.loading.set(false);

        if (sorted.length > 0) this.select(sorted[0].vehicleId);
      },
      error: (err: CommanderError) => {
        this.loading.set(false);
        this.error.set(err);
      },
    });
  }

  /**
   * Idempotent fleet-stats fetch. Re-runs on Obnoviť (load() clears the
   * stats map → the Prehlad effect detects "empty" and calls back here).
   * Failure is non-fatal: Tachometer / Štatistiky cells just stay '—'.
   */
  private triggerFleetStatsLoad() {
    if (this.fleetStatsLoading()) return;
    this.fleetStatsLoading.set(true);
    this.commander
      .getFleetStats()
      .pipe(catchError(() => of([] as CommanderFleetVehicleStats[])))
      .subscribe(fleetStats => {
        const stats: Record<string, CommanderFleetVehicleStats> = {};
        for (const s of fleetStats) {
          if (s.vehicleId) stats[s.vehicleId] = s;
        }
        this.fleetStatsById.set(stats);
        this.fleetStatsLoading.set(false);
      });
  }

  /** Look up the cached fleet-stats row for a vehicleId (or null). */
  fleetStatsFor(vehicleId: string): CommanderFleetVehicleStats | null {
    return this.fleetStatsById()[vehicleId] ?? null;
  }

  /**
   * Lat/lon coordinates as the table-row "Aktuálna poloha" cell. 4 decimal
   * places ≈ 11 m. Always coords (not address) — the address goes onto the
   * live-position map marker tooltip instead, where the manager can see the
   * full street name without making the table column wider. Returns
   * em-dash when no GPS at all.
   */
  formatCoords(p: CommanderPosition | null | undefined): string {
    if (!p || p.latitude == null || p.longitude == null) return '—';
    return `${p.latitude.toFixed(4)}, ${p.longitude.toFixed(4)}`;
  }

  /**
   * Best human-readable label for a position, in priority order:
   *   1. Commander's own <c>address</c> field (when the customer's account
   *      has address resolution enabled — this customer's currently does not).
   *   2. The OpenRouteService reverse-geocoded label cached in
   *      addressesByVehicleId (resolved client-side via /reverse-geocode).
   *   3. Coords as the final fallback so the cell is never empty.
   */
  locationLabel(p: CommanderPosition | null | undefined): string {
    if (!p) return '—';
    if (p.address && p.address.trim().length > 0) return p.address;
    const resolved = this.resolvedAddressFor(p.vehicleId);
    if (resolved) return resolved;
    return this.formatCoords(p);
  }

  /**
   * Map-marker tooltip text. Same priority order as locationLabel(); empty
   * string when no GPS at all so Leaflet can omit the tooltip cleanly.
   */
  locationTooltip(p: CommanderPosition | null | undefined): string {
    if (!p) return '';
    return this.locationLabel(p);
  }

  /**
   * Compact distance + duration line for the Prehlad Štatistiky cell.
   * Avg speed lives in its own Priemerná rýchlosť column now, so this
   * is just "22,6 km · 1h 59m". Em-dash for empty buckets.
   */
  formatBucket(b: CommanderRideBucket | null | undefined): string {
    if (!b || b.rideCount === 0) return '—';
    const km = (b.distanceKm ?? 0).toLocaleString('sk-SK', { maximumFractionDigits: 1 });
    const dur = this.formatDurationCompact(b.durationSeconds ?? 0);
    return `${km} km · ${dur}`;
  }

  /**
   * Avg speed as a standalone "11 km/h" / "32 km/h" string for the
   * Priemerná rýchlosť column. Em-dash for empty buckets so the column
   * doesn't show "0 km/h" for vehicles that didn't drive in the period.
   */
  formatBucketAvg(b: CommanderRideBucket | null | undefined): string {
    if (!b || b.rideCount === 0) return '—';
    const avg = (b.avgSpeedKmh ?? 0).toLocaleString('sk-SK', { maximumFractionDigits: 0 });
    return `${avg} km/h`;
  }

  /**
   * Tight duration formatting for table cells: "1h 59m" / "59m" / "0m".
   * formatDuration() uses the longer "h"/" min" wording — fine for the
   * Detail-tab card, too wide for the Prehlad Štatistiky column.
   */
  private formatDurationCompact(seconds: number): string {
    if (seconds <= 0) return '0m';
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    if (h > 0) return m > 0 ? `${h}h ${m}m` : `${h}h`;
    return `${m}m`;
  }

  select(vehicleId: string) {
    if (!vehicleId) return;
    this.selectedVehicleId.set(vehicleId);
    // Switching vehicles always exits ride mode — the previously-selected
    // ride belongs to a different vehicle and shouldn't carry over.
    this.selectedRideId.set(null);

    this.selectedTacho.set(null);
    this.selectedTachoLoading.set(true);
    this.commander.getCurrentTacho(vehicleId).subscribe({
      next: t => {
        this.selectedTacho.set(t);
        this.selectedTachoLoading.set(false);
      },
      error: () => {
        // Tacho is best-effort; never block the detail view if it fails.
        this.selectedTachoLoading.set(false);
      },
    });

    this.selectedRideSummary.set(null);
    this.selectedRideSummaryLoading.set(true);
    this.commander.getRideSummary(vehicleId).subscribe({
      next: s => {
        this.selectedRideSummary.set(s);
        this.selectedRideSummaryLoading.set(false);
      },
      error: () => {
        // Ride summary is best-effort too — the rest of the panel still renders.
        this.selectedRideSummaryLoading.set(false);
      },
    });

    this.selectedRecentRides.set([]);
    this.selectedRecentRidesLoading.set(true);
    this.commander.getRecentRides(vehicleId).subscribe({
      next: rides => {
        this.selectedRecentRides.set(rides);
        this.selectedRecentRidesLoading.set(false);
      },
      error: () => {
        this.selectedRecentRidesLoading.set(false);
      },
    });
  }

  selectRide(rideId: string) {
    const ride = this.selectedRecentRides().find(r => r.rideId === rideId);
    // Don't enter ride mode for private rides — Commander returns null coords
    // for them, so there's nothing to plot.
    if (!ride || ride.latStart == null || ride.latStop == null ||
        ride.lonStart == null || ride.lonStop == null) return;
    this.selectedRideId.set(rideId);
    // Clear any stale snapped route from the previously-selected ride so the
    // map immediately falls back to the dashed straight line while the new
    // snap call is in flight (which is also the correct end state if the
    // call returns null). Then fetch the new one and let the effect redraw.
    this.selectedRideSnapped.set(null);
    this.commander
      .snapRoute(ride.latStart, ride.lonStart, ride.latStop, ride.lonStop)
      .subscribe(snap => {
        // Only apply if the user hasn't navigated away to a different ride
        // in the meantime — otherwise we'd flash the wrong polyline.
        if (this.selectedRideId() === rideId) this.selectedRideSnapped.set(snap);
      });
    // Smooth-scroll the map into view. On phones and narrow viewports the
    // user has scrolled down to the rides list to pick a ride; without this,
    // the map updates above the fold and they'd have to scroll back up by
    // hand to see it. block:'nearest' is a no-op on wide viewports where
    // the map is already on screen, so desktop UX is unchanged.
    // setTimeout(0) defers past the change-detection cycle that may swap
    // the map's @if branch from "live coords" to "ride coords", keeping
    // the viewChild ref valid.
    setTimeout(() => {
      this.mapDetailContainer()?.nativeElement.scrollIntoView({
        behavior: 'smooth',
        block: 'nearest',
      });
    }, 0);
  }

  clearRide() {
    this.selectedRideId.set(null);
    this.selectedRideSnapped.set(null);
  }

  /** True when the selected ride has plottable coords. */
  rideHasCoords(r: CommanderRideDetail | null | undefined): boolean {
    return !!r && r.latStart != null && r.lonStart != null && r.latStop != null && r.lonStop != null;
  }

  /**
   * Format a duration in seconds as a Slovak-readable string:
   *   0           → "0 s"
   *   45          → "45 s"
   *   90          → "1 min"
   *   3600        → "1 h"
   *   5400        → "1 h 30 min"
   *   86400       → "24 h"
   * Seconds are dropped once the value exceeds a minute, since the underlying
   * Commander durations are always at least minute-precise in practice.
   */
  formatDuration(seconds: number): string {
    if (seconds <= 0) return '0 s';
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    if (h > 0) return m > 0 ? `${h} h ${m} min` : `${h} h`;
    if (m > 0) return `${m} min`;
    return `${seconds} s`;
  }

  setView(v: ViewMode) {
    // Switching to Prehlad while a ride is selected: drop the ride so the
    // shared map renders the selected vehicle's live position instead of
    // staying parked on a ride pin the user is no longer looking at.
    // Switching back to Detail keeps state untouched — if the user wants
    // the same ride they had open, they can re-pick from the rides list.
    if (v === 'prehlad') {
      this.selectedRideId.set(null);
      this.selectedRideSnapped.set(null);
    }
    this.view.set(v);
  }

  statusFor(p: CommanderPosition | null | undefined): VehicleStatus {
    if (!p) return { kind: 'unknown', label: 'Bez údajov', dotClass: 'bg-slate-400' };
    const ageMs = p.gpsTime
      ? Date.now() - new Date(p.gpsTime).getTime()
      : Number.MAX_SAFE_INTEGER;
    const stale = ageMs > 7 * 24 * 60 * 60 * 1000;
    if (stale) return { kind: 'stale', label: 'Bez signálu', dotClass: 'bg-red-500' };
    const speed = p.speedKmh ?? 0;
    // Customer feedback: "Ide" / "Beží" was confusing. Renamed to
    // "V pohybe" (in motion) and "Zapnuté zapaľovanie" (engine on but
    // stationary, e.g. idling at a job site).
    if (p.ignitionOn && speed > 0) return { kind: 'moving', label: 'V pohybe', dotClass: 'bg-green-500' };
    if (p.ignitionOn) return { kind: 'idle', label: 'Zapnuté zapaľovanie', dotClass: 'bg-amber-500' };
    return { kind: 'parked', label: 'Stojí', dotClass: 'bg-slate-400' };
  }

  status(vehicleId: string): VehicleStatus {
    return this.statusFor(this.positionsById()[vehicleId]);
  }

  position(vehicleId: string): CommanderPosition | undefined {
    return this.positionsById()[vehicleId];
  }

  /** Google Maps deep link — used for the "Zobraziť v novom okne" button. */
  mapHref(): string | null {
    const p = this.selectedPosition();
    if (!p || p.latitude == null || p.longitude == null) return null;
    return `https://www.google.com/maps?q=${p.latitude},${p.longitude}`;
  }

  // -----------------------------------------------------------------
  // Leaflet map lifecycle
  // -----------------------------------------------------------------

  private syncMap(
    el: HTMLElement | undefined,
    ride: CommanderRideDetail | null,
    livePos: CommanderPosition | null,
    snapped: CommanderSnappedRoute | null,
  ) {
    // Decide which mode to render. Ride mode wins when a ride is selected
    // and has plottable coords (private rides have nulls — they fall back).
    const rideHasCoords = !!ride && ride.latStart != null && ride.lonStart != null && ride.latStop != null && ride.lonStop != null;
    const liveHasCoords = !!livePos && livePos.latitude != null && livePos.longitude != null;

    // Nothing to render → tear everything down.
    if (!el || (!rideHasCoords && !liveHasCoords)) {
      this.tearDownMap();
      return;
    }

    // Container changed (tab switch) → tear down so we can re-mount.
    if (this.leafletMap && this.leafletMap.getContainer() !== el) {
      this.tearDownMap();
    }

    if (!this.leafletMap) {
      // Pick an arbitrary initial centre — it'll be overridden immediately below.
      const initial: L.LatLngTuple = rideHasCoords
        ? [ride!.latStart!, ride!.lonStart!]
        : [livePos!.latitude!, livePos!.longitude!];
      this.leafletMap = L.map(el, {
        zoomControl: true,
        attributionControl: true,
      }).setView(initial, 14);

      // Drop Leaflet's default prefix ("Leaflet" + a Ukrainian-flag emoji baked
      // into the library since 2022). We keep the OSM attribution because the
      // OSM tile-usage policy requires it; we just don't show Leaflet's own
      // branding + flag, which is unrelated to the customer's product.
      // NB: Leaflet 1.9 also accepts `false` at runtime to suppress the prefix
      // entirely, but @types/leaflet only types it as `string`. An empty
      // string achieves the same end result.
      this.leafletMap.attributionControl.setPrefix('');

      L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
        attribution: '© OpenStreetMap contributors',
        maxZoom: 19,
      }).addTo(this.leafletMap);
    }

    const currentVehicleId = livePos?.vehicleId ?? null;
    const currentRideId = ride?.rideId ?? null;

    if (rideHasCoords) {
      // Switching INTO ride mode: drop the live marker if present.
      if (this.leafletMarker) {
        this.leafletMarker.remove();
        this.leafletMarker = undefined;
      }
      // fitBounds when the ride id has actually changed (or first render),
      // OR when a snapped polyline just arrived for the same ride (so the
      // map zooms to the full real path, not just the start/stop straight
      // line). The latter is tracked via lastRenderedSnapped.
      const snapJustArrived = snapped != null && this.lastRenderedSnapped == null
        && currentRideId === this.lastRenderedRideId;
      const recenter = currentRideId !== this.lastRenderedRideId || snapJustArrived;
      this.renderRideMarkers(ride!, snapped, recenter);
      this.lastRenderedSnapped = snapped;
    } else {
      // Switching INTO live mode: drop ride markers and the connecting line.
      if (this.leafletRideMarkers) {
        for (const m of this.leafletRideMarkers) m.remove();
        this.leafletRideMarkers = undefined;
      }
      if (this.leafletRidePolyline) {
        this.leafletRidePolyline.remove();
        this.leafletRidePolyline = undefined;
      }
      // Recenter (hard setView, resets zoom) when the vehicle changed or we
      // just exited ride mode. Otherwise: if live mode is on and the dot
      // actually moved, smooth-pan to it ("follow the dot"); if live mode
      // is off, leave the camera where the user put it.
      const recenter =
        currentVehicleId !== this.lastRenderedVehicleId ||
        this.lastRenderedRideId !== null;
      const positionChanged =
        livePos!.latitude !== this.lastRenderedLat ||
        livePos!.longitude !== this.lastRenderedLon;
      const follow = !recenter && this.liveRefresh() && positionChanged;
      this.renderLiveMarker(livePos!, recenter, follow);
      this.lastRenderedLat = livePos!.latitude!;
      this.lastRenderedLon = livePos!.longitude!;
    }

    this.lastRenderedVehicleId = currentVehicleId;
    this.lastRenderedRideId = currentRideId;

    setTimeout(() => this.leafletMap?.invalidateSize(), 50);
  }

  private renderLiveMarker(p: CommanderPosition, recenter: boolean, follow: boolean) {
    if (!this.leafletMap) return;
    const lat = p.latitude!;
    const lon = p.longitude!;
    // Tooltip text = full address (when Commander has it) or coords as a
    // fallback. Updated on every render so a moving vehicle's tooltip
    // tracks its current address.
    const tooltipText = this.locationTooltip(p);
    if (this.leafletMarker) {
      this.leafletMarker.setLatLng([lat, lon]);
      const existing = this.leafletMarker.getTooltip();
      if (existing) {
        existing.setContent(tooltipText);
      } else if (tooltipText) {
        this.leafletMarker.bindTooltip(tooltipText, {
          direction: 'top',
          offset: [0, -10],
          className: 'commander-ride-tooltip',
        });
      }
    } else {
      const icon = L.divIcon({
        className: 'commander-marker',
        html: '<div class="cm-pin"></div>',
        iconSize: [22, 22],
        iconAnchor: [11, 11],
      });
      this.leafletMarker = L.marker([lat, lon], { icon }).addTo(this.leafletMap);
      if (tooltipText) {
        // Hover-only tooltip on desktop; on touch the tooltip flashes when
        // the marker is tapped, which is the closest Leaflet equivalent of
        // a popup-on-tap without adding an explicit popup layer.
        this.leafletMarker.bindTooltip(tooltipText, {
          direction: 'top',
          offset: [0, -10],
          className: 'commander-ride-tooltip',
        });
      }
    }
    if (recenter) {
      // Hard snap on vehicle change / ride exit — also resets zoom to 14.
      this.leafletMap.setView([lat, lon], 14);
    } else if (follow) {
      // Smooth pan, keep current zoom so the user's view is preserved.
      this.leafletMap.panTo([lat, lon], { animate: true, duration: 0.5 });
    }
  }

  private renderRideMarkers(
    ride: CommanderRideDetail,
    snapped: CommanderSnappedRoute | null,
    fitBoundsNow: boolean,
  ) {
    if (!this.leafletMap) return;
    const start: L.LatLngTuple = [ride.latStart!, ride.lonStart!];
    const stop: L.LatLngTuple = [ride.latStop!, ride.lonStop!];

    // Markers (start + stop pills) — same regardless of snapping mode.
    if (this.leafletRideMarkers) {
      this.leafletRideMarkers[0].setLatLng(start);
      this.leafletRideMarkers[1].setLatLng(stop);
    } else {
      const startIcon = L.divIcon({
        className: 'commander-marker',
        html: '<div class="cm-pin cm-pin-start"></div>',
        iconSize: [22, 22],
        iconAnchor: [11, 11],
      });
      const stopIcon = L.divIcon({
        className: 'commander-marker',
        html: '<div class="cm-pin cm-pin-stop"></div>',
        iconSize: [22, 22],
        iconAnchor: [11, 11],
      });
      const startMarker = L.marker(start, { icon: startIcon }).addTo(this.leafletMap);
      const stopMarker = L.marker(stop, { icon: stopIcon }).addTo(this.leafletMap);
      // Permanent pill labels so the start/stop role is visible without hover.
      // Full addresses are intentionally not in the tooltip (they'd clutter the
      // map); they're already visible in the ride list below.
      startMarker.bindTooltip('Štart', {
        permanent: true,
        direction: 'top',
        offset: [0, -10],
        className: 'commander-ride-tooltip commander-ride-tooltip-start',
      });
      stopMarker.bindTooltip('Cieľ', {
        permanent: true,
        direction: 'top',
        offset: [0, -10],
        className: 'commander-ride-tooltip commander-ride-tooltip-stop',
      });
      this.leafletRideMarkers = [startMarker, stopMarker];
    }

    // Build the polyline points and style:
    //   - If we have a snapped route from ORS, draw the full road path
    //     as a solid amber line (best-guess driven path, not ground truth).
    //   - Otherwise fall back to the dashed straight line — same look as
    //     before snapping was added, so an outage / no-key environment is
    //     visually identical to the pre-snapping behaviour.
    // ORS coords are [lon, lat] (GeoJSON); Leaflet wants [lat, lon].
    let linePoints: L.LatLngTuple[];
    let lineOpts: L.PolylineOptions;
    if (snapped && snapped.coordinates.length >= 2) {
      linePoints = snapped.coordinates.map(
        ([lon, lat]) => [lat, lon] as L.LatLngTuple,
      );
      lineOpts = {
        color: '#f59e0b',  // amber-500
        weight: 4,
        opacity: 0.9,
      };
    } else {
      linePoints = [start, stop];
      lineOpts = {
        color: '#f59e0b',
        weight: 3,
        opacity: 0.75,
        dashArray: '8, 8',
      };
    }

    if (this.leafletRidePolyline) {
      this.leafletRidePolyline.setLatLngs(linePoints);
      this.leafletRidePolyline.setStyle(lineOpts);
    } else {
      this.leafletRidePolyline = L.polyline(linePoints, lineOpts).addTo(this.leafletMap);
    }

    if (fitBoundsNow) {
      // Bounds: include every polyline point so a long detour fits, not
      // just the start/stop endpoints.
      this.leafletMap.fitBounds(
        L.latLngBounds(linePoints),
        { padding: [40, 40], maxZoom: 15 },
      );
    }
  }

  private tearDownMap() {
    if (this.leafletMarker) {
      this.leafletMarker.remove();
      this.leafletMarker = undefined;
    }
    if (this.leafletRideMarkers) {
      for (const m of this.leafletRideMarkers) m.remove();
      this.leafletRideMarkers = undefined;
    }
    if (this.leafletRidePolyline) {
      this.leafletRidePolyline.remove();
      this.leafletRidePolyline = undefined;
    }
    if (this.leafletMap) {
      this.leafletMap.remove();
      this.leafletMap = undefined;
    }
    // Reset render tracking — next render is effectively a fresh map and
    // the recenter checks should treat it as such.
    this.lastRenderedVehicleId = null;
    this.lastRenderedRideId = null;
    this.lastRenderedLat = null;
    this.lastRenderedLon = null;
    this.lastRenderedSnapped = null;
  }
}
