import { Anchor, Card, Grid, Group, SimpleGrid, Stack, Table, Text, Title } from '@mantine/core';
import { Link } from 'react-router';
import { useDashboard } from '../api/queries';
import type { FindingSeverity, ProcessingStatus } from '../api/types';
import { useTeam } from '../auth/TeamContext';
import { PageHeader } from '../components/PageHeader';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import { SeverityBadge, StatusBadge } from '../components/StatusBadge';
import { formatDateTime, formatDuration } from '../lib/format';

function Stat({ label, value, hint }: { label: string; value: React.ReactNode; hint?: string }) {
  return (
    <Card withBorder padding="md">
      <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
        {label}
      </Text>
      <Text fz={28} fw={700} lh={1.2}>
        {value}
      </Text>
      {hint && (
        <Text size="xs" c="dimmed">
          {hint}
        </Text>
      )}
    </Card>
  );
}

export function DashboardPage() {
  const { team } = useTeam();
  const dashboard = useDashboard(team?.teamId);

  return (
    <>
      <PageHeader title="Dashboard" description={team?.teamName} />
      {dashboard.isPending ? (
        <LoadingState />
      ) : dashboard.isError ? (
        <ErrorState error={dashboard.error} onRetry={() => dashboard.refetch()} />
      ) : (
        <Stack>
          <SimpleGrid cols={{ base: 2, md: 4 }}>
            <Stat label="Uçuş" value={dashboard.data.flightCount} hint={`Son 30 günde ${dashboard.data.flightsLast30Days}`} />
            <Stat label="Toplam uçuş süresi" value={formatDuration(dashboard.data.totalFlightHours * 3600)} />
            <Stat label="Araç" value={dashboard.data.aircraftCount} hint={`${dashboard.data.projectCount} proje`} />
            <Stat
              label="Kritik bulgulu uçuş"
              value={dashboard.data.flightsWithCriticalFindings}
              hint="Son analizlere göre"
            />
          </SimpleGrid>
          <Grid>
            <Grid.Col span={{ base: 12, md: 6 }}>
              <Card withBorder h="100%">
                <Title order={4} mb="sm">
                  İşleme durumu
                </Title>
                {Object.keys(dashboard.data.filesByStatus).length === 0 ? (
                  <Text c="dimmed" size="sm">
                    Henüz log yüklenmedi.
                  </Text>
                ) : (
                  <Group gap="xs">
                    {Object.entries(dashboard.data.filesByStatus).map(([status, count]) => (
                      <Group key={status} gap={4}>
                        <StatusBadge status={status as ProcessingStatus} />
                        <Text size="sm" fw={600}>
                          {count}
                        </Text>
                      </Group>
                    ))}
                  </Group>
                )}
              </Card>
            </Grid.Col>
            <Grid.Col span={{ base: 12, md: 6 }}>
              <Card withBorder h="100%">
                <Title order={4} mb="sm">
                  Bulgular (son analizler)
                </Title>
                <Group gap="xs">
                  {(['Critical', 'Warning', 'Info'] as FindingSeverity[]).map((s) => (
                    <SeverityBadge key={s} severity={s} count={dashboard.data.findingsBySeverity[s] ?? 0} />
                  ))}
                </Group>
              </Card>
            </Grid.Col>
          </Grid>
          <Grid>
            <Grid.Col span={{ base: 12, lg: 7 }}>
              <Card withBorder>
                <Group justify="space-between" mb="sm">
                  <Title order={4}>Son uçuşlar</Title>
                  <Anchor component={Link} to="/flights" size="sm">
                    Tümü
                  </Anchor>
                </Group>
                {dashboard.data.recentFlights.length === 0 ? (
                  <EmptyState title="Uçuş yok">
                    <Anchor component={Link} to="/upload">
                      Log yükleyerek
                    </Anchor>{' '}
                    ilk uçuşu oluşturun.
                  </EmptyState>
                ) : (
                  <Table.ScrollContainer minWidth={480}>
                    <Table highlightOnHover>
                      <Table.Thead>
                        <Table.Tr>
                          <Table.Th>Uçuş</Table.Th>
                          <Table.Th>Tarih</Table.Th>
                          <Table.Th>Süre</Table.Th>
                          <Table.Th>Durum</Table.Th>
                        </Table.Tr>
                      </Table.Thead>
                      <Table.Tbody>
                        {dashboard.data.recentFlights.map((f) => (
                          <Table.Tr key={f.id}>
                            <Table.Td>
                              <Anchor component={Link} to={`/flights/${f.id}`}>
                                {f.name}
                              </Anchor>
                              <Text size="xs" c="dimmed">
                                {f.projectName}
                              </Text>
                            </Table.Td>
                            <Table.Td>{formatDateTime(f.occurredAt)}</Table.Td>
                            <Table.Td>{formatDuration(f.durationSeconds)}</Table.Td>
                            <Table.Td>
                              <StatusBadge status={f.processingStatus} />
                            </Table.Td>
                          </Table.Tr>
                        ))}
                      </Table.Tbody>
                    </Table>
                  </Table.ScrollContainer>
                )}
              </Card>
            </Grid.Col>
            <Grid.Col span={{ base: 12, lg: 5 }}>
              <Card withBorder>
                <Title order={4} mb="sm">
                  Dikkat gerektiren bulgular
                </Title>
                {dashboard.data.recentFindings.length === 0 ? (
                  <Text c="dimmed" size="sm">
                    Son analizlerde uyarı veya kritik bulgu yok.
                  </Text>
                ) : (
                  <Stack gap="xs">
                    {dashboard.data.recentFindings.map((f) => (
                      <Group key={f.findingId} justify="space-between" wrap="nowrap" align="flex-start">
                        <Stack gap={0} style={{ minWidth: 0 }}>
                          <Anchor component={Link} to={`/flights/${f.flightId}?tab=findings&finding=${f.findingId}`} size="sm" lineClamp={1}>
                            {f.title}
                          </Anchor>
                          <Text size="xs" c="dimmed" lineClamp={1}>
                            {f.flightName} · {f.ruleId}
                          </Text>
                        </Stack>
                        <SeverityBadge severity={f.severity} />
                      </Group>
                    ))}
                  </Stack>
                )}
              </Card>
            </Grid.Col>
          </Grid>
        </Stack>
      )}
    </>
  );
}
