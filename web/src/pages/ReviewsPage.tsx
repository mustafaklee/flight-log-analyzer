import { Anchor, Card, Group, Pagination, SegmentedControl, Select, Table, Text } from '@mantine/core';
import { IconFlag } from '@tabler/icons-react';
import { useState, type ReactNode } from 'react';
import { Link } from 'react-router';
import { useTeamReviews } from '../api/queries';
import type { FindingReviewStatus } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { useTeam } from '../auth/TeamContext';
import { PageHeader } from '../components/PageHeader';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import { SeverityBadge } from '../components/StatusBadge';
import { formatDateTime, formatLogTime } from '../lib/format';
import { reviewStatusLabels } from '../lib/labels';

const STATUSES: FindingReviewStatus[] = ['Open', 'Investigating', 'Confirmed', 'Dismissed', 'Resolved'];

/** The team's reviewed findings: what is assigned to me, flagged or still open, across flights. */
export function ReviewsPage() {
  const { team } = useTeam();
  const { user } = useAuth();
  const [scope, setScope] = useState<'mine' | 'flagged' | 'open' | 'all'>('mine');
  const [status, setStatus] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const reviews = useTeamReviews(team?.teamId, {
    assigneeId: scope === 'mine' ? user?.id : undefined,
    flagged: scope === 'flagged' ? true : undefined,
    openOnly: scope === 'open' ? true : undefined,
    status,
    page,
    pageSize: 50,
  });

  return (
    <>
      <PageHeader title="İncelemeler" description="Atanmış, işaretlenmiş veya incelemesi süren bulgular" />
      <Card withBorder mb="md" padding="sm">
        <Group>
          <SegmentedControl
            size="xs"
            value={scope}
            onChange={(v) => {
              setScope(v as typeof scope);
              setPage(1);
            }}
            data={[
              { value: 'mine', label: 'Bana atananlar' },
              { value: 'flagged', label: 'İşaretliler' },
              { value: 'open', label: 'Sonuçlanmamış' },
              { value: 'all', label: 'Tümü' },
            ]}
          />
          <Select
            size="xs"
            aria-label="Durum"
            placeholder="Tüm durumlar"
            clearable
            data={STATUSES.map((s) => ({ value: s, label: reviewStatusLabels[s] }))}
            value={status}
            onChange={(v) => {
              setStatus(v);
              setPage(1);
            }}
          />
        </Group>
      </Card>
      {reviews.isPending ? (
        <LoadingState />
      ) : reviews.isError ? (
        <ErrorState error={reviews.error} onRetry={() => reviews.refetch()} />
      ) : reviews.data.items.length === 0 ? (
        <EmptyState title="Kayıt yok">
          {scope === 'mine' ? 'Size atanmış bir bulgu yok.' : 'Bu filtreye uyan incelenmiş bulgu yok. Bulgular uçuş sayfasındaki Bulgular sekmesinden incelenir.'}
        </EmptyState>
      ) : (
        <Card withBorder padding={0}>
          <Table.ScrollContainer minWidth={800}>
            <Table highlightOnHover verticalSpacing="xs" data-testid="reviews-table">
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Bulgu</Table.Th>
                  <Table.Th>Uçuş</Table.Th>
                  <Table.Th>Zaman</Table.Th>
                  <Table.Th>İnceleme</Table.Th>
                  <Table.Th>Son değişiklik</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {reviews.data.items.map((r) => (
                  <Table.Tr key={r.reviewId}>
                    <Table.Td>
                      <Group gap="xs" wrap="nowrap">
                        {r.severity && <SeverityBadge severity={r.severity} />}
                        <Stack0>
                          {r.findingId ? (
                            <Anchor component={Link} to={`/flights/${r.flightId}?tab=findings&finding=${r.findingId}`} size="sm">
                              {r.title ?? r.ruleId}
                            </Anchor>
                          ) : (
                            <Text size="sm">{r.ruleId}</Text>
                          )}
                          {!r.findingId && (
                            <Text size="xs" c="dimmed">
                              Son analizde bu bulgu artık yok.
                            </Text>
                          )}
                        </Stack0>
                      </Group>
                    </Table.Td>
                    <Table.Td>
                      <Anchor component={Link} to={`/flights/${r.flightId}`} size="sm">
                        {r.flightName}
                      </Anchor>
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs">
                        {formatLogTime(r.startUs, r.logStartUs ?? 0)} – {formatLogTime(r.endUs, r.logStartUs ?? 0)}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Group gap={6}>
                        <Text size="xs">{reviewStatusLabels[r.status]}</Text>
                        {r.flagged && <IconFlag size={14} color="var(--mantine-color-red-6)" aria-label="İşaretli" />}
                        <Text size="xs" c="dimmed">
                          {r.assignee?.displayName ?? 'atanmamış'}
                        </Text>
                      </Group>
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs">{formatDateTime(r.updatedAt)}</Text>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
          {reviews.data.totalPages > 1 && (
            <Group justify="center" p="sm">
              <Pagination total={reviews.data.totalPages} value={page} onChange={setPage} />
            </Group>
          )}
        </Card>
      )}
    </>
  );
}

function Stack0({ children }: { children: ReactNode }) {
  return <div style={{ minWidth: 0 }}>{children}</div>;
}
