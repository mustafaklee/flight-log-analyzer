import { Button, Card, Group, Select, SimpleGrid, Stack, Table, Text, TextInput, Textarea, Title } from '@mantine/core';
import { modals } from '@mantine/modals';
import { notifications } from '@mantine/notifications';
import { IconTrash } from '@tabler/icons-react';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { errorMessage } from '../api/client';
import { useAircraftList, useDeleteFlight, useUpdateFlight } from '../api/queries';
import type { Flight } from '../api/types';
import { useTeam } from '../auth/TeamContext';
import { formatDateTime, formatDuration } from '../lib/format';
import { autopilotLabels } from '../lib/labels';

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

/** What the log says about the flight, plus the fields the team edits (name, notes, aircraft). */
export function MetadataPanel({ flight }: { flight: Flight }) {
  const { can } = useTeam();
  const navigate = useNavigate();
  const aircraft = useAircraftList(flight.teamId, { pageSize: 100 });
  const update = useUpdateFlight();
  const remove = useDeleteFlight();
  const [name, setName] = useState(flight.name);
  const [notes, setNotes] = useState(flight.notes ?? '');
  const [aircraftId, setAircraftId] = useState<string | null>(flight.aircraftId);
  const editable = can('Engineer');
  const dirty = name.trim() !== flight.name || (notes.trim() || null) !== flight.notes || aircraftId !== flight.aircraftId;

  const save = () =>
    update.mutate(
      { id: flight.id, body: { name: name.trim(), notes: notes.trim() || null, aircraftId, version: flight.version } },
      {
        onSuccess: () => notifications.show({ color: 'green', message: 'Uçuş kaydedildi.' }),
        onError: (e) => notifications.show({ color: 'red', title: 'Kaydedilemedi', message: errorMessage(e) }),
      },
    );

  const confirmDelete = () =>
    modals.openConfirmModal({
      title: 'Uçuşu sil',
      children: <Text size="sm">{flight.name} ve log dosyası, işlenmiş veriler ve analizler silinecek. Bu işlem geri alınamaz.</Text>,
      labels: { confirm: 'Sil', cancel: 'Vazgeç' },
      confirmProps: { color: 'red' },
      onConfirm: () =>
        remove.mutate(flight.id, {
          onSuccess: () => navigate('/flights'),
          onError: (e) => notifications.show({ color: 'red', title: 'Silinemedi', message: errorMessage(e) }),
        }),
    });

  const metadata = Object.entries(flight.logMetadata);

  return (
    <Stack>
      <Card withBorder>
        <Title order={5} mb="sm">
          Logdan okunanlar
        </Title>
        <SimpleGrid cols={{ base: 2, md: 4 }}>
          <Field label="Otopilot" value={autopilotLabels[flight.autopilot]} />
          <Field label="Firmware" value={flight.firmwareVersion} />
          <Field label="Araç tipi" value={flight.vehicleType} />
          <Field label="Donanım" value={flight.hardware} />
          <Field label="Başlangıç (UTC, GPS saatinden)" value={flight.startedAt ? formatDateTime(flight.startedAt) : 'Logda GPS saati yok'} />
          <Field label="Log süresi" value={formatDuration(flight.durationSeconds)} />
          <Field label="Proje" value={flight.projectName} />
          <Field label="Kayıt" value={formatDateTime(flight.createdAt)} />
        </SimpleGrid>
        {metadata.length > 0 && (
          <Table mt="md" fz="xs" withRowBorders={false} verticalSpacing={2}>
            <Table.Tbody>
              {metadata.map(([k, v]) => (
                <Table.Tr key={k}>
                  <Table.Th w={220} ff="monospace">
                    {k}
                  </Table.Th>
                  <Table.Td style={{ wordBreak: 'break-all' }}>{v}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </Card>

      <Card withBorder>
        <Title order={5} mb="sm">
          Uçuş bilgileri
        </Title>
        <Stack>
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <TextInput label="Ad" value={name} onChange={(e) => setName(e.currentTarget.value)} maxLength={200} readOnly={!editable} required />
            <Select
              label="Araç"
              placeholder="Araç seçilmedi"
              clearable
              searchable
              readOnly={!editable}
              data={(aircraft.data?.items ?? []).map((a) => ({ value: a.id, label: a.identifier ? `${a.name} (${a.identifier})` : a.name }))}
              value={aircraftId}
              onChange={setAircraftId}
            />
          </SimpleGrid>
          <Textarea label="Notlar" value={notes} onChange={(e) => setNotes(e.currentTarget.value)} autosize minRows={3} maxLength={4000} readOnly={!editable} />
          <Group justify="space-between">
            {can('ProjectManager') ? (
              <Button color="red" variant="subtle" leftSection={<IconTrash size={14} />} onClick={confirmDelete} loading={remove.isPending}>
                Uçuşu sil
              </Button>
            ) : (
              <span />
            )}
            {editable && (
              <Button onClick={save} loading={update.isPending} disabled={!dirty || !name.trim()}>
                Kaydet
              </Button>
            )}
          </Group>
        </Stack>
      </Card>
    </Stack>
  );
}
