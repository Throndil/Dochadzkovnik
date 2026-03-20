import { Pipe, PipeTransform } from '@angular/core';

@Pipe({ name: 'hm', standalone: true })
export class HmPipe implements PipeTransform {
  transform(hours: number | null | undefined): string {
    if (hours == null || hours < 0) return '—';
    const totalMin = Math.round(hours * 60);
    const h = Math.floor(totalMin / 60);
    const m = totalMin % 60;
    if (h === 0 && m === 0) return '0h';
    if (h === 0) return `${m}m`;
    if (m === 0) return `${h}h`;
    return `${h}h ${m}m`;
  }
}
