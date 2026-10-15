import dayjs from 'dayjs';

export function formatDateTime(value: string | null | undefined): string {
  return value ? dayjs(value).format('DD.MM.YYYY HH:mm') : '—';
}

export function formatDate(value: string | null | undefined): string {
  return value ? dayjs(value).format('DD.MM.YYYY') : '—';
}

/** 1 h 02 min, 3 min 05 s, 12.4 s */
export function formatDuration(seconds: number | null | undefined): string {
  if (seconds == null) {
    return '—';
  }

  if (seconds < 60) {
    return `${seconds.toFixed(1)} s`;
  }

  const total = Math.round(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  return h > 0 ? `${h} sa ${String(m).padStart(2, '0')} dk` : `${m} dk ${String(s).padStart(2, '0')} s`;
}

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes == null) {
    return '—';
  }

  const units = ['B', 'KB', 'MB', 'GB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }

  return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

/** A log timestamp (µs on the vehicle's boot clock) as seconds since the log start: "123.456 s". */
export function formatLogTime(us: number, logStartUs: number): string {
  return `${((us - logStartUs) / 1e6).toFixed(3)} s`;
}

export function formatNumber(value: number | null | undefined, digits = 3): string {
  if (value == null || Number.isNaN(value)) {
    return '—';
  }

  return Number.isInteger(value) ? String(value) : value.toFixed(digits).replace(/\.?0+$/, '');
}
