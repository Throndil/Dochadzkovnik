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
import { AuthService } from '../../services/auth.service';
import { FeatureFlagService } from '../../services/feature-flag.service';
import {
  CommanderError,
  CommanderPosition,
  CommanderRideDetail,
  CommanderRideSummary,
  CommanderService,
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
  imports: [NavbarComponent, DatePipe, DecimalPipe],
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
      this.syncMap(el, ride, livePos);
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

  load() {
    if (!this.enabled()) return;
    this.loading.set(true);
    this.error.set(null);
    this.vehicles.set([]);
    this.positionsById.set({});
    this.selectedVehicleId.set(null);
    this.selectedTacho.set(null);

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
    if (!ride || ride.latStart == null || ride.latStop == null) return;
    this.selectedRideId.set(rideId);
  }

  clearRide() {
    this.selectedRideId.set(null);
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
    this.view.set(v);
  }

  statusFor(p: CommanderPosition | null | undefined): VehicleStatus {
    if (!p) return { kind: 'unknown', label: 'Bez údajov', dotClass: 'bg-slate-400' };
    const ageMs = p.gpsTimeUtc
      ? Date.now() - new Date(p.gpsTimeUtc).getTime()
      : Number.MAX_SAFE_INTEGER;
    const stale = ageMs > 7 * 24 * 60 * 60 * 1000;
    if (stale) return { kind: 'stale', label: 'Bez signálu', dotClass: 'bg-red-500' };
    const speed = p.speedKmh ?? 0;
    if (p.ignitionOn && speed > 0) return { kind: 'moving', label: 'Ide', dotClass: 'bg-green-500' };
    if (p.ignitionOn) return { kind: 'idle', label: 'Beží', dotClass: 'bg-amber-500' };
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
      // fitBounds when the ride id has actually changed (or first render).
      const recenter = currentRideId !== this.lastRenderedRideId;
      this.renderRideMarkers(ride!, recenter);
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
    if (this.leafletMarker) {
      this.leafletMarker.setLatLng([lat, lon]);
    } else {
      const icon = L.divIcon({
        className: 'commander-marker',
        html: '<div class="cm-pin"></div>',
        iconSize: [22, 22],
        iconAnchor: [11, 11],
      });
      this.leafletMarker = L.marker([lat, lon], { icon }).addTo(this.leafletMap);
    }
    if (recenter) {
      // Hard snap on vehicle change / ride exit — also resets zoom to 14.
      this.leafletMap.setView([lat, lon], 14);
    } else if (follow) {
      // Smooth pan, keep current zoom so the user's view is preserved.
      this.leafletMap.panTo([lat, lon], { animate: true, duration: 0.5 });
    }
  }

  private renderRideMarkers(ride: CommanderRideDetail, fitBoundsNow: boolean) {
    if (!this.leafletMap) return;
    const start: L.LatLngTuple = [ride.latStart!, ride.lonStart!];
    const stop: L.LatLngTuple = [ride.latStop!, ride.lonStop!];

    // Markers
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

    // Connecting line. Dashed so it reads as "approximate / not the real route"
    // — the Commander v1 API doesn't expose per-second GPS samples, only the
    // ride's start and stop coordinates.
    if (this.leafletRidePolyline) {
      this.leafletRidePolyline.setLatLngs([start, stop]);
    } else {
      this.leafletRidePolyline = L.polyline([start, stop], {
        color: '#f59e0b',          // amber-500
        weight: 3,
        opacity: 0.75,
        dashArray: '8, 8',
      }).addTo(this.leafletMap);
    }

    if (fitBoundsNow) {
      this.leafletMap.fitBounds(L.latLngBounds(start, stop), { padding: [40, 40], maxZoom: 14 });
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
  }
}
