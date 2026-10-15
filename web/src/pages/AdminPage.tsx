import { Badge, Button, Card, Checkbox, Group, Modal, Pagination, PasswordInput, SegmentedControl, SimpleGrid, Stack, Table, Tabs, Text, TextInput, Title } from '@mantine/core';
import { useDebouncedValue, useDisclosure } from '@mantine/hooks';
import { IconPlus, IconSearch } from '@tabler/icons-react';
import { useState } from 'react';
import { errorMessage } from '../api/client';
import { useAdminUsers, useCreateUser, useProcessingStats, useSetUserDisabled } from '../api/queries';
import type { DurationStats } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { PageHeader } from '../components/PageHeader';
import { ErrorState, LoadingState } from '../components/States';
import { formatBytes, formatDateTime } from '../lib/format';
import { AdminAiUsage } from './AdminAiUsage';

function Users() {
  const { user } = useAuth();
  const [search, setSearch] = useState('');
  const [debounced] = useDebouncedValue(search, 300);
  const [page, setPage] = useState(1);
  const users = useAdminUsers({ search: debounced || undefined, page, pageSize: 25 });
  const toggle = useSetUserDisabled();
  const create = useCreateUser();
  const [opened, { open, close }] = useDisclosure();
  const [form, setForm] = useState({ email: '', displayName: '', password: '', isAdministrator: false });

  return (
    <Stack>
      <Group justify="space-between">
        <TextInput placeholder="E-posta veya ad" leftSection={<IconSearch size={14} />} value={search} onChange={(e) => { setSearch(e.currentTarget.value); setPage(1); }} aria-label="Kullanıcı ara" />
        <Button leftSection={<IconPlus size={16} />} onClick={open}>
          Kullanıcı oluştur
        </Button>
      </Group>
      {toggle.error && <Text c="red">{errorMessage(toggle.error)}</Text>}
      {users.isPending ? (
        <LoadingState />
      ) : users.isError ? (
        <ErrorState error={users.error} />
      ) : (
        <Card withBorder padding={0}>
          <Table.ScrollContainer minWidth={600}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Kullanıcı</Table.Th>
                  <Table.Th>Rol</Table.Th>
                  <Table.Th>Oluşturuldu</Table.Th>
                  <Table.Th>Durum</Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {users.data.items.map((u) => (
                  <Table.Tr key={u.id}>
                    <Table.Td>
                      <Text size="sm" fw={500}>{u.displayName}</Text>
                      <Text size="xs" c="dimmed">{u.email}</Text>
                    </Table.Td>
                    <Table.Td>{u.isAdministrator ? <Badge>Yönetici</Badge> : <Text size="sm">Kullanıcı</Text>}</Table.Td>
                    <Table.Td>{formatDateTime(u.createdAt)}</Table.Td>
                    <Table.Td>
                      <Group gap="xs">
                        {u.isDisabled ? <Badge color="gray">Devre dışı</Badge> : <Badge color="green" variant="light">Etkin</Badge>}
                        {u.id !== user?.id && (
                          <Button size="compact-xs" variant="subtle" onClick={() => toggle.mutate({ id: u.id, disabled: !u.isDisabled })}>
                            {u.isDisabled ? 'Etkinleştir' : 'Devre dışı bırak'}
                          </Button>
                        )}
                      </Group>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
          {users.data.totalPages > 1 && (
            <Group justify="center" p="sm">
              <Pagination total={users.data.totalPages} value={page} onChange={setPage} />
            </Group>
          )}
        </Card>
      )}
      <Modal opened={opened} onClose={close} title="Kullanıcı oluştur">
        <form
          onSubmit={(e) => {
            e.preventDefault();
            create.mutate(form, {
              onSuccess: () => {
                setForm({ email: '', displayName: '', password: '', isAdministrator: false });
                close();
              },
            });
          }}
        >
          <Stack>
            <TextInput label="E-posta" type="email" required value={form.email} onChange={(e) => setForm({ ...form, email: e.currentTarget.value })} />
            <TextInput label="Ad soyad" required value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.currentTarget.value })} />
            <PasswordInput label="İlk parola" required value={form.password} onChange={(e) => setForm({ ...form, password: e.currentTarget.value })} description="En az 10 karakter; büyük, küçük harf ve rakam" />
            <Checkbox label="Yönetici" checked={form.isAdministrator} onChange={(e) => setForm({ ...form, isAdministrator: e.currentTarget.checked })} />
            {create.error && <Text c="red" size="sm">{errorMessage(create.error)}</Text>}
            <Group justify="flex-end">
              <Button type="submit" loading={create.isPending}>Oluştur</Button>
            </Group>
          </Stack>
        </form>
      </Modal>
    </Stack>
  );
}

function Duration({ label, stats }: { label: string; stats: DurationStats }) {
  return (
    <Card withBorder>
      <Text size="xs" c="dimmed" tt="uppercase" fw={600}>{label}</Text>
      {stats.count === 0 ? (
        <Text c="dimmed" size="sm">Veri yok</Text>
      ) : (
        <Text size="sm">
          ort. {stats.averageMs?.toFixed(0)} ms · p95 {stats.p95Ms?.toFixed(0)} ms · en fazla {stats.maxMs?.toFixed(0)} ms
          <Text span c="dimmed"> ({stats.count})</Text>
        </Text>
      )}
    </Card>
  );
}

function Counts({ title, values }: { title: string; values: Record<string, number> }) {
  return (
    <Card withBorder>
      <Text size="xs" c="dimmed" tt="uppercase" fw={600} mb={4}>{title}</Text>
      {Object.keys(values).length === 0 ? (
        <Text c="dimmed" size="sm">—</Text>
      ) : (
        <Group gap="xs">
          {Object.entries(values).map(([k, v]) => (
            <Badge key={k} variant="light" color="gray">{k}: {v}</Badge>
          ))}
        </Group>
      )}
    </Card>
  );
}

function Processing() {
  const [hours, setHours] = useState('24');
  const stats = useProcessingStats(Number(hours));
  return (
    <Stack>
      <SegmentedControl
        w="fit-content"
        value={hours}
        onChange={setHours}
        data={[
          { value: '1', label: '1 saat' },
          { value: '24', label: '24 saat' },
          { value: '168', label: '7 gün' },
          { value: '720', label: '30 gün' },
        ]}
      />
      {stats.isPending ? (
        <LoadingState />
      ) : stats.isError ? (
        <ErrorState error={stats.error} />
      ) : (
        <>
          <SimpleGrid cols={{ base: 1, md: 3 }}>
            <Counts title="Dosyalar (tümü)" values={stats.data.filesByStatus} />
            <Counts title="İşleme denemeleri" values={stats.data.jobsByOutcome} />
            <Counts title="Analizler" values={stats.data.analysisRunsByStatus} />
            <Duration label="İşleme süresi" stats={stats.data.jobDuration} />
            <Duration label="Kuyrukta bekleme" stats={stats.data.queueWait} />
            <Duration label="Analiz süresi" stats={stats.data.analysisDuration} />
          </SimpleGrid>
          <SimpleGrid cols={{ base: 1, md: 2 }}>
            <Card withBorder>
              <Title order={5} mb="xs">Hatalar</Title>
              <Text size="sm">
                Hata oranı: {stats.data.jobFailureRate == null ? '—' : `%${(stats.data.jobFailureRate * 100).toFixed(1)}`} · Otomatik yeniden deneme: {stats.data.automaticRetries} · En yüksek bellek: {formatBytes(stats.data.maxAllocatedBytes)}
              </Text>
              {stats.data.failureCodes.length > 0 && (
                <Table mt="xs">
                  <Table.Tbody>
                    {stats.data.failureCodes.map((c) => (
                      <Table.Tr key={c.code}>
                        <Table.Td><code>{c.code}</code></Table.Td>
                        <Table.Td>{c.count}</Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              )}
            </Card>
            <Card withBorder>
              <Title order={5} mb="xs">Kuyruk</Title>
              <Text size="sm">
                Bekleyen outbox mesajı: {stats.data.outboxPending}
                {stats.data.oldestPendingMessageAt && ` (en eski ${formatDateTime(stats.data.oldestPendingMessageAt)})`}
              </Text>
              {stats.data.queues.length === 0 ? (
                <Text size="sm" c="dimmed">Broker kullanılmıyor (veritabanı taşıması).</Text>
              ) : (
                <Table mt="xs">
                  <Table.Tbody>
                    {stats.data.queues.map((q) => (
                      <Table.Tr key={q.queue}>
                        <Table.Td><code>{q.queue}</code></Table.Td>
                        <Table.Td>{q.messages} mesaj</Table.Td>
                        <Table.Td>{q.consumers} tüketici</Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              )}
            </Card>
          </SimpleGrid>
        </>
      )}
    </Stack>
  );
}

export function AdminPage() {
  return (
    <>
      <PageHeader title="Yönetim" />
      <Tabs defaultValue="processing" keepMounted={false}>
        <Tabs.List mb="md">
          <Tabs.Tab value="processing">İşleme</Tabs.Tab>
          <Tabs.Tab value="ai">AI kullanımı</Tabs.Tab>
          <Tabs.Tab value="users">Kullanıcılar</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="processing">
          <Processing />
        </Tabs.Panel>
        <Tabs.Panel value="ai">
          <AdminAiUsage />
        </Tabs.Panel>
        <Tabs.Panel value="users">
          <Users />
        </Tabs.Panel>
      </Tabs>
    </>
  );
}
