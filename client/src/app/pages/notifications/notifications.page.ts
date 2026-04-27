import { Component, OnInit, signal, computed, inject, effect } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { NotificationConfigService, NotificationConfig, NotificationEmployeeStatus, NotificationLogEntry } from '../../services/notification-config.service';
import { EmployeeService, Employee } from '../../services/employee.service';
import { KioskService, MissingHoursOverview } from '../../services/kiosk.service';

@Component({
  selector: 'app-notifications',
  imports: [NavbarComponent, CommonModule, FormsModule],
  templateUrl: './notifications.page.html',
  standalone: true
})
export class NotificationsPage implements OnInit {
  private configSvc = inject(NotificationConfigService);
  private employeeSvc = inject(EmployeeService);
  private kioskSvc = inject(KioskService);

  // "Treba pripomenúť" — workers missing entries for the past 2 days.
  // This is the primary feature now (option A from the discussion above):
  // a list the manager can use to call/SMS people directly.
  missingOverview = signal<MissingHoursOverview | null>(null);
  missingOverviewLoading = signal(false);

  // Card 1: Config state
  config = signal<Partial<NotificationConfig>>({});
  configLoading = signal(false);
  configSaving = signal(false);
  configSaveMessage = signal('');
  configSaveSuccess = signal(false);

  // Card 2: Employees state
  employees = signal<NotificationEmployeeStatus[]>([]);
  allEmployees = signal<Employee[]>([]);
  employeesLoading = signal(false);
  employeesError = signal('');
  employeeDebounceTimers = new Map<number, any>();
  employeeUpdatingIds = signal<Set<number>>(new Set());

  // Card 3: Test & Demo state
  selectedEmployeeId = signal<number | null>(null);
  testPushLoading = signal(false);
  testResult = signal<string>('');
  testError = signal<string>('');

  fireNowLoading = signal(false);
  fireNowResult = signal<string>('');

  demoSelectedEmployeeId = signal<number | null>(null);
  demoLoading = signal(false);
  demoResult = signal<string>('');
  demoError = signal<string>('');

  resetTodayLoading = signal(false);
  resetTodayMessage = signal<string>('');

  // Card 4: History state
  historyFrom = signal('');
  historyTo = signal('');
  historyEmployeeId = signal<number | null>(null);
  historyChannel = signal<string>(''); // 'all', 'Push', 'WhatsApp'
  historyStatus = signal<string>(''); // 'all', 'Sent', 'Failed', 'Skipped', 'NoSubscription'
  history = signal<NotificationLogEntry[]>([]);
  historyLoading = signal(false);
  historyError = signal('');

  // Computed: active employees only (for dropdowns)
  activeEmployees = computed(() => this.allEmployees().filter(e => e.isActive));

  constructor() {
    // Auto-load history when filters change
    effect(() => {
      this.historyFrom();
      this.historyTo();
      this.historyEmployeeId();
      this.historyChannel();
      this.historyStatus();
      this.loadHistory();
    });
  }

  ngOnInit() {
    this.loadMissingOverview();
    this.loadConfig();
    this.loadEmployees();
    this.loadAllEmployees();
    this.loadHistory();
  }

  loadMissingOverview() {
    this.missingOverviewLoading.set(true);
    this.kioskSvc.getMissingHoursOverview().subscribe({
      next: data => { this.missingOverview.set(data); this.missingOverviewLoading.set(false); },
      error: () => { this.missingOverview.set(null); this.missingOverviewLoading.set(false); }
    });
  }

  /** Format yyyy-MM-dd as Slovak short date with weekday: "Pi 24. 4.". */
  formatMissingDateLong(dateStr: string): string {
    const [y, m, d] = dateStr.split('-').map(Number);
    const date = new Date(y, m - 1, d);
    const names = ['Ne', 'Po', 'Ut', 'St', 'Št', 'Pi', 'So'];
    return `${names[date.getDay()]} ${d}. ${m}.`;
  }

  // =========================
  // Card 1: Config
  // =========================
  loadConfig() {
    this.configLoading.set(true);
    this.configSvc.getConfig().subscribe({
      next: (cfg) => {
        this.config.set(cfg);
        this.configLoading.set(false);
      },
      error: () => {
        this.configLoading.set(false);
        alert('Nepodarilo sa načítať konfiguráciu upozornení.');
      }
    });
  }

  saveConfig() {
    this.configSaving.set(true);
    this.configSaveMessage.set('');
    this.configSaveSuccess.set(false);

    const payload: Partial<NotificationConfig> = {
      noActivity48hEnabled: this.config().noActivity48hEnabled,
      noActivity48hTime: this.config().noActivity48hTime,
      workingDaysOnly: this.config().workingDaysOnly,
      managerSummaryEnabled: this.config().managerSummaryEnabled,
      managerSummaryEmployeeId: this.config().managerSummaryEmployeeId
    };

    this.configSvc.updateConfig(payload).subscribe({
      next: () => {
        this.configSaving.set(false);
        this.configSaveSuccess.set(true);
        this.configSaveMessage.set('Uložené ✓');
        setTimeout(() => {
          this.configSaveMessage.set('');
          this.configSaveSuccess.set(false);
        }, 2000);
      },
      error: () => {
        this.configSaving.set(false);
        this.configSaveMessage.set('Chyba pri ukladaní');
      }
    });
  }

  toggleConfigEnabled() {
    this.config.update(c => ({
      ...c,
      noActivity48hEnabled: !c.noActivity48hEnabled
    }));
  }

  toggleWorkingDaysOnly() {
    this.config.update(c => ({
      ...c,
      workingDaysOnly: !c.workingDaysOnly
    }));
  }

  toggleManagerSummaryEnabled() {
    this.config.update(c => ({
      ...c,
      managerSummaryEnabled: !c.managerSummaryEnabled
    }));
  }

  onNoActivity48hTimeChange(value: string) {
    this.config.update(c => ({
      ...c,
      noActivity48hTime: value
    }));
  }

  onManagerSummaryEmployeeChange(value: string) {
    const id = value ? parseInt(value, 10) : null;
    this.config.update(c => ({
      ...c,
      managerSummaryEmployeeId: id
    }));
  }

  // =========================
  // Card 2: Employees
  // =========================
  loadEmployees() {
    this.employeesLoading.set(true);
    this.employeesError.set('');
    this.configSvc.getEmployeeStatuses().subscribe({
      next: (emps) => {
        this.employees.set(emps);
        this.employeesLoading.set(false);
      },
      error: () => {
        this.employeesLoading.set(false);
        this.employeesError.set('Nepodarilo sa načítať zamestnancov.');
      }
    });
  }

  loadAllEmployees() {
    this.employeeSvc.getAll().subscribe({
      next: (emps) => this.allEmployees.set(emps),
      error: () => this.allEmployees.set([])
    });
  }

  toggleEmployeeNotifications(emp: NotificationEmployeeStatus) {
    this.updateEmployeeWithDebounce(emp.id, {
      notificationsEnabled: !emp.notificationsEnabled
    });
  }

  private updateEmployeeWithDebounce(employeeId: number, updates: Partial<NotificationEmployeeStatus>) {
    // Cancel existing timer
    if (this.employeeDebounceTimers.has(employeeId)) {
      clearTimeout(this.employeeDebounceTimers.get(employeeId));
    }

    // Mark as updating
    this.employeeUpdatingIds.update(s => new Set([...s, employeeId]));

    // Debounce 500ms
    const timer = setTimeout(() => {
      this.configSvc.updateEmployeeNotifications(employeeId, updates).subscribe({
        next: () => {
          this.employeeUpdatingIds.update(s => {
            const newSet = new Set(s);
            newSet.delete(employeeId);
            return newSet;
          });
          this.loadEmployees();
        },
        error: () => {
          this.employeeUpdatingIds.update(s => {
            const newSet = new Set(s);
            newSet.delete(employeeId);
            return newSet;
          });
        }
      });
      this.employeeDebounceTimers.delete(employeeId);
    }, 500);

    this.employeeDebounceTimers.set(employeeId, timer);
  }

  formatLastNotified(date?: string): string {
    if (!date) return '—';
    try {
      const d = new Date(date);
      return d.toLocaleDateString('sk-SK', { day: '2-digit', month: '2-digit', year: 'numeric' });
    } catch {
      return '—';
    }
  }

  // =========================
  // Card 3: Test & Demo
  // =========================
  onTestPushClick() {
    const empId = this.selectedEmployeeId();
    if (!empId) {
      this.testError.set('Vyberte zamestnanca.');
      return;
    }

    this.testPushLoading.set(true);
    this.testError.set('');
    this.testResult.set('');

    this.configSvc.testPush(empId).subscribe({
      next: (res: any) => {
        this.testPushLoading.set(false);
        if (res.success || res.sendCount > 0) {
          this.testResult.set(res.message || `Odoslané ${res.sendCount || 1} notifikácií`);
        } else {
          this.testError.set(res.errorMessage || 'Nepodarilo sa odoslať.');
        }
      },
      error: (err) => {
        this.testPushLoading.set(false);
        this.testError.set(err?.error?.message || 'Chyba pri odoslaní.');
      }
    });
  }

  onFireNowClick() {
    if (!confirm('Spustí 48h kontrolu pre všetkých zamestnancov. Pokračovať?')) return;

    this.fireNowLoading.set(true);
    this.fireNowResult.set('');

    this.configSvc.fireNow().subscribe({
      next: (res: any) => {
        this.fireNowLoading.set(false);
        this.fireNowResult.set(res.message || `Odoslané ${res.sendCount || 0} upozornení`);
      },
      error: () => {
        this.fireNowLoading.set(false);
        this.fireNowResult.set('Chyba pri spustení.');
      }
    });
  }

  onDemoClick() {
    const empId = this.demoSelectedEmployeeId();
    if (!empId) {
      this.demoError.set('Vyberte zamestnanca.');
      return;
    }

    this.demoLoading.set(true);
    this.demoError.set('');
    this.demoResult.set('');

    this.configSvc.fireForEmployee(empId, true).subscribe({
      next: (res: any) => {
        this.demoLoading.set(false);
        if (res.success || res.sendCount > 0) {
          const now = new Date();
          const timeStr = now.toLocaleTimeString('sk-SK', { hour: '2-digit', minute: '2-digit' });
          const emp = this.activeEmployees().find(e => e.id === empId);
          const empName = emp ? `${emp.firstName} ${emp.lastName}` : '';
          this.demoResult.set(`✓ Pripomienka odoslaná o ${timeStr}${empName ? ' pre ' + empName : ''}`);
        } else {
          this.demoError.set(res.errorMessage || 'Nepodarilo sa odoslať.');
        }
      },
      error: (err) => {
        this.demoLoading.set(false);
        this.demoError.set(err?.error?.message || 'Chyba pri odoslaní.');
      }
    });
  }

  onResetTodayClick() {
    if (!confirm('Vymazať dnešné záznamy o odoslaních? Budete môcť poslať ukážku znova.')) return;

    this.resetTodayLoading.set(true);
    this.resetTodayMessage.set('');

    this.configSvc.resetToday().subscribe({
      next: () => {
        this.resetTodayLoading.set(false);
        this.resetTodayMessage.set('Záznamy zmazané ✓');
        setTimeout(() => this.resetTodayMessage.set(''), 2000);
        this.loadHistory();
      },
      error: () => {
        this.resetTodayLoading.set(false);
        this.resetTodayMessage.set('Chyba pri mazaní.');
      }
    });
  }

  // =========================
  // Card 4: History
  // =========================
  loadHistory() {
    this.historyLoading.set(true);
    this.historyError.set('');

    this.configSvc.getHistory(
      this.historyFrom() || undefined,
      this.historyTo() || undefined,
      this.historyEmployeeId() || undefined
    ).subscribe({
      next: (logs) => {
        let filtered = logs;
        if (this.historyChannel() && this.historyChannel() !== '') {
          filtered = filtered.filter(l => l.channel === this.historyChannel());
        }
        if (this.historyStatus() && this.historyStatus() !== '') {
          filtered = filtered.filter(l => l.status === this.historyStatus());
        }
        this.history.set(filtered);
        this.historyLoading.set(false);
      },
      error: () => {
        this.historyLoading.set(false);
        this.historyError.set('Nepodarilo sa načítať históriu.');
      }
    });
  }

  formatHistoryDate(dateStr: string): string {
    try {
      const parts = dateStr.split('T');
      if (parts.length > 1) {
        // ISO format with time
        const d = new Date(dateStr);
        return d.toLocaleString('sk-SK', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' });
      } else {
        // Date only
        const d = new Date(dateStr);
        return d.toLocaleDateString('sk-SK', { day: '2-digit', month: '2-digit', year: 'numeric' });
      }
    } catch {
      return dateStr;
    }
  }

  truncateError(msg?: string): string {
    if (!msg) return '';
    return msg.length > 50 ? msg.substring(0, 50) + '…' : msg;
  }
}
