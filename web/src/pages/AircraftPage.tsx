import { Anchor, Badge, Button, Card, Group, Modal, SimpleGrid, Stack, Table, Text } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { modals } from '@mantine/modals';
import { IconEdit, IconTrash } from '@tabler/icons-react';
import { Link, useNavigate, useParams } from 'react-router';
import { errorMessage } from '../api/client';
import { useAircraft, useDeleteAircraft, useFlights } from '../api/queries';
import { useTeam } from '../auth/TeamContext';
import { PageHeader } from '../components/PageHeader';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import { StatusBadge } from '../components/StatusBadge';
import { formatDateTime, formatDuration } from '../lib/format';
import { autopilotLabels } from '../lib/labels';
import { AircraftForm, aircraftTypeLabels } from './FleetPage';

function Field({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div>
      <Text size="xs" c="dimmed">
        {label}
      </Text>
      <Text size="sm">{value || '—'}</Text>
    </div>
  );
}

export function AircraftPage() {
  const { aircraftId } = useParams();
  const navigate = useNavigate();
  const { can } = useTeam();
  const aircraft = useAircraft(aircraftId);
  const flights = useFlights({ aircraftId, pageSize: 50 }, !!aircraftId);
  const remove = useDeleteAircraft(aircraft.data?.teamId ?? '');
  const [editing, { open, close }] = useDisclosure();

  if (aircraft.isPending) {
    return <LoadingState />;
  }

  if (aircraft.isError) {
    return <ErrorState error={aircraft.error} onRetry={() => aircraft.refetch()} />;
  }

  const a = aircraft.data;
  const confirmDelete = () =>
    modals.openConfirmModal({
      title: 'Aracı sil',
      children: <Text size="sm">{a.name} silinecek. Uçuşları korunur, yalnızca araç bağlantısı kalkar.</Text>,
      labels: { confirm: 'Sil', cancel: 'Vazgeç' },
      confirmProps: { color: 'red' },
      onConfirm: () => remove.mutate(a.id, { onSuccess: () => navigate('/fleet') }),
    });

  return (
    <>
      <PageHeader
        title={a.name}
        description={a.identifier ?? undefined}
        actions={
          <>
            {can('Engineer') && (
              <Button variant="light" leftSection={<IconEdit size={16} />} onClick={open}>
                Düzenle
              </Button>
            )}
            {can('ProjectManager') && (
              <Button variant="light" color="red" leftSection={<IconTrash size={16} />} onClick={confirmDelete} loading={remove.isPending}>
                Sil
              </Button>
            )}
          </>
        }
      />
      {remove.error && (
        <Text c="red" mb="sm">
          {errorMessage(remove.error)}
        </Text>
      )}
      <Stack>
        <Card withBorder>
          <SimpleGrid cols={{ base: 2, md: 4 }}>
            <Field label="Tip" value={aircraftTypeLabels[a.type]} />
            <Field label="Otopilot" value={autopilotLabels[a.autopilot]} />
            <Field label="Firmware" value={[a.firmwareName, a.firmwareVersion].filter(Boolean).join(' ')} />
            <Field label="Uçuş kontrolcüsü" value={a.flightController} />
            <Field label="Donanım" value={a.hardware} />
            <Field
              label="Etiketler"
              value={
                a.tags.length ? (
                  <Group gap={4}>
                    {a.tags.map((t) => (
                      <Badge key={t} variant="outline" size="sm">
                        {t}
                      </Badge>
                    ))}
                  </Group>
                ) : null
              }
            />
            <Field label="Eklendi" value={formatDateTime(a.createdAt)} />
          </SimpleGrid>
          {a.description && (
            <Text size="sm" mt="md" style={{ whiteSpace: 'pre-wrap' }}>
              {a.description}
            </Text>
          )}
        </Card>
        <Card withBorder padding={0}>
          <Text fw={600} p="sm">
            Uçuşlar {flights.data ? `(${flights.data.totalCount})` : ''}
          </Text>
          {flights.isPending ? (
            <LoadingState />
          ) : flights.isError ? (
            <ErrorState error={flights.error} />
          ) : flights.data.items.length === 0 ? (
            <EmptyState title="Bu araca bağlı uçuş yok" />
          ) : (
            <Table.ScrollContainer minWidth={500}>
              <Table highlightOnHover>
                <Table.Tbody>
                  {flights.data.items.map((f) => (
                    <Table.Tr key={f.id}>
                      <Table.Td>
                        <Anchor component={Link} to={`/flights/${f.id}`}>
                          {f.name}
                        </Anchor>
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
      </Stack>
      <Modal opened={editing} onClose={close} title="Aracı düzenle" size="lg">
        <AircraftForm teamId={a.teamId} aircraft={a} onDone={close} />
      </Modal>
    </>
  );
}
