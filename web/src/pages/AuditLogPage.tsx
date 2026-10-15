import { Card, Group, Pagination, Select, Text, TextInput } from '@mantine/core';
import { DatePickerInput } from '@mantine/dates';
import dayjs from 'dayjs';
import { useState } from 'react';
import { useMembers, useTeamAuditLog } from '../api/queries';
import { useTeam } from '../auth/TeamContext';
import { PageHeader } from '../components/PageHeader';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import { AuditTable } from '../flight/ActivityPanel';
import { auditActionLabels } from '../lib/labels';

const ACTION_FILTERS = [
  { value: 'team.', label: 'Ekip üyeleri' },
  { value: 'project.', label: 'Projeler' },
  { value: 'aircraft.', label: 'Araçlar' },
  { value: 'flight.', label: 'Uçuşlar' },
  { value: 'log_file.', label: 'Log dosyaları' },
  { value: 'finding.', label: 'Bulgu incelemeleri' },
  { value: 'comment.', label: 'Yorumlar' },
  ...Object.entries(auditActionLabels)
    .filter(([k]) => ['analysis.requested', 'ai_analysis.requested', 'data.exported'].includes(k))
    .map(([value, label]) => ({ value, label })),
];

/** The team's audit log (project managers): who did what, when, with the request's trace id. */
export function AuditLogPage() {
  const { team, can } = useTeam();
  const members = useMembers(team?.teamId);
  const [action, setAction] = useState<string | null>(null);
  const [actorId, setActorId] = useState<string | null>(null);
  const [range, setRange] = useState<[string | null, string | null]>([null, null]);
  const [entityId, setEntityId] = useState('');
  const [page, setPage] = useState(1);
  const log = useTeamAuditLog(can('ProjectManager') ? team?.teamId : undefined, {
    action,
    actorId,
    entityId: /^[0-9a-f-]{36}$/i.test(entityId.trim()) ? entityId.trim() : undefined,
    from: range[0] ? dayjs(range[0]).startOf('day').toISOString() : undefined,
    to: range[1] ? dayjs(range[1]).endOf('day').toISOString() : undefined,
    page,
    pageSize: 50,
  });
  const reset = <T,>(set: (v: T) => void) => (v: T) => {
    set(v);
    setPage(1);
  };

  if (!can('ProjectManager')) {
    return <EmptyState title="Yetki yok">Denetim kaydını yalnızca proje yöneticileri görebilir.</EmptyState>;
  }

  return (
    <>
      <PageHeader title="Denetim kaydı" description={log.data ? `${log.data.totalCount} kayıt` : undefined} />
      <Card withBorder mb="md" padding="sm">
        <Group gap="sm" align="flex-end">
          <Select label="İşlem" clearable data={ACTION_FILTERS} value={action} onChange={reset(setAction)} w={200} />
          <Select
            label="Kişi"
            clearable
            searchable
            data={(members.data ?? []).map((m) => ({ value: m.userId, label: m.displayName }))}
            value={actorId}
            onChange={reset(setActorId)}
            w={200}
          />
          <DatePickerInput type="range" label="Tarih aralığı" clearable valueFormat="DD.MM.YYYY" value={range} onChange={reset(setRange)} w={230} />
          <TextInput label="Kayıt kimliği" placeholder="uçuş, bulgu, yorum…" value={entityId} onChange={(e) => reset(setEntityId)(e.currentTarget.value)} w={300} />
        </Group>
      </Card>
      {log.isPending ? (
        <LoadingState />
      ) : log.isError ? (
        <ErrorState error={log.error} onRetry={() => log.refetch()} />
      ) : log.data.items.length === 0 ? (
        <EmptyState title="Kayıt yok">Filtreye uyan bir işlem kaydı yok.</EmptyState>
      ) : (
        <Card withBorder padding="xs">
          <AuditTable entries={log.data.items} showFlight />
          {log.data.totalPages > 1 && (
            <Group justify="center" p="sm">
              <Pagination total={log.data.totalPages} value={page} onChange={setPage} />
            </Group>
          )}
          <Text size="xs" c="dimmed" mt="xs">
            Kayıtlar silinen kayıtlardan sonra da saklanır; saklama süresi sunucu ayarındadır (varsayılan 730 gün).
          </Text>
        </Card>
      )}
    </>
  );
}
