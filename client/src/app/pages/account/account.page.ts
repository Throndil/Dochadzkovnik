import { Component, signal, OnInit, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { AlertComponent } from '../../components/alert/alert.component';
import { AuthService } from '../../services/auth.service';
import { FeatureFlagService } from '../../services/feature-flag.service';

/** One sellable module row on the Moduly panel. `parent` marks a sub-toggle
 *  that needs its parent module enabled first. */
type ModuleDef = { key: string; label: string; desc: string; enabled: () => boolean; parent?: string };

@Component({
  selector: 'app-account',
  imports: [NavbarComponent, FormsModule, AlertComponent],
  templateUrl: './account.page.html'
})
export class AccountPage implements OnInit {
  // Recovery email
  recoveryEmail = '';
  adminUsername = signal('');
  emailSaving = signal(false);
  emailSaved = signal(false);
  emailError = signal('');

  // Change password
  currentPassword = '';
  newPassword = '';
  confirmPassword = '';
  pwdSaving = signal(false);
  pwdSaved = signal(false);
  pwdError = signal('');

  // Display name (navbar / kiosk header) — self-service rename
  displayNameDraft = '';
  nameSaving = signal(false);
  nameSaved = signal(false);
  nameError = signal('');

  onSaveDisplayName() {
    const name = this.displayNameDraft.trim();
    if (name.length < 2) return;
    this.nameError.set('');
    this.nameSaved.set(false);
    this.nameSaving.set(true);
    this.auth.updateDisplayName(name).subscribe({
      next: () => { this.nameSaved.set(true); this.nameSaving.set(false); },
      error: () => { this.nameError.set('Uloženie sa nepodarilo.'); this.nameSaving.set(false); }
    });
  }

  // Security PIN (second gate after the password at login)
  hasPin = signal(false);
  pinCurrent = '';
  pinNew = '';
  pinConfirm = '';
  pinSaving = signal(false);
  pinSaved = signal(false);
  pinError = signal('');

  // Moduly (superadmin only) — the sellable packaging; toggles map 1:1 to
  // server feature flags (API + routes + navbar all gate on them).
  flags = inject(FeatureFlagService);
  flagSaving = signal(false);
  flagError = signal('');

  /** Always-on core, listed on the panel so the packaging is explicit. */
  readonly coreItems = ['Šichtovnica (dochádzka)', 'Zamestnanci', 'Pracoviská', 'Záznamy hodín', 'Prehľad'];

  readonly moduleDefs: ModuleDef[] = [
    { key: 'payrollAndPnL', label: 'Mzdy a financie', desc: 'Mzdy, zálohy, sadzby, Odvody, náklady a zisk (P&L), Financie súhrn s grafmi.', enabled: () => this.flags.payrollAndPnL() },
    { key: 'invoiceScanning', label: 'Faktúry a AI skenovanie', desc: 'Nahrávanie a AI rozpoznávanie faktúr a bločkov. Vyžaduje Google Document AI — viď SECRETS.md.', enabled: () => this.flags.invoiceScanning() },
    { key: 'invoiceCameraScan', label: 'Skenovanie mobilom', desc: 'Tlačidlo „Naskenovať mobilom" na stránke Faktúry.', enabled: () => this.flags.invoiceCameraScan(), parent: 'invoiceScanning' },
    { key: 'materialPurchases', label: 'Materiál a sklad', desc: 'Kioskový nákup materiálu, katalóg, nákupy a sklad.', enabled: () => this.flags.materialPurchases() },
    { key: 'vehicles', label: 'Vozidlá', desc: 'Správa áut a palivových kariet. Výber auta na kiosku zostáva v základe.', enabled: () => this.flags.vehicles() },
    { key: 'strojeDivisions', label: 'Stroje a divízie', desc: 'Divízia AZ Stroje: register strojov, prepínač divízií, divízne doklady a report.', enabled: () => this.flags.strojeDivisions() },
    { key: 'planner', label: 'Plánovač', desc: 'Týždenný plán: kto kedy robí na ktorom pracovisku, dovolenky a PN.', enabled: () => this.flags.planner() },
    { key: 'notifications', label: 'Notifikácie', desc: 'PWA push, admin Notifikácie stránka, kioskový banner.', enabled: () => this.flags.notifications() },
    { key: 'commanderIntegration', label: 'Commander GPS', desc: 'Live údaje o vozidlách z Commander API na profile auta.', enabled: () => this.flags.commanderIntegration() },
    { key: 'proofOfWorkChoices', label: 'Preukazovanie práce', desc: 'Po zadaní hodín si pracovník vyberie: fotka, denník alebo priamy zápis.', enabled: () => this.flags.proofOfWorkChoices() },
  ];

  /** Sub-toggles stay disabled until their parent module is on. */
  moduleParentOn(m: ModuleDef): boolean {
    if (!m.parent) return true;
    const p = this.moduleDefs.find(d => d.key === m.parent);
    return p ? p.enabled() : true;
  }

  get pinValid() {
    return /^\d{4,8}$/.test(this.pinNew)
      && this.pinNew === this.pinConfirm
      && (!this.hasPin() || this.pinCurrent.length > 0);
  }

  onChangePin() {
    if (!this.pinValid) return;
    this.pinError.set('');
    this.pinSaved.set(false);
    this.pinSaving.set(true);
    this.auth.changeAdminPin(this.hasPin() ? this.pinCurrent : null, this.pinNew).subscribe({
      next: () => {
        this.pinSaved.set(true);
        this.pinSaving.set(false);
        this.hasPin.set(true);
        this.pinCurrent = '';
        this.pinNew = '';
        this.pinConfirm = '';
      },
      error: (err) => {
        this.pinError.set(typeof err?.error === 'string' ? err.error : 'Zmena PIN-u sa nepodarila.');
        this.pinSaving.set(false);
      }
    });
  }

  get pwdValid() {
    return this.currentPassword && this.newPassword.length >= 6 && this.newPassword === this.confirmPassword;
  }

  constructor(public auth: AuthService) {}

  /**
   * Toggle a flag and surface any error in the card. Re-fetched after the PUT
   * succeeds so the local signal stays in sync with the server's authoritative
   * state.
   */
  async onToggleFlag(key: string, enabled: boolean) {
    this.flagError.set('');
    this.flagSaving.set(true);
    try {
      await this.flags.setEnabled(key, enabled);
    } catch {
      this.flagError.set('Zmenu sa nepodarilo uložiť. Skús znovu.');
    } finally {
      this.flagSaving.set(false);
    }
  }

  ngOnInit() {
    this.displayNameDraft = this.auth.displayName();
    this.auth.getMe().subscribe({ next: (me) => {
      this.recoveryEmail = me.email ?? '';
      this.adminUsername.set(me.userName ?? '');
      this.hasPin.set(!!me.hasAdminPin);
    }});
  }

  onSaveEmail() {
    this.emailError.set('');
    this.emailSaved.set(false);
    this.emailSaving.set(true);
    this.auth.updateRecoveryEmail(this.recoveryEmail).subscribe({
      next: () => { this.emailSaved.set(true); this.emailSaving.set(false); },
      error: () => { this.emailError.set('Uloženie sa nepodarilo.'); this.emailSaving.set(false); }
    });
  }

  onChangePassword() {
    if (!this.pwdValid) return;
    this.pwdError.set('');
    this.pwdSaved.set(false);
    this.pwdSaving.set(true);
    this.auth.changePassword(this.currentPassword, this.newPassword).subscribe({
      next: () => {
        this.pwdSaved.set(true);
        this.pwdSaving.set(false);
        this.currentPassword = '';
        this.newPassword = '';
        this.confirmPassword = '';
      },
      error: (err) => { this.pwdError.set(err.error ?? 'Zmena hesla sa nepodarila.'); this.pwdSaving.set(false); }
    });
  }
}
