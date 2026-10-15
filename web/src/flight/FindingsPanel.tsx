import { Accordion, Alert, Badge, Button, Group, Select, Stack, Table, Text, Tooltip } from '@mantine/core';
import { IconChartLine, IconRefresh } from '@tabler/icons-react';
import { notifications } from '@mantine/notifications';
import { useMemo, useState } from 'react';
import { errorMessage } from '../api/client';
import { useAnalysisRun, useRequestAnalysis, useRules } from '../api/queries';
import type { AnalysisRun, Finding, FindingSeverity } from '../api/types';
import { useTeam } from '../auth/TeamContext';
import { SeverityBadge } from '../components/StatusBadge';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import { formatDateTime, formatLogTime, formatNumber } from '../lib/format';
import { confidenceLabels, severityLabels } from '../lib/labels';

const runStatusLabels: Record<AnalysisRun['status'], string> = { Queued: 'Kuyrukta', Running: 'Çalışıyor', Succeeded: 'Tamamlandı', Failed: 'Başarısız' };
const runStatusColors: Record<AnalysisRun['status'], string> = { Queued: 'blue', Running: 'cyan', Succeeded: 'green', Failed: 'red' };
const ruleStatusLabels = { Applied: 'Uygulandı', NotApplicable: 'Uygulanamadı', Failed: 'Hata' } as const;
const ruleStatusColors = { Applied: 'green', NotApplicable: 'gray', Failed: 'red' } as const;
const SEVERITY_ORDER: FindingSeverity[] = ['Critical', 'Warning', 'Info'];

function Values({ values }: { values: Record<string, number> }) {
  const entries = Object.entries(values);
  if (entries.length === 0) {
    return <Text span c="dimmed">—</Text>;
  }

  return (
    <>
      {entries.map(([k, v]) => (
        <Text key={k} size="xs" ff="monospace">
          {k} = {formatNumber(v)}
        </Text>
      ))}
    </>
  );
}

/**
 * Findings of the latest analysis run: every value comes from the API as the rule computed it (measured value,
 * threshold and where the threshold came from); nothing is recomputed here.
 */
export function FindingsPanel({
  flightId,
  runs,
  findings,
  findingsLoading,
  logFromUs,
  selectedId,
  onSelect,
  onShowOnChart,
}: {
  flightId: string;
  runs: AnalysisRun[];
  findings: Finding[];
  findingsLoading: boolean;
  logFromUs: number;
  selectedId?: string;
  onSelect: (finding: Finding) => void;
  onShowOnChart: (finding: Finding) => void;
}) {
  const { can } = useTeam();
  const latest = runs[0];
  const lastSucceeded = runs.find((r) => r.status === 'Succeeded');
  const detail = useAnalysisRun(lastSucceeded?.id);
  const rules = useRules();
  const request = useRequestAnalysis(flightId);
  const [severity, setSeverity] = useState<FindingSeverity | null>(null);

  const sorted = useMemo(
    () =>
      findings
        .filter((f) => !severity || f.severity === severity)
        .toSorted((a, b) => SEVERITY_ORDER.indexOf(a.severity) - SEVERITY_ORDER.indexOf(b.severity) || a.startUs - b.startUs),
    [findings, severity],
  );

  const ruleName = (id: string) => rules.data?.find((r) => r.id === id)?.title ?? id;
  const pending = latest && (latest.status === 'Queued' || latest.status === 'Running');

  const rerun = () =>
    request.mutate(undefined, {
      onSuccess: () => notifications.show({ color: 'green', message: 'Analiz kuyruğa alındı.' }),
      onError: (e) => notifications.show({ color: 'red', title: 'Analiz istenemedi', message: errorMessage(e) }),
    });

  return (
    <Stack gap="sm">
      <Group justify="space-between" align="flex-start">
        <Stack gap={2}>
          {latest ? (
            <Group gap="xs">
              <Badge color={runStatusColors[latest.status]} variant="light">
                {runStatusLabels[latest.status]}
              </Badge>
              <Text size="xs" c="dimmed">
                Analiz #{latest.attempt} · motor {latest.engineVersion} · {latest.trigger === 'Manual' ? 'elle istendi' : 'otomatik'} ·{' '}
                {formatDateTime(latest.finishedAt ?? latest.createdAt)}
              </Text>
            </Group>
          ) : (
            <Text size="sm" c="dimmed">
              Bu uçuş henüz analiz edilmedi.
            </Text>
          )}
          {lastSucceeded && (
            <Text size="xs" c="dimmed">
              {lastSucceeded.rulesApplied} kural uygulandı, {lastSucceeded.rulesNotApplicable} kural uygulanamadı
              {lastSucceeded.rulesFailed > 0 ? `, ${lastSucceeded.rulesFailed} kural hata verdi` : ''}.
            </Text>
          )}
        </Stack>
        {can('Engineer') && (
          <Button size="xs" variant="light" leftSection={<IconRefresh size={14} />} onClick={rerun} loading={request.isPending} disabled={!!pending}>
            Yeniden analiz et
          </Button>
        )}
      </Group>

      {latest?.status === 'Failed' && (
        <Alert color="red" title="Son analiz başarısız">
          {latest.failureMessage ?? latest.failureCode}
          {lastSucceeded && lastSucceeded.id !== latest.id && ' Aşağıda son başarılı analizin bulguları gösteriliyor.'}
        </Alert>
      )}

      {!lastSucceeded ? (
        pending ? (
          <LoadingState label="Analiz sürüyor…" />
        ) : (
          <EmptyState title="Bulgu yok">Analiz tamamlandığında bulgular burada listelenir.</EmptyState>
        )
      ) : findingsLoading ? (
        <LoadingState />
      ) : (
        <>
          <Group gap="xs">
            <Select
              size="xs"
              aria-label="Önem filtresi"
              placeholder="Tüm önem seviyeleri"
              clearable
              data={SEVERITY_ORDER.map((s) => ({ value: s, label: severityLabels[s] }))}
              value={severity}
              onChange={(v) => setSeverity(v as FindingSeverity | null)}
            />
            <Text size="xs" c="dimmed">
              {sorted.length} bulgu
            </Text>
          </Group>
          {sorted.length === 0 ? (
            <EmptyState title="Bulgu yok">
              {findings.length === 0 ? 'Kurallar bu uçuşta eşik aşımı bulmadı.' : 'Filtreye uyan bulgu yok.'}
            </EmptyState>
          ) : (
            <Accordion
              variant="separated"
              value={selectedId ?? null}
              onChange={(id) => {
                const f = findings.find((x) => x.id === id);
                if (f) {
                  onSelect(f);
                }
              }}
            >
              {sorted.map((f) => (
                <Accordion.Item key={f.id} value={f.id} data-testid="finding">
                  <Accordion.Control>
                    <Group gap="xs" wrap="nowrap">
                      <SeverityBadge severity={f.severity} />
                      <Stack gap={0} style={{ minWidth: 0 }}>
                        <Text size="sm" fw={500} lineClamp={1}>
                          {f.title}
                        </Text>
                        <Text size="xs" c="dimmed">
                          {formatLogTime(f.startUs, logFromUs)} – {formatLogTime(f.endUs, logFromUs)}
                          {f.occurrenceCount > 1 ? ` · ${f.occurrenceCount} kez` : ''}
                        </Text>
                      </Stack>
                    </Group>
                  </Accordion.Control>
                  <Accordion.Panel>
                    <Stack gap="xs">
                      <Text size="sm">{f.description}</Text>
                      <Table withRowBorders={false} verticalSpacing={2} fz="xs">
                        <Table.Tbody>
                          <Table.Tr>
                            <Table.Th w={130}>Kural</Table.Th>
                            <Table.Td>
                              {ruleName(f.ruleId)} <Text span c="dimmed">({f.ruleId} v{f.ruleVersion})</Text>
                            </Table.Td>
                          </Table.Tr>
                          <Table.Tr>
                            <Table.Th>Ölçülen</Table.Th>
                            <Table.Td>
                              <Values values={f.measured} />
                            </Table.Td>
                          </Table.Tr>
                          <Table.Tr>
                            <Table.Th>Eşik</Table.Th>
                            <Table.Td>
                              <Values values={f.threshold} />
                            </Table.Td>
                          </Table.Tr>
                          <Table.Tr>
                            <Table.Th>Eşik kaynağı</Table.Th>
                            <Table.Td>{f.thresholdSource}</Table.Td>
                          </Table.Tr>
                          <Table.Tr>
                            <Table.Th>Güven</Table.Th>
                            <Table.Td>{confidenceLabels[f.confidence]}</Table.Td>
                          </Table.Tr>
                          {f.dataCoverage != null && (
                            <Table.Tr>
                              <Table.Th>Veri kapsamı</Table.Th>
                              <Table.Td>%{formatNumber(f.dataCoverage * 100, 1)}</Table.Td>
                            </Table.Tr>
                          )}
                          <Table.Tr>
                            <Table.Th>Sinyaller</Table.Th>
                            <Table.Td ff="monospace">{f.signals.join(', ') || '—'}</Table.Td>
                          </Table.Tr>
                        </Table.Tbody>
                      </Table>
                      {f.limitations && (
                        <Alert color="gray" p="xs">
                          <Text size="xs">Sınırlama: {f.limitations}</Text>
                        </Alert>
                      )}
                      <Group>
                        <Button size="xs" variant="light" leftSection={<IconChartLine size={14} />} onClick={() => onShowOnChart(f)}>
                          Grafikte göster
                        </Button>
                      </Group>
                    </Stack>
                  </Accordion.Panel>
                </Accordion.Item>
              ))}
            </Accordion>
          )}
        </>
      )}

      {lastSucceeded && (
        <Accordion variant="contained">
          <Accordion.Item value="rules">
            <Accordion.Control>
              <Text size="sm">Kural sonuçları</Text>
            </Accordion.Control>
            <Accordion.Panel>
              {detail.isPending ? (
                <LoadingState />
              ) : detail.isError ? (
                <ErrorState error={detail.error} onRetry={() => detail.refetch()} />
              ) : (
                <Table fz="xs" verticalSpacing={4}>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>Kural</Table.Th>
                      <Table.Th>Durum</Table.Th>
                      <Table.Th>Bulgu</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {(detail.data.rules ?? []).map((r) => (
                      <Table.Tr key={r.ruleId}>
                        <Table.Td>
                          <Tooltip label={`${r.ruleId} v${r.ruleVersion} · ${r.durationMs} ms`}>
                            <Text size="xs">{ruleName(r.ruleId)}</Text>
                          </Tooltip>
                        </Table.Td>
                        <Table.Td>
                          <Badge size="xs" variant="light" color={ruleStatusColors[r.status]}>
                            {ruleStatusLabels[r.status]}
                          </Badge>
                          {r.reason && (
                            <Text size="xs" c="dimmed">
                              {r.reason}
                            </Text>
                          )}
                        </Table.Td>
                        <Table.Td>{r.findingCount}</Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              )}
            </Accordion.Panel>
          </Accordion.Item>
        </Accordion>
      )}
    </Stack>
  );
}
