import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { AuthService } from '../../services/auth.service';
import { FeatureFlagService } from '../../services/feature-flag.service';
import {
  CommanderError,
  CommanderPosition,
  CommanderService,
  CommanderTacho,
  CommanderVehicle,
} from '../../services/commander.service';

/**
 * Read-only Commander panel embedded on the car-detail page.
 *
 * Pairs the local Car (by license-plate match) with the matching Commander
 * vehicle and shows live odometer, last GPS, ignition, and last-communication
 * timestamp. Plate matching is intentionally cheap — see
 * <code>CommanderService.normalisePlate</code>. If/when we add a
 * <code>Car.CommanderVehicleId</code> column the matching logic moves to the
 * backend and this component just consumes the FK.
 *
 * Visibility: gated by the <code>CommanderIntegration</code> feature flag.
 * Superadmins always see the panel (for testing) — when they see it with the
 * flag off, a small "Skryté" badge labels that state.
 */
@Component({
  selector: 'app-commander-car-panel',
  imports: [DatePipe, DecimalPipe],
  templateUrl: './commander-car-panel.component.html',
})
export class CommanderCarPanelComponent implements OnInit {
  /** Driving input — license plate of the car being displayed. */
  licensePlate = input<string>('');

  private commander = inject(CommanderService);
  flags = inject(FeatureFlagService);
  auth = inject(AuthService);

  enabled = computed(() => this.flags.commanderIntegration() || this.auth.isSuperAdmin());

  loading = signal(false);
  error = signal<CommanderError | null>(null);

  vehicle = signal<CommanderVehicle | null>(null);
  tacho = signal<CommanderTacho | null>(null);
  position = signal<CommanderPosition | null>(null);
  noMatch = signal(false);

  ngOnInit() {
    if (this.enabled()) this.load();
  }

  load() {
    if (!this.enabled()) return;
    this.loading.set(true);
    this.error.set(null);
    this.noMatch.set(false);
    this.vehicle.set(null);
    this.tacho.set(null);
    this.position.set(null);

    const plateLocal = CommanderService.normalisePlate(this.licensePlate());
    if (!plateLocal) {
      this.loading.set(false);
      this.noMatch.set(true);
      return;
    }

    this.commander.getVehicles().subscribe({
      next: list => {
        const match = list.find(
          v => CommanderService.normalisePlate(v.registrationPlate) === plateLocal,
        );
        if (!match) {
          this.loading.set(false);
          this.noMatch.set(true);
          return;
        }
        this.vehicle.set(match);
        const id = match.vehicleId;

        // /current-tacho is per-vehicle, /last-positions returns the full fleet
        // and we filter client-side. Both fired in parallel; an error in one
        // is logged but doesn't block the other from rendering its data.
        forkJoin({
          tacho: this.commander.getCurrentTacho(id).pipe(catchError(() => of(null))),
          positions: this.commander.getLastPositions().pipe(catchError(() => of(null))),
        }).subscribe(({ tacho, positions }) => {
          this.tacho.set(tacho);
          if (positions) {
            this.position.set(positions.find(p => p.vehicleId === id) ?? null);
          }
          this.loading.set(false);
        });
      },
      error: (err: CommanderError) => {
        this.loading.set(false);
        this.error.set(err);
      },
    });
  }

  /** Google Maps URL for the last-known GPS position, or null if unavailable. */
  mapHref(): string | null {
    const p = this.position();
    if (!p || p.latitude == null || p.longitude == null) return null;
    return `https://www.google.com/maps?q=${p.latitude},${p.longitude}`;
  }
}
