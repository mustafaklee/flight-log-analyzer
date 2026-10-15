import { Badge, Group, Pagination, Select, Stack, Table, Text } from '@mantine/core';
import { useState } from 'react';
import { useFlightActivity } from '../api/queries';
import type { AuditEntry } from '../api/types';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import { formatDateTime } from '../lib/format';
import { auditActionLabel } from '../lib/labels';

const ACTION_GROUPS = [
  { value: 'comment.', label: 'Yorumlar' },
  { value: 'finding.', label: 'Bulgu incelemeleri' },
  { value: 'flight.', label: 'Uçuş bilgileri ve etiketler' },
  { value: 'log_file.', label: 'Log dosyası' },
  { value: 'analysis.requested', label: 'Analiz istekleri' },
  { value: 'ai_analysis.requested', label: 'AI raporu istekleri' },
  { value: 'data.exported', label: 'Dışa aktarmalar' },
];

/** A table of audit entries: when, who, what, and a summary of the change. Shared by the flight activity and the team audit log. */
export function AuditTable({ entries, showFlight = false }: { entries: AuditEntry[]; showFlight?: boolean }) {
  return (
    <Table.ScrollContainer minWidth={640}>
      <Table fz="xs" verticalSpacing={4} striped data-testid="audit-table">
        <Table.Thead>
          <Table.Tr>
            <Table.Th w={130}>Zaman</Table.Th>
            <Table.Th w={150}>Kişi</Table.Th>
            <Table.Th w={180}>İşlem</Table.Th>
            <Table.Th>Ayrıntı</Table.Th>
            {showFlight && <Table.Th w={110}>İz kimliği</Table.Th>}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {entries.map((e) => (
            <Table.Tr key={e.id}>
              <Table.Td>{formatDateTime(e.createdAt)}</Table.Td>
              <Table.Td>{e.actor.displayName}</Table.Td>
              <Table.Td>
                <Badge size="xs" variant="light" color={e.action.endsWith('deleted') || e.action.endsWith('removed') ? 'red' : 'blue'}>
                  {auditActionLabel(e.action)}
                </Badge>
              </Table.Td>
              <Table.Td>
                <Text size="xs" style={{ overflowWrap: 'anywhere' }}>
                  {e.summary ?? '—'}
                </Text>
              </Table.Td>
              {showFlight && (
                <Table.Td>
                  <Text size="xs" ff="monospace" c="dimmed" lineClamp={1} title={e.correlationId}>
                    {e.correlationId.slice(0, 12)}
                  </Text>
                </Table.Td>
              )}
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

/** Who did what on a flight, newest first (any team member can read it). */
export function ActivityPanel({ flightId }: { flightId: string }) {
  const [action, setAction] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const activity = useFlightActivity(flightId, { action, page, pageSize: 50 });

  return (
    <Stack gap="sm">
      <Group>
        <Select
          size="xs"
          aria-label="İşlem filtresi"
          placeholder="Tüm işlemler"
          clearable
          data={ACTION_GROUPS}
          value={action}
          onChange={(v) => {
            setAction(v);
            setPage(1);
          }}
        />
        {activity.data && (
          <Text size="xs" c="dimmed">
            {activity.data.totalCount} kayıt
          </Text>
        )}
      </Group>
      {activity.isPending ? (
        <LoadingState />
      ) : activity.isError ? (
        <ErrorState error={activity.error} onRetry={() => activity.refetch()} />
      ) : activity.data.items.length === 0 ? (
        <EmptyState title="Kayıt yok">Bu uçuşta kaydedilmiş bir işlem yok.</EmptyState>
      ) : (
        <>
          <AuditTable entries={activity.data.items} />
          {activity.data.totalPages > 1 && <Pagination size="sm" total={activity.data.totalPages} value={page} onChange={setPage} />}
        </>
      )}
    </Stack>
  );
}
