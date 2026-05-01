import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';

/**
 * Live read-only data fetched from the customer's Commander v1 fleet API,
 * via our backend proxy at /api/commander/*.
 *
 * The frontend never sees Commander credentials — those live in the API's
 * IConfiguration only. All errors arrive here as the Slovak
 * <code>CommanderError</code> shape; never propagate raw Commander error
 * strings to the user. See COMMANDER_PLAN.md.
 */

export interface CommanderVehicle {
  vehicleId: string;
  name?: string | null;
  registrationPlate?: string | null;
  vin?: string | null;
  /** ISO-8601 string. May be null if Commander has never received a packet from the unit. */
  lastCommunicationUtc?: string | null;
}

export interface CommanderVehicleDetail extends CommanderVehicle {
  model?: string | null;
  manufactureYear?: string | null;
  commissioningDate?: string | null;
  mainFuelType?: string | null;
  objectType?: string | null;
}

export interface CommanderPosition {
  vehicleId: string;
  /** ISO-8601 string. */
  gpsTimeUtc?: string | null;
  latitude?: number | null;
  longitude?: number | null;
  speedKmh?: number | null;
  ignitionOn?: boolean | null;
  voltageVolts?: number | null;
  address?: string | null;
}

export interface CommanderTacho {
  km?: number | null;
  engineHours?: number | null;
}

export interface CommanderRideBucket {
  rideCount: number;
  distanceKm: number;
  durationSeconds: number;
}

/**
 * Aggregated ride totals matching Commander's own "Prehľad jázd" panel:
 * Dnes / Včera / Posledných 7 dní / Tento mesiac / Minulý mesiac.
 * Bucketed server-side by ride startTime in Europe/Bratislava local time.
 *
 * <code>truncated</code> is true when the underlying paginated /rides call
 * hit our page cap (currently 5 pages = 500 rides for the ~31-day window) —
 * the totals are then lower bounds, not exact.
 */
export interface CommanderRideSummary {
  today: CommanderRideBucket;
  yesterday: CommanderRideBucket;
  last7Days: CommanderRideBucket;
  thisMonth: CommanderRideBucket;
  lastMonth: CommanderRideBucket;
  truncated: boolean;
}

/**
 * One ride entry. GPS coords + addresses are populated ONLY for
 * BUSINESS_RIDE — Commander does not expose them for PRIVAT_RIDE per the
 * v1 spec, so PRIVAT_RIDE rows render in the list but cannot be plotted on
 * the map.
 */
export interface CommanderRideDetail {
  rideId: string;
  startTimeUtc?: string | null;
  stopTimeUtc?: string | null;
  durationSeconds?: number | null;
  distanceKm?: number | null;
  avgSpeedKmh?: number | null;
  rideType?: string | null;
  driverName?: string | null;
  note?: string | null;
  latStart?: number | null;
  lonStart?: number | null;
  latStop?: number | null;
  lonStop?: number | null;
  startAddress?: string | null;
  stopAddress?: string | null;
}

/**
 * Typed error envelope. The Commander backend either returns 200 + body, or a
 * 4xx/5xx with this exact JSON shape. The service catches HttpErrorResponse
 * and re-throws this — components observe it via the rxjs error channel.
 */
export interface CommanderError {
  /** Slovak, user-facing. Safe to render verbatim. */
  error: string;
  /** Lowercase enum: 'ratelimited' | 'network' | 'notfound' | 'config' | 'badrequest' | 'invalidresponse' | 'servererror' | 'unknown' */
  code: string;
  /** Whether a retry might succeed (true for ratelimited, network, servererror). */
  retryable: boolean;
  /** When set (typically on 429), the suggested wait before retrying. */
  retryAfterSeconds?: number | null;
  /** Raw HTTP status. 0 means the request never reached the server. */
  httpStatus: number;
}

@Injectable({ providedIn: 'root' })
export class CommanderService {
  private http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiUrl}/commander`;

  /** Full vehicle list. Backend caches 24h per Commander spec guidance. */
  getVehicles(): Observable<CommanderVehicle[]> {
    return this.http
      .get<CommanderVehicle[]>(`${this.baseUrl}/vehicles`)
      .pipe(catchError(err => throwError(() => this.toCommanderError(err))));
  }

  getVehicle(vehicleId: string): Observable<CommanderVehicleDetail> {
    const enc = encodeURIComponent(vehicleId);
    return this.http
      .get<CommanderVehicleDetail>(`${this.baseUrl}/vehicles/${enc}`)
      .pipe(catchError(err => throwError(() => this.toCommanderError(err))));
  }

  /** Live GPS for every vehicle. Backend caches 30s. */
  getLastPositions(): Observable<CommanderPosition[]> {
    return this.http
      .get<CommanderPosition[]>(`${this.baseUrl}/last-positions`)
      .pipe(catchError(err => throwError(() => this.toCommanderError(err))));
  }

  /** Live odometer + engine-hours for one vehicle. Not cached. */
  getCurrentTacho(vehicleId: string): Observable<CommanderTacho> {
    const enc = encodeURIComponent(vehicleId);
    return this.http
      .get<CommanderTacho>(`${this.baseUrl}/vehicles/${enc}/current-tacho`)
      .pipe(catchError(err => throwError(() => this.toCommanderError(err))));
  }

  /** Five aggregate ride buckets for one vehicle. Backend caches 60s. */
  getRideSummary(vehicleId: string): Observable<CommanderRideSummary> {
    const enc = encodeURIComponent(vehicleId);
    return this.http
      .get<CommanderRideSummary>(`${this.baseUrl}/vehicles/${enc}/ride-summary`)
      .pipe(catchError(err => throwError(() => this.toCommanderError(err))));
  }

  /** Recent rides (last 7 days, newest first) for one vehicle. Backend caches 60s. */
  getRecentRides(vehicleId: string): Observable<CommanderRideDetail[]> {
    const enc = encodeURIComponent(vehicleId);
    return this.http
      .get<CommanderRideDetail[]>(`${this.baseUrl}/vehicles/${enc}/rides`)
      .pipe(catchError(err => throwError(() => this.toCommanderError(err))));
  }

  /**
   * Normalises a license plate so two strings can be compared as equal even
   * when one has spaces or dashes. Strips every non-alphanumeric character and
   * uppercases the result. e.g. "BA-010 AB" → "BA010AB".
   *
   * Used to pair our local Car.licensePlate with Commander's
   * vehicleRegistrationPlate. If/when we add a Car.CommanderVehicleId column
   * the matching logic moves there and this helper retires.
   */
  static normalisePlate(plate: string | null | undefined): string {
    if (!plate) return '';
    return plate.replace(/[^a-zA-Z0-9]/g, '').toUpperCase();
  }

  private toCommanderError(err: unknown): CommanderError {
    if (err instanceof HttpErrorResponse) {
      const body = err.error as Partial<CommanderError> | null;
      if (body && typeof body.error === 'string') {
        return {
          error: body.error,
          code: body.code ?? 'unknown',
          retryable: body.retryable ?? false,
          retryAfterSeconds: body.retryAfterSeconds ?? null,
          httpStatus: err.status,
        };
      }
      return {
        error: err.status === 0
          ? 'Pripojenie zlyhalo. Skontrolujte sieť a skúste znova.'
          : 'Commander momentálne nedostupný — pokus o obnovenie.',
        code: err.status === 0 ? 'network' : 'unknown',
        retryable: true,
        retryAfterSeconds: null,
        httpStatus: err.status,
      };
    }
    return {
      error: 'Neočakávaná chyba.',
      code: 'unknown',
      retryable: false,
      retryAfterSeconds: null,
      httpStatus: 0,
    };
  }
}
