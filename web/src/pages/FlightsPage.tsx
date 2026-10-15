import { Anchor, Button, Card, Group, Pagination, Select, Table, Text, TextInput } from '@mantine/core';
import { DatePickerInput } from '@mantine/dates';
import { useDebouncedValue } from '@mantine/hooks';
import { IconSearch, IconUpload } from '@tabler/icons-react';
import dayjs from 'dayjs';
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { useAircraftList, useFlights, useProjects } from '../api/queries';
import type { FlightSort, ProcessingStatus } from '../api/types';
import { useTeam } from '../auth/TeamContext';
import { PageHeader } from '../components/PageHeader';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import { SeverityBadge, StatusBadge } from '../components/StatusBadge';
import { formatBytes, formatDateTime, formatDuration } from '../lib/format';
import { autopilotLabels, statusLabels } from '../lib/labels';

const sorts: { value: FlightSort; label: string }[] = [
  { value: 'OccurredAtDesc', label: 'En yeni' },
  { value: 'OccurredAtAsc', label: 'En eski' },
  { value: 'DurationDesc', label: 'En uzun' },
  { value: 'DurationAsc', label: 'En kısa' },
  { value: 'NameAsc', label: 'Ad (A-Z)' },
  { value: 'NameDesc', label: 'Ad (Z-A)' },
];

const PAGE_SIZE = 25;

export function FlightsPage() {
  const { team } = useTeam();
  const [params, setParams] = useSearchParams();
  const [search, setSearch] = useState(params.get('search') ?? '');
  const [debouncedSearch] = useDebouncedValue(search, 300);

  const get = (key: string) => params.get(key) ?? undefined;
  const set = (key: string, value: string | null | undefined) => {
    const next = new URLSearchParams(params);
    if (value) {
      next.set(key, value);
    } else {
      next.delete(key);
    }

    if (key !== 'page') {
      next.delete('page');
    }

    setParams(next, { replace: true });
  };

  const page = Number(get('page') ?? 1);
  const query = {
    teamId: team?.teamId,
    projectId: get('projectId'),
    aircraftId: get('aircraftId'),
    autopilot: get('autopilot'),
    status: get('status'),
    from: get('from'),
    to: get('to'),
    search: debouncedSearch || undefined,
    sort: get('sort') ?? 'OccurredAtDesc',
    page,
    pageSize: PAGE_SIZE,
  };
  const flights = useFlights(query, !!team);
  const projects = useProjects(team?.teamId);
  const aircraft = useAircraftList(team?.teamId);
  const filtered = ['projectId', 'aircraftId', 'autopilot', 'status', 'from', 'to'].some((k) => params.has(k)) || !!debouncedSearch;

  return (
    <>
      <PageHeader
        title="Uçuş geçmişi"
        description={flights.data ? `${flights.data.totalCount} uçuş` : undefined}
        actions={
          <Button component={Link} to="/upload" leftSection={<IconUpload size={16} />}>
            Log yükle
          </Button>
        }
      />
      <Card withBorder mb="md" padding="sm">
        <Group gap="sm" align="flex-end" wrap="wrap">
          <TextInput
            label="Ara"
            placeholder="Uçuş veya dosya adı"
            leftSection={<IconSearch size={14} />}
            value={search}
            onChange={(e) => {
              setSearch(e.currentTarget.value);
              set('search', e.currentTarget.value);
            }}
            w={220}
          />
          <Select
            label="Proje"
            clearable
            searchable
            data={projects.data?.items.map((p) => ({ value: p.id, label: p.name })) ?? []}
            value={get('projectId') ?? null}
            onChange={(v) => set('projectId', v)}
            w={180}
          />
          <Select
            label="Araç"
            clearable
            searchable
            data={aircraft.data?.items.map((a) => ({ value: a.id, label: a.name })) ?? []}
            value={get('aircraftId') ?? null}
            onChange={(v) => set('aircraftId', v)}
            w={180}
          />
          <Select
            label="Otopilot"
            clearable
            data={[
              { value: 'Px4', label: 'PX4' },
              { value: 'ArduPilot', label: 'ArduPilot' },
            ]}
            value={get('autopilot') ?? null}
            onChange={(v) => set('autopilot', v)}
            w={130}
          />
          <Select
            label="Durum"
            clearable
            data={Object.entries(statusLabels).map(([value, label]) => ({ value, label }))}
            value={get('status') ?? null}
            onChange={(v) => set('status', v)}
            w={160}
          />
          <DatePickerInput
            type="range"
            label="Tarih aralığı"
            clearable
            valueFormat="DD.MM.YYYY"
            value={[get('from') ?? null, get('to') ?? null]}
            onChange={([from, to]) => {
              const next = new URLSearchParams(params);
              next.delete('page');
              for (const [key, value] of [
                ['from', from ? dayjs(from).startOf('day').toISOString() : null],
                ['to', to ? dayjs(to).endOf('day').toISOString() : null],
              ] as const) {
                if (value) {
                  next.set(key, value);
                } else {
                  next.delete(key);
                }
              }

              setParams(next, { replace: true });
            }}
            w={230}
          />
          <Select label="Sıralama" data={sorts} value={query.sort} onChange={(v) => set('sort', v)} allowDeselect={false} w={130} />
        </Group>
      </Card>
      {flights.isPending ? (
        <LoadingState />
      ) : flights.isError ? (
        <ErrorState error={flights.error} onRetry={() => flights.refetch()} />
      ) : flights.data.items.length === 0 ? (
        <EmptyState title={filtered ? 'Filtreye uyan uçuş yok' : 'Henüz uçuş yok'}>
          {filtered ? 'Filtreleri değiştirin veya temizleyin.' : 'Bir PX4 (.ulg) ya da ArduPilot (.bin) logu yükleyerek başlayın.'}
        </EmptyState>
      ) : (
        <Card withBorder padding={0}>
          <Table.ScrollContainer minWidth={900}>
            <Table highlightOnHover verticalSpacing="xs">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Uçuş</Table.Th>
                  <Table.Th>Tarih</Table.Th>
                  <Table.Th>Süre</Table.Th>
                  <Table.Th>Araç</Table.Th>
                  <Table.Th>Otopilot / firmware</Table.Th>
                  <Table.Th>Dosya</Table.Th>
                  <Table.Th>Durum</Table.Th>
                  <Table.Th>Bulgular</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {flights.data.items.map((f) => (
                  <Table.Tr key={f.id}>
                    <Table.Td>
                      <Anchor component={Link} to={`/flights/${f.id}`} fw={500}>
                        {f.name}
                      </Anchor>
                      <Text size="xs" c="dimmed">
                        {f.projectName}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm">{formatDateTime(f.occurredAt)}</Text>
                      {!f.startedAt && (
                        <Text size="xs" c="dimmed">
                          yükleme zamanı
                        </Text>
                      )}
                    </Table.Td>
                    <Table.Td>{formatDuration(f.durationSeconds)}</Table.Td>
                    <Table.Td>{f.aircraftName ?? <Text c="dimmed" size="sm">—</Text>}</Table.Td>
                    <Table.Td>
                      <Text size="sm">{autopilotLabels[f.autopilot]}</Text>
                      <Text size="xs" c="dimmed">
                        {f.firmwareVersion ?? '—'}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm" lineClamp={1} maw={200}>
                        {f.fileName ?? '—'}
                      </Text>
                      <Text size="xs" c="dimmed">
                        {formatBytes(f.fileSizeBytes)}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <StatusBadge status={f.processingStatus as ProcessingStatus | null} />
                    </Table.Td>
                    <Table.Td>
                      {f.latestAnalysis ? (
                        f.latestAnalysis.findingCount === 0 ? (
                          <Text size="sm" c="dimmed">
                            Bulgu yok
                          </Text>
                        ) : (
                          <Group gap={4}>
                            {f.latestAnalysis.criticalCount > 0 && <SeverityBadge severity="Critical" count={f.latestAnalysis.criticalCount} />}
                            {f.latestAnalysis.warningCount > 0 && <SeverityBadge severity="Warning" count={f.latestAnalysis.warningCount} />}
                            {f.latestAnalysis.infoCount > 0 && <SeverityBadge severity="Info" count={f.latestAnalysis.infoCount} />}
                          </Group>
                        )
                      ) : (
                        <Text size="sm" c="dimmed">
                          —
                        </Text>
                      )}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
          {flights.data.totalPages > 1 && (
            <Group justify="center" p="sm">
              <Pagination total={flights.data.totalPages} value={page} onChange={(p) => set('page', String(p))} />
            </Group>
          )}
        </Card>
      )}
    </>
  );
}
