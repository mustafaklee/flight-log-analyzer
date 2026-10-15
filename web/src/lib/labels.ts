import type { Autopilot, EventSeverity, FindingConfidence, FindingSeverity, FlightEventKind, ProcessingStatus, TeamRole } from '../api/types';

export const statusLabels: Record<ProcessingStatus, string> = {
  Uploaded: 'Yüklendi',
  Validating: 'Doğrulanıyor',
  Queued: 'Kuyrukta',
  Processing: 'İşleniyor',
  Processed: 'İşlendi',
  AnalysisPending: 'Analiz bekliyor',
  Analyzing: 'Analiz ediliyor',
  Completed: 'Tamamlandı',
  Failed: 'Başarısız',
  Cancelled: 'İptal edildi',
};

export const statusColors: Record<ProcessingStatus, string> = {
  Uploaded: 'gray',
  Validating: 'gray',
  Queued: 'blue',
  Processing: 'blue',
  Processed: 'cyan',
  AnalysisPending: 'cyan',
  Analyzing: 'cyan',
  Completed: 'green',
  Failed: 'red',
  Cancelled: 'gray',
};

export const severityLabels: Record<FindingSeverity, string> = { Info: 'Bilgi', Warning: 'Uyarı', Critical: 'Kritik' };
export const severityColors: Record<FindingSeverity, string> = { Info: 'blue', Warning: 'yellow', Critical: 'red' };
export const confidenceLabels: Record<FindingConfidence, string> = { Low: 'Düşük', Medium: 'Orta', High: 'Yüksek' };

export const roleLabels: Record<TeamRole, string> = { Viewer: 'İzleyici', Engineer: 'Mühendis', ProjectManager: 'Proje yöneticisi' };
export const autopilotLabels: Record<Autopilot, string> = { Unknown: 'Bilinmiyor', Px4: 'PX4', ArduPilot: 'ArduPilot' };

export const eventKindLabels: Record<FlightEventKind, string> = {
  Message: 'Mesaj',
  ModeChange: 'Mod değişimi',
  Armed: 'Arm',
  Disarmed: 'Disarm',
  Failsafe: 'Failsafe',
  Error: 'Hata',
  Event: 'Olay',
  Dropout: 'Veri kaybı',
};

/** Colors for event markers on charts and in lists. */
export const eventKindColors: Record<FlightEventKind, string> = {
  Message: 'gray',
  ModeChange: 'violet',
  Armed: 'green',
  Disarmed: 'teal',
  Failsafe: 'red',
  Error: 'orange',
  Event: 'indigo',
  Dropout: 'pink',
};

const SEVERE: EventSeverity[] = ['Emergency', 'Alert', 'Critical', 'Error'];

export function eventSeverityColor(severity: EventSeverity | null | undefined): string {
  if (!severity) {
    return 'gray';
  }

  return SEVERE.includes(severity) ? 'red' : severity === 'Warning' ? 'yellow' : 'gray';
}
