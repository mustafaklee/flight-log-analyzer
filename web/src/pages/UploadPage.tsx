import { ActionIcon, Alert, Anchor, Badge, Button, Card, Group, Progress, Select, Stack, Table, Text, TextInput, Tooltip } from '@mantine/core';
import { Dropzone } from '@mantine/dropzone';
import { IconFileUpload, IconRefresh, IconX } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { Link } from 'react-router';
import { ApiError, errorMessage, upload } from '../api/client';
import { useAircraftList, useLogFile, useProjects } from '../api/queries';
import type { UploadLogResult } from '../api/types';
import { useTeam } from '../auth/TeamContext';
import { PageHeader } from '../components/PageHeader';
import { EmptyState } from '../components/States';
import { StatusBadge } from '../components/StatusBadge';
import { formatBytes } from '../lib/format';

interface UploadItem {
  key: string;
  file: File;
  progress: number;
  state: 'waiting' | 'uploading' | 'done' | 'failed' | 'cancelled';
  result?: UploadLogResult;
  error?: string;
  duplicateFlightId?: string;
}

const ACCEPTED = /\.(ulg|bin)$/i;

/** The file's processing status after upload, kept live by the realtime connection. */
function LiveStatus({ logFileId }: { logFileId: string }) {
  const file = useLogFile(logFileId);
  return file.data ? <StatusBadge status={file.data.status} failureCode={file.data.failureCode} /> : <Badge variant="light">Yüklendi</Badge>;
}

export function UploadPage() {
  const { team, can } = useTeam();
  const client = useQueryClient();
  const projects = useProjects(team?.teamId);
  const aircraft = useAircraftList(team?.teamId);
  const [projectId, setProjectId] = useState<string | null>(null);
  const [aircraftId, setAircraftId] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [items, setItems] = useState<UploadItem[]>([]);
  const [rejected, setRejected] = useState<string[]>([]);
  const running = useRef(false);
  const abort = useRef<AbortController | null>(null);

  const update = (key: string, patch: Partial<UploadItem>) => setItems((current) => current.map((i) => (i.key === key ? { ...i, ...patch } : i)));

  // Uploads one file at a time: large logs share the connection better in sequence, and the API validates each.
  const run = async (queue: UploadItem[]) => {
    if (running.current || !projectId) {
      return;
    }

    running.current = true;
    for (const item of queue) {
      abort.current = new AbortController();
      update(item.key, { state: 'uploading', progress: 0, error: undefined });
      try {
        const result = await upload<UploadLogResult>(
          `/projects/${projectId}/logs`,
          item.file,
          { aircraftId, name: queue.length === 1 && name.trim() ? name.trim() : undefined },
          (p) => update(item.key, { progress: p }),
          abort.current.signal,
        );
        update(item.key, { state: 'done', progress: 1, result });
        client.invalidateQueries({ queryKey: ['flights'] });
        client.invalidateQueries({ queryKey: ['dashboard'] });
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') {
          update(item.key, { state: 'cancelled' });
        } else {
          const duplicate = err instanceof ApiError && err.problem.code === 'log_file.duplicate' ? (err.problem.flightId as string | undefined) : undefined;
          update(item.key, { state: 'failed', error: errorMessage(err), duplicateFlightId: duplicate });
        }
      }
    }

    abort.current = null;
    running.current = false;
  };

  const add = (files: File[]) => {
    const fresh = files.map<UploadItem>((file) => ({ key: `${file.name}-${file.size}-${crypto.randomUUID()}`, file, progress: 0, state: 'waiting' }));
    setItems((current) => [...fresh, ...current]);
    void run(fresh);
  };

  if (!can('Engineer')) {
    return (
      <>
        <PageHeader title="Log yükleme" />
        <Alert color="gray">Log yüklemek için ekipte en az Mühendis rolü gerekir.</Alert>
      </>
    );
  }

  const noProjects = projects.data && projects.data.items.length === 0;

  return (
    <>
      <PageHeader title="Log yükleme" description="PX4 ULog (.ulg) ve ArduPilot DataFlash (.bin) dosyaları. Her dosya yeni bir uçuş oluşturur." />
      <Stack>
        {noProjects && (
          <Alert color="yellow">
            Yükleme için önce bir proje gerekir. <Anchor component={Link} to="/projects">Proje oluşturun</Anchor>.
          </Alert>
        )}
        <Card withBorder>
          <Group grow align="flex-start" wrap="wrap">
            <Select
              label="Proje"
              required
              searchable
              data={projects.data?.items.map((p) => ({ value: p.id, label: p.name })) ?? []}
              value={projectId}
              onChange={setProjectId}
              placeholder="Proje seçin"
              miw={200}
            />
            <Select
              label="Araç"
              clearable
              searchable
              data={aircraft.data?.items.map((a) => ({ value: a.id, label: a.identifier ? `${a.name} (${a.identifier})` : a.name })) ?? []}
              value={aircraftId}
              onChange={setAircraftId}
              placeholder="İsteğe bağlı"
              miw={200}
            />
            <TextInput
              label="Uçuş adı"
              description="Tek dosya yüklenirken; boşsa dosya adı kullanılır"
              value={name}
              onChange={(e) => setName(e.currentTarget.value)}
              maxLength={200}
              miw={200}
            />
          </Group>
          <Dropzone
            mt="md"
            disabled={!projectId}
            onDrop={(files) => {
              setRejected([]);
              add(files.filter((f) => ACCEPTED.test(f.name)));
              const wrong = files.filter((f) => !ACCEPTED.test(f.name)).map((f) => f.name);
              if (wrong.length) {
                setRejected(wrong);
              }
            }}
            onReject={(r) => setRejected(r.map((x) => x.file.name))}
            multiple
            aria-label="Log dosyalarını seçin veya sürükleyin"
          >
            <Group justify="center" gap="md" mih={120} style={{ pointerEvents: 'none' }}>
              <IconFileUpload size={40} stroke={1.4} />
              <div>
                <Text size="lg">{projectId ? 'Dosyaları buraya sürükleyin veya tıklayın' : 'Önce bir proje seçin'}</Text>
                <Text size="sm" c="dimmed">
                  Birden fazla dosya seçilebilir; dosyalar sırayla yüklenir.
                </Text>
              </div>
            </Group>
          </Dropzone>
          {rejected.length > 0 && (
            <Alert color="red" mt="sm">
              Desteklenmeyen dosya: {rejected.join(', ')}. Yalnızca .ulg ve .bin kabul edilir.
            </Alert>
          )}
        </Card>

        <Card withBorder padding={0}>
          {items.length === 0 ? (
            <EmptyState title="Bu oturumda yükleme yapılmadı">Yüklenen dosyaların işlenme durumu burada canlı olarak görünür.</EmptyState>
          ) : (
            <Table.ScrollContainer minWidth={640}>
              <Table verticalSpacing="sm">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Dosya</Table.Th>
                    <Table.Th w={260}>Yükleme</Table.Th>
                    <Table.Th>İşleme</Table.Th>
                    <Table.Th w={50} />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {items.map((item) => (
                    <Table.Tr key={item.key}>
                      <Table.Td>
                        <Text size="sm" fw={500}>
                          {item.result ? (
                            <Anchor component={Link} to={`/flights/${item.result.flight.id}`}>
                              {item.file.name}
                            </Anchor>
                          ) : (
                            item.file.name
                          )}
                        </Text>
                        <Text size="xs" c="dimmed">
                          {formatBytes(item.file.size)}
                        </Text>
                      </Table.Td>
                      <Table.Td>
                        {item.state === 'uploading' && <Progress value={item.progress * 100} aria-label={`${item.file.name} yükleniyor`} animated />}
                        {item.state === 'waiting' && <Text size="sm" c="dimmed">Sırada</Text>}
                        {item.state === 'done' && <Text size="sm" c="green">Yüklendi</Text>}
                        {item.state === 'cancelled' && <Text size="sm" c="dimmed">İptal edildi</Text>}
                        {item.state === 'failed' && (
                          <Text size="sm" c="red">
                            {item.error}{' '}
                            {item.duplicateFlightId && (
                              <Anchor component={Link} to={`/flights/${item.duplicateFlightId}`} size="sm">
                                Mevcut uçuşu aç
                              </Anchor>
                            )}
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>{item.result ? <LiveStatus logFileId={item.result.logFile.id} /> : <Text c="dimmed">—</Text>}</Table.Td>
                      <Table.Td>
                        {item.state === 'uploading' && (
                          <Tooltip label="Yüklemeyi iptal et">
                            <ActionIcon variant="subtle" color="gray" onClick={() => abort.current?.abort()} aria-label="Yüklemeyi iptal et">
                              <IconX size={16} />
                            </ActionIcon>
                          </Tooltip>
                        )}
                        {(item.state === 'failed' || item.state === 'cancelled') && !item.duplicateFlightId && (
                          <Tooltip label="Tekrar dene">
                            <ActionIcon variant="subtle" onClick={() => void run([item])} aria-label="Tekrar dene">
                              <IconRefresh size={16} />
                            </ActionIcon>
                          </Tooltip>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          )}
        </Card>
        {items.some((i) => i.state === 'done') && (
          <Group>
            <Button variant="light" component={Link} to="/flights">
              Uçuş geçmişine git
            </Button>
          </Group>
        )}
      </Stack>
    </>
  );
}
