import { Alert, Anchor, Badge, Button, Card, Group, List, SegmentedControl, Stack, Table, Text, Title, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertTriangle, IconDownload } from '@tabler/icons-react';
import { useState, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router';
import { download, errorMessage } from '../api/client';
import { repeatedQuery, useComparison } from '../api/queries';
import type { ComparedFlight, ComparedSignal, FlightComparison } from '../api/types';
import { PageHeader } from '../components/PageHeader';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import { formatDateTime, formatDuration, formatNumber } from '../lib/format';
import { autopilotLabels, eventKindLabels, severityLabels } from '../lib/labels';

type Stat = 'mean' | 'p50' | 'p95' | 'min' | 'max';
const STAT_LABELS: Record<Stat, string> = { mean: 'Ortalama', p50: 'Medyan', p95: 'P95', min: 'En düşük', max: 'En yüksek' };

const signalLabels: Record<string, string> = {
  'attitude.roll': 'Roll',
  'attitude.pitch': 'Pitch',
  'gps.satellites': 'GPS uydu sayısı',
  'gps.hdop': 'GPS HDOP',
  'gps.ground_speed': 'Yer hızı',
  'gps.alt_msl': 'Deniz seviyesinden irtifa',
  'battery.voltage': 'Batarya voltajı',
  'battery.current': 'Batarya akımı',
  'battery.remaining': 'Kalan batarya',
};

const ruleStatusLabels = { Applied: 'Uygulandı', NotApplicable: 'Uygulanamadı', Failed: 'Hata' } as const;
const assessmentLabels: Record<string, string> = {
  NoIssuesFound: 'Sorun bulunmadı',
  MinorIssues: 'Küçük sorunlar',
  SignificantIssues: 'Önemli sorunlar',
  CriticalIssues: 'Kritik sorunlar',
  Inconclusive: 'Belirsiz',
};

/** Ids from ?ids=a,b,c (or repeated ids), de-duplicated, at most 6. */
function parseIds(params: URLSearchParams): string[] {
  const ids = params.getAll('ids').flatMap((v) => v.split(',')).map((v) => v.trim()).filter(Boolean);
  return [...new Set(ids)].slice(0, 6);
}

/**
 * Side-by-side comparison of 2 to 6 flights. Values come from the API as computed from stored data; notes say what
 * cannot be compared one to one (different autopilots, firmware, units, sampling rates, missing data).
 */
export function ComparePage() {
  const [params] = useSearchParams();
  const ids = parseIds(params);
  const comparison = useComparison(ids);
  const [busy, setBusy] = useState(false);

  const exportAs = async (format: 'Csv' | 'Json') => {
    setBusy(true);
    try {
      await download(`/flights/compare/export${repeatedQuery('ids', ids, { format })}`, `karsilastirma.${format.toLowerCase()}`);
    } catch (e) {
      notifications.show({ color: 'red', title: 'Dışa aktarılamadı', message: errorMessage(e) });
    } finally {
      setBusy(false);
    }
  };

  if (ids.length < 2) {
    return (
      <EmptyState title="Karşılaştırılacak uçuş seçilmedi" action={<Button component={Link} to="/flights">Uçuş geçmişine git</Button>}>
        Uçuş geçmişinde 2 ile 6 arasında uçuş seçip "Karşılaştır"a basın.
      </EmptyState>
    );
  }

  return (
    <>
      <PageHeader
        title="Uçuş karşılaştırma"
        description={comparison.data ? `${comparison.data.flights.length} uçuş · ${formatDateTime(comparison.data.generatedAt)} itibarıyla hesaplandı` : undefined}
        actions={
          <Group gap="xs">
            <Button size="xs" variant="default" leftSection={<IconDownload size={14} />} loading={busy} onClick={() => exportAs('Csv')} disabled={!comparison.data}>
              CSV
            </Button>
            <Button size="xs" variant="default" leftSection={<IconDownload size={14} />} loading={busy} onClick={() => exportAs('Json')} disabled={!comparison.data}>
              JSON
            </Button>
          </Group>
        }
      />
      {comparison.isPending ? (
        <LoadingState label="Uçuşlar karşılaştırılıyor…" />
      ) : comparison.isError ? (
        <ErrorState error={comparison.error} onRetry={() => comparison.refetch()} />
      ) : (
        <ComparisonView comparison={comparison.data} />
      )}
    </>
  );
}

export function ComparisonView({ comparison: c }: { comparison: FlightComparison }) {
  const [stat, setStat] = useState<Stat>('mean');
  const name = (id: string) => c.flights.find((f) => f.id === id)?.name ?? id;
  const categories = [...new Set(c.flights.flatMap((f) => Object.keys(f.findingsByCategory)))].sort();
  const eventKinds = [...new Set(c.flights.flatMap((f) => Object.keys(f.eventCounts)))];
  const modes = [...new Set(c.flights.flatMap((f) => f.modes.map((m) => m.mode)))];

  return (
    <Stack gap="md">
      {c.notes.length > 0 && (
        <Alert color="yellow" icon={<IconAlertTriangle size={18} />} title="Doğrudan karşılaştırılamayan noktalar" data-testid="comparison-notes">
          <List size="sm" spacing={4}>
            {c.notes.map((n) => (
              <List.Item key={n.code}>
                {n.message}
                {n.flightIds.length > 0 && <Text span c="dimmed"> ({n.flightIds.map(name).join(', ')})</Text>}
              </List.Item>
            ))}
          </List>
        </Alert>
      )}

      <Section title="Uçuşlar">
        <CompareTable flights={c.flights}>
          <Row label="Proje" flights={c.flights} value={(f) => f.projectName} />
          <Row label="Araç" flights={c.flights} value={(f) => f.aircraftName ?? '—'} />
          <Row label="Otopilot" flights={c.flights} value={(f) => `${autopilotLabels[f.autopilot]} ${f.firmwareVersion ?? ''}`} />
          <Row label="Araç tipi" flights={c.flights} value={(f) => f.vehicleType ?? '—'} />
          <Row label="Tarih" flights={c.flights} value={(f) => formatDateTime(f.startedAt)} />
          <Row label="Log süresi" flights={c.flights} value={(f) => formatDuration(f.durationSeconds)} />
          <Row
            label="Analiz edilen süre"
            hint="Arm edilmiş süreler; arm bilgisi yoksa logun tamamı."
            flights={c.flights}
            value={(f) => `${formatDuration(f.analyzedSeconds)}${f.armingKnown ? '' : ' (arm bilgisi yok)'}`}
          />
        </CompareTable>
      </Section>

      <Section title="Bulgular ve kurallar">
        <CompareTable flights={c.flights}>
          {(['CriticalCount', 'WarningCount', 'InfoCount'] as const).map((k) => {
            const severity = k.replace('Count', '') as 'Critical' | 'Warning' | 'Info';
            const key = (k.charAt(0).toLowerCase() + k.slice(1)) as 'criticalCount' | 'warningCount' | 'infoCount';
            return <Row key={k} label={severityLabels[severity]} flights={c.flights} value={(f) => (f[key] == null ? 'analiz yok' : String(f[key]))} />;
          })}
          {categories.map((cat) => (
            <Row key={cat} label={`Kategori: ${cat}`} flights={c.flights} value={(f) => String(f.findingsByCategory[cat] ?? 0)} />
          ))}
          {c.rules.map((r) => (
            <Table.Tr key={r.ruleId}>
              <Table.Td>
                <Text size="xs" ff="monospace">
                  {r.ruleId}
                </Text>
              </Table.Td>
              {r.values.map((v, i) => (
                <Table.Td key={c.flights[i]!.id}>
                  {v == null ? (
                    <Text size="xs" c="dimmed">—</Text>
                  ) : (
                    <Group gap={4}>
                      <Text size="xs">{ruleStatusLabels[v.status]}</Text>
                      {v.findings > 0 && (
                        <Badge size="xs" variant="light" color={v.highestSeverity === 'Critical' ? 'red' : v.highestSeverity === 'Warning' ? 'yellow' : 'blue'}>
                          {v.findings} bulgu
                        </Badge>
                      )}
                    </Group>
                  )}
                </Table.Td>
              ))}
            </Table.Tr>
          ))}
        </CompareTable>
      </Section>

      <Section
        title="Sinyaller"
        actions={
          <SegmentedControl size="xs" value={stat} onChange={(v) => setStat(v as Stat)} data={Object.entries(STAT_LABELS).map(([value, label]) => ({ value, label }))} />
        }
      >
        <CompareTable flights={c.flights}>
          {c.signals.map((s) => (
            <SignalRow key={s.name} signal={s} stat={stat} flights={c.flights} />
          ))}
        </CompareTable>
      </Section>

      <Section title="Batarya, titreşim, olaylar ve modlar">
        <CompareTable flights={c.flights}>
          <Row label="Başlangıç voltajı (V)" flights={c.flights} value={(f) => formatNumber(f.battery?.startVoltage, 2)} />
          <Row label="Bitiş voltajı (V)" flights={c.flights} value={(f) => formatNumber(f.battery?.endVoltage, 2)} />
          <Row label="En düşük voltaj (V)" flights={c.flights} value={(f) => formatNumber(f.battery?.minVoltage, 2)} />
          <Row label="Ortalama akım (A)" flights={c.flights} value={(f) => formatNumber(f.battery?.meanCurrent, 2)} />
          <Row label="Kullanılan batarya (%)" flights={c.flights} value={(f) => formatNumber(f.battery?.usedPercent, 1)} />
          <Row
            label="Titreşim (ortalama / P95 / en yüksek)"
            hint="ArduPilot VIBE ve PX4 accel_vibration_metric farklı büyüklüklerdir; yalnızca aynı otopilotun uçuşları arasında karşılaştırılabilir."
            flights={c.flights}
            value={(f) =>
              f.vibration
                ? `${formatNumber(f.vibration.mean, 2)} / ${formatNumber(f.vibration.p95, 2)} / ${formatNumber(f.vibration.max, 2)} ${f.vibration.unit ?? ''} (${autopilotLabels[f.vibration.comparableWithin]})`
                : '—'
            }
          />
          {eventKinds.map((k) => (
            <Row key={k} label={`Olay: ${eventKindLabels[k as keyof typeof eventKindLabels] ?? k}`} flights={c.flights} value={(f) => String(f.eventCounts[k] ?? 0)} />
          ))}
          {modes.map((m) => (
            <Row
              key={m}
              label={`Mod: ${m}`}
              flights={c.flights}
              value={(f) => {
                const share = f.modes.find((x) => x.mode === m);
                return share ? `${formatDuration(share.seconds)} (%${formatNumber(share.share * 100, 0)})` : '—';
              }}
            />
          ))}
          <Row
            label="Son AI raporu"
            flights={c.flights}
            value={(f) => (f.latestAiReport ? `${assessmentLabels[f.latestAiReport.assessment ?? ''] ?? '—'} (${f.latestAiReport.model})` : '—')}
          />
        </CompareTable>
      </Section>

      <Card withBorder padding="sm">
        <Text size="sm" fw={500} mb={4}>
          Sınırlamalar
        </Text>
        <List size="xs" spacing={2}>
          {c.limitations.map((l) => (
            <List.Item key={l}>{l}</List.Item>
          ))}
        </List>
        <Text size="xs" c="dimmed" mt="xs">
          Hesaplama sürümü: {c.version}
        </Text>
      </Card>
    </Stack>
  );
}

function Section({ title, actions, children }: { title: string; actions?: ReactNode; children: ReactNode }) {
  return (
    <Card withBorder padding="sm">
      <Group justify="space-between" mb="xs">
        <Title order={5}>{title}</Title>
        {actions}
      </Group>
      {children}
    </Card>
  );
}

function CompareTable({ flights, children }: { flights: ComparedFlight[]; children: ReactNode }) {
  return (
    <Table.ScrollContainer minWidth={240 + flights.length * 170}>
      <Table fz="xs" verticalSpacing={4} striped withColumnBorders>
        <Table.Thead>
          <Table.Tr>
            <Table.Th w={240} />
            {flights.map((f) => (
              <Table.Th key={f.id}>
                <Anchor component={Link} to={`/flights/${f.id}`} size="sm" fw={600}>
                  {f.name}
                </Anchor>
              </Table.Th>
            ))}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>{children}</Table.Tbody>
      </Table>
    </Table.ScrollContainer>
  );
}

function Row({ label, hint, flights, value }: { label: string; hint?: string; flights: ComparedFlight[]; value: (f: ComparedFlight) => string }) {
  return (
    <Table.Tr>
      <Table.Td>{hint ? <Tooltip label={hint} multiline w={300}><Text size="xs" td="underline dotted">{label}</Text></Tooltip> : label}</Table.Td>
      {flights.map((f) => (
        <Table.Td key={f.id}>{value(f)}</Table.Td>
      ))}
    </Table.Tr>
  );
}

function SignalRow({ signal: s, stat, flights }: { signal: ComparedSignal; stat: Stat; flights: ComparedFlight[] }) {
  const label = `${signalLabels[s.name] ?? s.name}${s.unit ? ` (${s.unit})` : ''}`;
  return (
    <Table.Tr data-testid="signal-row">
      <Table.Td>
        <Group gap={4} wrap="nowrap">
          <Text size="xs">{label}</Text>
          {s.note && (
            <Tooltip label={s.note} multiline w={320}>
              <IconAlertTriangle size={14} color={s.comparable ? 'var(--mantine-color-yellow-6)' : 'var(--mantine-color-red-6)'} aria-label={s.note} />
            </Tooltip>
          )}
        </Group>
      </Table.Td>
      {s.values.map((v, i) => (
        <Table.Td key={flights[i]!.id}>
          {v == null ? (
            <Text size="xs" c="dimmed">veri yok</Text>
          ) : (
            <Tooltip label={`${v.samples} örnek · ${formatNumber(v.rateHz, 1)} Hz`}>
              <Text size="xs">{formatNumber(v[stat], 3)}</Text>
            </Tooltip>
          )}
        </Table.Td>
      ))}
    </Table.Tr>
  );
}
