import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';

export interface NotificationConfig {
  noActivity48hEnabled: boolean;
  noActivity48hTime: string;
  workingDaysOnly: boolean;
  managerSummaryEnabled: boolean;
  managerSummaryEmployeeId: number | null;
}

export interface NotificationEmployeeStatus {
  id: number;
  firstName: string;
  lastName: string;
  fullName: string;
  phoneNumber?: string;
  notificationsEnabled: boolean;
  pushSubscriptionCount: number;
  lastNotifiedAt?: string;
}

export interface NotificationLogEntry {
  id: number;
  employeeId?: number;
  employeeName: string;
  channel: string;
  triggerType: string;
  body: string;
  triggerDate: string;
  sentAt: string;
  status: string;
  errorMessage?: string;
}

@Injectable({
  providedIn: 'root'
})
export class NotificationConfigService {
  private http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiUrl}/notifications`;

  getConfig() {
    return this.http.get<NotificationConfig>(`${this.baseUrl}/config`);
  }

  updateConfig(config: Partial<NotificationConfig>) {
    return this.http.put<NotificationConfig>(`${this.baseUrl}/config`, config);
  }

  getHistory(from?: string, to?: string, employeeId?: number, page = 1, pageSize = 50) {
    return this.http.get<NotificationLogEntry[]>(`${this.baseUrl}/history`, {
      params: {
        ...(from && { from }),
        ...(to && { to }),
        ...(employeeId && { employeeId: employeeId.toString() }),
        page: page.toString(),
        pageSize: pageSize.toString(),
      },
    });
  }

  getEmployeeStatuses() {
    return this.http.get<NotificationEmployeeStatus[]>(`${this.baseUrl}/employees`);
  }

  updateEmployeeNotifications(employeeId: number, settings: Partial<NotificationEmployeeStatus>) {
    return this.http.put<NotificationEmployeeStatus>(
      `${this.baseUrl}/employees/${employeeId}`,
      settings
    );
  }

  testPush(employeeId: number, title?: string, body?: string) {
    return this.http.post(`${this.baseUrl}/test/push`, {
      employeeId,
      title,
      body,
    });
  }

  fireNow() {
    return this.http.post(`${this.baseUrl}/fire-now`, {});
  }

  fireForEmployee(employeeId: number, ignoreIdempotency = true) {
    return this.http.post(`${this.baseUrl}/fire-for-employee`, {
      employeeId,
      ignoreIdempotency,
    });
  }

  resetToday() {
    return this.http.post(`${this.baseUrl}/reset-today`, {});
  }
}
