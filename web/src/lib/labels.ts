import type { Autopilot, EventSeverity, FindingConfidence, FindingReviewStatus, FindingSeverity, FlightEventKind, ProcessingStatus, TeamRole } from '../api/types';

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

export const reviewStatusLabels: Record<FindingReviewStatus, string> = {
  Open: 'Açık',
  Investigating: 'İnceleniyor',
  Confirmed: 'Doğrulandı',
  Dismissed: 'Geçersiz',
  Resolved: 'Çözüldü',
};

export const reviewStatusColors: Record<FindingReviewStatus, string> = {
  Open: 'gray',
  Investigating: 'blue',
  Confirmed: 'orange',
  Dismissed: 'dark',
  Resolved: 'green',
};

/** Turkish names of audit actions; unknown actions are shown as written. */
export const auditActionLabels: Record<string, string> = {
  'flight.updated': 'Uçuş güncellendi',
  'flight.deleted': 'Uçuş silindi',
  'flight.tags_changed': 'Etiketler değişti',
  'log_file.uploaded': 'Log yüklendi',
  'log_file.cancelled': 'İşleme iptal edildi',
  'log_file.retried': 'İşleme yeniden denendi',
  'analysis.requested': 'Analiz istendi',
  'ai_analysis.requested': 'AI raporu istendi',
  'comment.created': 'Yorum eklendi',
  'comment.edited': 'Yorum düzenlendi',
  'comment.deleted': 'Yorum silindi',
  'finding.status_changed': 'İnceleme durumu değişti',
  'finding.assigned': 'Atama değişti',
  'finding.flagged': 'İşaretlendi',
  'finding.unflagged': 'İşaret kaldırıldı',
  'team.member_added': 'Üye eklendi',
  'team.member_role_changed': 'Üye rolü değişti',
  'team.member_removed': 'Üye çıkarıldı',
  'project.created': 'Proje oluşturuldu',
  'project.deleted': 'Proje silindi',
  'aircraft.created': 'Araç eklendi',
  'aircraft.deleted': 'Araç silindi',
  'data.exported': 'Veri dışa aktarıldı',
};

export const auditActionLabel = (action: string) => auditActionLabels[action] ?? action;
