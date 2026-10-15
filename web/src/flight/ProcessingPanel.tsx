import { Alert, Badge, Button, Card, Group, Stack, Table, Text, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconDownload, IconPlayerStop, IconRefresh } from '@tabler/icons-react';
import { useState } from 'react';
import { download, errorMessage } from '../api/client';
import { isActive, useJobs, useLogFileAction } from '../api/queries';
import type { LogFile, ProcessingJob } from '../api/types';
import { useTeam } from '../auth/TeamContext';
import { StatusBadge } from '../components/StatusBadge';
import { ErrorState, LoadingState } from '../components/States';
import { formatBytes, formatDateTime } from '../lib/format';

const outcomeLabels: Record<ProcessingJob['outcome'], string> = { Running: 'Çalışıyor', Succeeded: 'Başarılı', Failed: 'Başarısız', Cancelled: 'İptal' };
const outcomeColors: Record<ProcessingJob['outcome'], string> = { Running: 'blue', Succeeded: 'green', Failed: 'red', Cancelled: 'gray' };
const formatLabels: Record<LogFile['format'], string> = { Px4ULog: 'PX4 ULog', ArduPilotDataFlash: 'ArduPilot DataFlash' };

/** The uploaded file, its processing state and history, and the actions the user's role allows. */
export function ProcessingPanel({ file }: { file: LogFile }) {
  const { can } = useTeam();
  const jobs = useJobs(file.id);
  const action = useLogFileAction();
  const [downloading, setDownloading] = useState(false);

  const run = (kind: 'cancel' | 'retry') =>
    action.mutate(
      { id: file.id, action: kind },
      {
        onSuccess: () => notifications.show({ color: 'green', message: kind === 'cancel' ? 'İşleme iptal edildi.' : 'Dosya yeniden kuyruğa alındı.' }),
        onError: (e) => notifications.show({ color: 'red', title: 'İşlem yapılamadı', message: errorMessage(e) }),
      },
    );

  const save = async () => {
    setDownloading(true);
    try {
      await download(`/log-files/${file.id}/content`, file.fileName);
    } catch (e) {
      notifications.show({ color: 'red', title: 'İndirilemedi', message: errorMessage(e) });
    } finally {
      setDownloading(false);
    }
  };

  const cancellable = ['Uploaded', 'Validating', 'Queued', 'Processing'].includes(file.status);
  const retryable = file.status === 'Failed' || file.status === 'Cancelled';

  return (
    <Stack>
      <Card withBorder>
        <Group justify="space-between" align="flex-start">
          <Stack gap={4}>
            <Group gap="xs">
              <Title order={5}>{file.fileName}</Title>
              <StatusBadge status={file.status} failureCode={file.failureCode} />
            </Group>
            <Text size="sm" c="dimmed">
              {formatLabels[file.format]} · {formatBytes(file.sizeBytes)} · yüklendi {formatDateTime(file.uploadedAt)} · durum değişimi{' '}
              {formatDateTime(file.statusChangedAt)}
            </Text>
            <Text size="xs" c="dimmed" ff="monospace">
              SHA-256 {file.sha256}
            </Text>
          </Stack>
          <Group gap="xs">
            <Button size="xs" variant="default" leftSection={<IconDownload size={14} />} onClick={save} loading={downloading}>
              Orijinal dosyayı indir
            </Button>
            {can('Engineer') && cancellable && (
              <Button size="xs" color="red" variant="light" leftSection={<IconPlayerStop size={14} />} onClick={() => run('cancel')} loading={action.isPending}>
                İptal et
              </Button>
            )}
            {can('Engineer') && retryable && (
              <Button size="xs" variant="light" leftSection={<IconRefresh size={14} />} onClick={() => run('retry')} loading={action.isPending}>
                Yeniden işle
              </Button>
            )}
          </Group>
        </Group>
        {file.failureMessage && (
          <Alert mt="sm" color={file.status === 'Failed' ? 'red' : 'gray'} title={file.failureCode ?? undefined}>
            {file.failureMessage}
          </Alert>
        )}
        {isActive(file.status) && (
          <Text size="xs" c="dimmed" mt="sm">
            Durum değiştikçe bu sayfa kendiliğinden güncellenir.
          </Text>
        )}
      </Card>

      <Title order={5}>İşleme geçmişi</Title>
      {jobs.isPending ? (
        <LoadingState />
      ) : jobs.isError ? (
        <ErrorState error={jobs.error} onRetry={() => jobs.refetch()} />
      ) : jobs.data.length === 0 ? (
        <Text size="sm" c="dimmed">
          Henüz işleme denemesi yok.
        </Text>
      ) : (
        <Table.ScrollContainer minWidth={760}>
          <Table striped fz="xs">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>#</Table.Th>
                <Table.Th>Sonuç</Table.Th>
                <Table.Th>Başlangıç</Table.Th>
                <Table.Th>Süre</Table.Th>
                <Table.Th>Parser</Table.Th>
                <Table.Th>Mesaj / kayıt</Table.Th>
                <Table.Th>Sinyal / olay / parametre</Table.Th>
                <Table.Th>Notlar</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {jobs.data.map((j) => (
                <Table.Tr key={j.id}>
                  <Table.Td>{j.attempt}</Table.Td>
                  <Table.Td>
                    <Badge size="xs" variant="light" color={outcomeColors[j.outcome]}>
                      {outcomeLabels[j.outcome]}
                    </Badge>
                  </Table.Td>
                  <Table.Td>{formatDateTime(j.startedAt)}</Table.Td>
                  <Table.Td>{j.durationMs != null ? `${j.durationMs} ms` : '—'}</Table.Td>
                  <Table.Td>{j.parserName ? `${j.parserName} ${j.parserVersion ?? ''}` : '—'}</Table.Td>
                  <Table.Td>
                    {j.messageCount} / {j.recordCount}
                  </Table.Td>
                  <Table.Td>
                    {j.signalCount} / {j.eventCount} / {j.parameterCount}
                  </Table.Td>
                  <Table.Td>
                    <Stack gap={2}>
                      {j.truncated && <Text size="xs" c="yellow">Log kesik bitiyor</Text>}
                      {j.corrupt && <Text size="xs" c="yellow">Bozuk bölümler atlandı ({formatBytes(j.skippedBytes)})</Text>}
                      {j.failureMessage && <Text size="xs" c="red">{j.failureMessage}</Text>}
                      {j.warnings.slice(0, 3).map((w) => (
                        <Text key={w} size="xs" c="dimmed">
                          {w}
                        </Text>
                      ))}
                      {j.warnings.length > 3 && <Text size="xs" c="dimmed">+{j.warnings.length - 3} uyarı</Text>}
                    </Stack>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </Stack>
  );
}
