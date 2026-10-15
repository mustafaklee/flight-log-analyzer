import { Alert, Badge, Card, Group, SegmentedControl, SimpleGrid, Stack, Table, Text, Title } from '@mantine/core';
import { useState } from 'react';
import { useAiUsage } from '../api/queries';
import { ErrorState, LoadingState } from '../components/States';
import { aiFailureLabels } from '../flight/AiReportPanel';

const usd = (value: number) => `$${value.toFixed(4)}`;
const tokens = (value: number) => value.toLocaleString('tr-TR');

function Figure({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <Card withBorder>
      <Text size="xs" c="dimmed" tt="uppercase" fw={600}>
        {label}
      </Text>
      <Text size="lg" fw={600}>
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

/** AI usage for administrators: volume, tokens, estimated cost, model latency and failures (ADR-0008). */
export function AdminAiUsage() {
  const [days, setDays] = useState('30');
  const usage = useAiUsage(Number(days));

  return (
    <Stack>
      <SegmentedControl
        w="fit-content"
        value={days}
        onChange={setDays}
        data={[
          { value: '1', label: '24 saat' },
          { value: '7', label: '7 gün' },
          { value: '30', label: '30 gün' },
          { value: '90', label: '90 gün' },
        ]}
      />
      {usage.isPending ? (
        <LoadingState />
      ) : usage.isError ? (
        <ErrorState error={usage.error} onRetry={() => usage.refetch()} />
      ) : (
        <>
          <Alert color={usage.data.enabled ? 'green' : 'gray'} variant="light" title={usage.data.enabled ? 'AI analizi açık' : 'AI analizi kapalı'}>
            Sağlayıcı {usage.data.provider}, model {usage.data.model}, ekip başına günlük sınır{' '}
            {usage.data.dailyLimitPerTeam > 0 ? usage.data.dailyLimitPerTeam : 'yok'}. Maliyet, yapılandırmadaki fiyat listesinden hesaplanan tahmindir.
          </Alert>
          <SimpleGrid cols={{ base: 2, md: 4 }}>
            <Figure
              label="Analiz"
              value={String(usage.data.totals.analyses)}
              hint={`${usage.data.totals.succeeded} başarılı · ${usage.data.totals.failed} başarısız · ${usage.data.totals.pending} bekliyor`}
            />
            <Figure label="Tahmini maliyet" value={usd(usage.data.totals.costUsd)} />
            <Figure
              label="Token"
              value={tokens(usage.data.totals.inputTokens + usage.data.totals.outputTokens)}
              hint={`${tokens(usage.data.totals.inputTokens)} girdi · ${tokens(usage.data.totals.outputTokens)} çıktı · ${tokens(usage.data.totals.cacheReadTokens)} önbellekten`}
            />
            <Figure
              label="Model süresi (çağrı başına)"
              value={usage.data.totals.averageLatencyMs == null ? '—' : `${(usage.data.totals.averageLatencyMs / 1000).toFixed(1)} s`}
              hint={`${usage.data.totals.providerCalls} çağrı`}
            />
          </SimpleGrid>
          <SimpleGrid cols={{ base: 1, md: 2 }}>
            <Card withBorder>
              <Title order={5} mb="xs">
                Modele göre
              </Title>
              <Table fz="sm">
                <Table.Tbody>
                  {usage.data.byModel.map((m) => (
                    <Table.Tr key={`${m.provider}/${m.model}`}>
                      <Table.Td>
                        {m.provider} {m.model}
                      </Table.Td>
                      <Table.Td>{m.analyses} analiz</Table.Td>
                      <Table.Td>{usd(m.costUsd)}</Table.Td>
                      <Table.Td>{m.unpriced > 0 && <Badge color="yellow" variant="light">{m.unpriced} fiyatsız</Badge>}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Card>
            <Card withBorder>
              <Title order={5} mb="xs">
                Ekibe göre
              </Title>
              <Table fz="sm">
                <Table.Tbody>
                  {usage.data.byTeam.map((t) => (
                    <Table.Tr key={t.teamId}>
                      <Table.Td>{t.teamName}</Table.Td>
                      <Table.Td>{t.analyses} analiz</Table.Td>
                      <Table.Td>{usd(t.costUsd)}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Card>
            <Card withBorder>
              <Title order={5} mb="xs">
                Günlük
              </Title>
              <Table fz="sm">
                <Table.Tbody>
                  {usage.data.byDay.map((d) => (
                    <Table.Tr key={d.day}>
                      <Table.Td>{d.day}</Table.Td>
                      <Table.Td>
                        {d.analyses} analiz{d.failed > 0 ? ` (${d.failed} başarısız)` : ''}
                      </Table.Td>
                      <Table.Td>{tokens(d.inputTokens + d.outputTokens)} token</Table.Td>
                      <Table.Td>{usd(d.costUsd)}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Card>
            <Card withBorder>
              <Title order={5} mb="xs">
                Hatalar
              </Title>
              {usage.data.failures.length === 0 ? (
                <Text size="sm" c="dimmed">
                  Hata yok.
                </Text>
              ) : (
                <Table fz="sm">
                  <Table.Tbody>
                    {usage.data.failures.map((f) => (
                      <Table.Tr key={f.code}>
                        <Table.Td>
                          <code>{f.code}</code>
                        </Table.Td>
                        <Table.Td>{f.count}</Table.Td>
                        <Table.Td>
                          <Text size="xs" c="dimmed">
                            {aiFailureLabels[f.code] ?? ''}
                          </Text>
                        </Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              )}
            </Card>
          </SimpleGrid>
          {usage.data.totals.analyses === 0 && (
            <Group>
              <Text size="sm" c="dimmed">
                Bu dönemde AI analizi yapılmadı.
              </Text>
            </Group>
          )}
        </>
      )}
    </Stack>
  );
}
