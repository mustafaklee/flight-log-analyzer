import { Anchor, Badge, Button, Card, Group, Modal, Select, SimpleGrid, Stack, Table, TagsInput, Text, TextInput, Textarea } from '@mantine/core';
import { useDebouncedValue, useDisclosure } from '@mantine/hooks';
import { IconPlus, IconSearch } from '@tabler/icons-react';
import { useState } from 'react';
import { Link } from 'react-router';
import { errorMessage } from '../api/client';
import { useAircraftList, useSaveAircraft } from '../api/queries';
import type { Aircraft, AircraftType, Autopilot, SaveAircraftRequest } from '../api/types';
import { useTeam } from '../auth/TeamContext';
import { PageHeader } from '../components/PageHeader';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import { autopilotLabels } from '../lib/labels';

export const aircraftTypeLabels: Record<AircraftType, string> = {
  Other: 'Diğer',
  Multicopter: 'Multikopter',
  FixedWing: 'Sabit kanat',
  Vtol: 'VTOL',
  Helicopter: 'Helikopter',
};

const empty: SaveAircraftRequest = {
  name: '',
  identifier: null,
  type: 'Multicopter',
  autopilot: 'Px4',
  firmwareName: null,
  firmwareVersion: null,
  flightController: null,
  hardware: null,
  description: null,
  tags: [],
  version: null,
};

export function AircraftForm({ teamId, aircraft, onDone }: { teamId: string; aircraft?: Aircraft; onDone: (saved: Aircraft) => void }) {
  const [form, setForm] = useState<SaveAircraftRequest>(aircraft ? { ...aircraft } : empty);
  const save = useSaveAircraft(teamId);
  const field = (key: keyof SaveAircraftRequest) => ({
    value: (form[key] as string | null) ?? '',
    onChange: (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => setForm({ ...form, [key]: e.currentTarget.value || null }),
  });

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        save.mutate({ id: aircraft?.id, body: { ...form, name: form.name.trim() } }, { onSuccess: onDone });
      }}
    >
      <Stack>
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <TextInput label="Ad" required {...field('name')} value={form.name} maxLength={100} />
          <TextInput label="Kimlik / seri no" description="Ekip içinde tekil" {...field('identifier')} maxLength={100} />
          <Select
            label="Tip"
            data={Object.entries(aircraftTypeLabels).map(([value, label]) => ({ value, label }))}
            value={form.type}
            onChange={(v) => v && setForm({ ...form, type: v as AircraftType })}
            allowDeselect={false}
          />
          <Select
            label="Otopilot"
            data={(['Px4', 'ArduPilot', 'Unknown'] as Autopilot[]).map((value) => ({ value, label: autopilotLabels[value] }))}
            value={form.autopilot}
            onChange={(v) => v && setForm({ ...form, autopilot: v as Autopilot })}
            allowDeselect={false}
          />
          <TextInput label="Firmware" {...field('firmwareName')} maxLength={100} />
          <TextInput label="Firmware sürümü" {...field('firmwareVersion')} maxLength={50} />
          <TextInput label="Uçuş kontrolcüsü" {...field('flightController')} maxLength={100} />
          <TextInput label="Donanım" {...field('hardware')} maxLength={100} />
        </SimpleGrid>
        <TagsInput label="Etiketler" value={form.tags ?? []} onChange={(tags) => setForm({ ...form, tags })} maxTags={20} />
        <Textarea label="Açıklama" {...field('description')} autosize minRows={2} maxLength={2000} />
        {save.error && (
          <Text c="red" size="sm" role="alert">
            {errorMessage(save.error)}
          </Text>
        )}
        <Group justify="flex-end">
          <Button type="submit" loading={save.isPending} disabled={!form.name.trim()}>
            Kaydet
          </Button>
        </Group>
      </Stack>
    </form>
  );
}

export function FleetPage() {
  const { team, can } = useTeam();
  const [search, setSearch] = useState('');
  const [debounced] = useDebouncedValue(search, 300);
  const [autopilot, setAutopilot] = useState<string | null>(null);
  const [opened, { open, close }] = useDisclosure();
  const list = useAircraftList(team?.teamId, { search: debounced || undefined, autopilot, pageSize: 100 });

  return (
    <>
      <PageHeader
        title="Filo"
        description="Ekibin araçları"
        actions={
          can('Engineer') && (
            <Button leftSection={<IconPlus size={16} />} onClick={open}>
              Araç ekle
            </Button>
          )
        }
      />
      <Group mb="md">
        <TextInput placeholder="Ad, kimlik veya etiket" leftSection={<IconSearch size={14} />} value={search} onChange={(e) => setSearch(e.currentTarget.value)} aria-label="Araç ara" />
        <Select
          placeholder="Otopilot"
          aria-label="Otopilot"
          clearable
          data={[
            { value: 'Px4', label: 'PX4' },
            { value: 'ArduPilot', label: 'ArduPilot' },
          ]}
          value={autopilot}
          onChange={setAutopilot}
          w={150}
        />
      </Group>
      {list.isPending ? (
        <LoadingState />
      ) : list.isError ? (
        <ErrorState error={list.error} onRetry={() => list.refetch()} />
      ) : list.data.items.length === 0 ? (
        <EmptyState title="Araç yok">Araç ekleyip uçuş loglarını araca bağlayabilirsiniz.</EmptyState>
      ) : (
        <Card withBorder padding={0}>
          <Table.ScrollContainer minWidth={700}>
            <Table highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Araç</Table.Th>
                  <Table.Th>Tip</Table.Th>
                  <Table.Th>Otopilot / firmware</Table.Th>
                  <Table.Th>Donanım</Table.Th>
                  <Table.Th>Etiketler</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {list.data.items.map((a) => (
                  <Table.Tr key={a.id}>
                    <Table.Td>
                      <Anchor component={Link} to={`/fleet/${a.id}`} fw={500}>
                        {a.name}
                      </Anchor>
                      <Text size="xs" c="dimmed">
                        {a.identifier ?? '—'}
                      </Text>
                    </Table.Td>
                    <Table.Td>{aircraftTypeLabels[a.type]}</Table.Td>
                    <Table.Td>
                      {autopilotLabels[a.autopilot]}
                      <Text size="xs" c="dimmed">
                        {[a.firmwareName, a.firmwareVersion].filter(Boolean).join(' ') || '—'}
                      </Text>
                    </Table.Td>
                    <Table.Td>{[a.flightController, a.hardware].filter(Boolean).join(' / ') || '—'}</Table.Td>
                    <Table.Td>
                      <Group gap={4}>
                        {a.tags.map((t) => (
                          <Badge key={t} variant="outline" size="sm">
                            {t}
                          </Badge>
                        ))}
                      </Group>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Card>
      )}
      <Modal opened={opened} onClose={close} title="Araç ekle" size="lg">
        {team && <AircraftForm teamId={team.teamId} onDone={close} />}
      </Modal>
    </>
  );
}
