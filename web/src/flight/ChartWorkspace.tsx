import { ActionIcon, Alert, Badge, Card, Checkbox, Group, Loader, MultiSelect, Stack, Text, Tooltip } from '@mantine/core';
import { IconArrowLeft, IconArrowRight, IconZoomIn, IconZoomOut, IconZoomReset } from '@tabler/icons-react';
import { useMemo } from 'react';
import { useSignalData } from '../api/queries';
import type { Finding, FlightEvent, FlightEventKind, Signal } from '../api/types';
import { ErrorState } from '../components/States';
import { eventKindColors, eventKindLabels, severityColors } from '../lib/labels';
import { mantineColor, seriesColors } from './palette';
import { TimeSeriesChart, type ChartBand, type ChartMarker, type ChartSeries } from './TimeSeriesChart';
import { pan, zoom, type TimeWindow } from './view';

export const MAX_SIGNALS = 8;
const MAX_POINTS = 2000;

export interface Selection {
  /** Event or finding the user picked; drawn emphasized. */
  eventUs?: number;
  findingId?: string;
}

interface Props {
  flightId: string;
  logFromUs: number;
  logToUs: number;
  catalog: Signal[];
  selected: string[];
  onSelectedChange: (names: string[]) => void;
  window: TimeWindow | null;
  onWindowChange: (w: TimeWindow | null) => void;
  events: FlightEvent[];
  findings: Finding[];
  markerKinds: FlightEventKind[];
  onMarkerKindsChange: (kinds: FlightEventKind[]) => void;
  showFindings: boolean;
  onShowFindingsChange: (show: boolean) => void;
  selection: Selection;
  onPickTime?: (us: number) => void;
}

const MARKER_KINDS: FlightEventKind[] = ['ModeChange', 'Armed', 'Disarmed', 'Failsafe', 'Error', 'Dropout', 'Event', 'Message'];

/** Signal picker and the charts: one chart per unit, all on the same time axis and zoomed together. */
export function ChartWorkspace(props: Props) {
  const { flightId, logFromUs, logToUs, catalog, selected, window, onWindowChange } = props;
  const data = useSignalData(flightId, selected, { fromUs: window?.fromUs, toUs: window?.toUs }, MAX_POINTS);

  const toS = (us: number) => (us - logFromUs) / 1e6;
  const xRange: [number, number] = [toS(window?.fromUs ?? logFromUs), toS(window?.toUs ?? logToUs)];

  const options = useMemo(() => {
    const normalized = catalog.filter((s) => s.kind === 'Normalized');
    const raw = catalog.filter((s) => s.kind === 'Raw');
    const item = (s: Signal) => ({ value: s.name, label: s.unit ? `${s.name} [${s.unit}]` : s.name });
    return [
      { group: 'Normalize sinyaller', items: normalized.map(item) },
      { group: 'Ham log alanları', items: raw.map(item) },
    ].filter((g) => g.items.length > 0);
  }, [catalog]);

  const groups = useMemo(() => {
    if (!data.data) {
      return [];
    }

    const colorOf = new Map(selected.map((name, i) => [name, seriesColors[i % seriesColors.length]!]));
    const byUnit = new Map<string, ChartSeries[]>();
    for (const s of data.data.signals) {
      const key = s.unit ?? '';
      const list = byUnit.get(key) ?? [];
      list.push({ name: s.name, color: colorOf.get(s.name) ?? seriesColors[0]!, t: s.timestampsUs.map((t) => (t - logFromUs) / 1e6), v: s.values });
      byUnit.set(key, list);
    }

    return [...byUnit.entries()].map(([unit, series]) => ({ unit: unit || null, series }));
  }, [data.data, selected, logFromUs]);

  const markers = useMemo<ChartMarker[]>(
    () =>
      props.events
        .filter((e) => props.markerKinds.includes(e.kind))
        .map((e) => ({
          t: (e.timestampUs - logFromUs) / 1e6,
          color: mantineColor(eventKindColors[e.kind]),
          label: e.text,
          emphasized: e.timestampUs === props.selection.eventUs,
        })),
    [props.events, props.markerKinds, props.selection.eventUs, logFromUs],
  );

  const bands = useMemo<ChartBand[]>(
    () =>
      props.showFindings
        ? props.findings
            .filter((f) => f.severity !== 'Info' || f.id === props.selection.findingId)
            .map((f) => ({
              from: (f.startUs - logFromUs) / 1e6,
              to: (f.endUs - logFromUs) / 1e6,
              color: mantineColor(f.id === props.selection.findingId ? 'blue' : severityColors[f.severity]),
              label: f.title,
            }))
        : [],
    [props.findings, props.showFindings, props.selection.findingId, logFromUs],
  );

  const downsampled = data.data?.signals.some((s) => s.downsampled);
  const current = window ?? { fromUs: logFromUs, toUs: logToUs };

  return (
    <Stack gap="sm">
      <MultiSelect
        label="Sinyaller"
        description={`En fazla ${MAX_SIGNALS} sinyal. Aynı birimdeki sinyaller aynı grafikte gösterilir.`}
        placeholder={selected.length === 0 ? 'Sinyal seçin' : undefined}
        data={options}
        value={selected}
        onChange={(v) => props.onSelectedChange(v.slice(0, MAX_SIGNALS))}
        maxValues={MAX_SIGNALS}
        searchable
        clearable
        limit={150}
        nothingFoundMessage="Sinyal bulunamadı"
      />
      <Group justify="space-between" gap="xs">
        <Group gap={4}>
          <Tooltip label="Yakınlaştır">
            <ActionIcon variant="default" aria-label="Yakınlaştır" onClick={() => onWindowChange(zoom(current, 0.5, logFromUs, logToUs))}>
              <IconZoomIn size={16} />
            </ActionIcon>
          </Tooltip>
          <Tooltip label="Uzaklaştır">
            <ActionIcon variant="default" aria-label="Uzaklaştır" disabled={!window} onClick={() => onWindowChange(zoom(current, 2, logFromUs, logToUs))}>
              <IconZoomOut size={16} />
            </ActionIcon>
          </Tooltip>
          <Tooltip label="Geri kaydır">
            <ActionIcon variant="default" aria-label="Geri kaydır" disabled={!window} onClick={() => onWindowChange(pan(current, -0.5, logFromUs, logToUs))}>
              <IconArrowLeft size={16} />
            </ActionIcon>
          </Tooltip>
          <Tooltip label="İleri kaydır">
            <ActionIcon variant="default" aria-label="İleri kaydır" disabled={!window} onClick={() => onWindowChange(pan(current, 0.5, logFromUs, logToUs))}>
              <IconArrowRight size={16} />
            </ActionIcon>
          </Tooltip>
          <Tooltip label="Tüm log">
            <ActionIcon variant="default" aria-label="Tüm logu göster" disabled={!window} onClick={() => onWindowChange(null)}>
              <IconZoomReset size={16} />
            </ActionIcon>
          </Tooltip>
          <Text size="xs" c="dimmed" ml="xs">
            {xRange[0].toFixed(1)} – {xRange[1].toFixed(1)} s · seçmek için grafikte sürükleyin
          </Text>
          {data.isFetching && <Loader size="xs" />}
        </Group>
        <Group gap="xs">
          {downsampled && (
            <Tooltip label="Bu aralıkta örnek sayısı ekran çözünürlüğünden fazla: her dilimin en küçük ve en büyük değeri gösteriliyor. Yakınlaştırınca tam çözünürlüğe geçilir.">
              <Badge variant="light" color="gray">
                Azaltılmış çözünürlük
              </Badge>
            </Tooltip>
          )}
        </Group>
      </Group>
      <Group gap="md">
        <Text size="xs" fw={600}>
          İşaretler:
        </Text>
        {MARKER_KINDS.map((k) => (
          <Checkbox
            key={k}
            size="xs"
            label={eventKindLabels[k]}
            color={eventKindColors[k]}
            checked={props.markerKinds.includes(k)}
            onChange={(e) => props.onMarkerKindsChange(e.currentTarget.checked ? [...props.markerKinds, k] : props.markerKinds.filter((x) => x !== k))}
          />
        ))}
        <Checkbox size="xs" label="Bulgu aralıkları" checked={props.showFindings} onChange={(e) => props.onShowFindingsChange(e.currentTarget.checked)} />
      </Group>
      {selected.length === 0 ? (
        <Alert color="gray">İncelemek için sinyal seçin.</Alert>
      ) : data.isError ? (
        <ErrorState error={data.error} onRetry={() => data.refetch()} />
      ) : !data.data ? (
        <Card withBorder h={240}>
          <Loader m="auto" />
        </Card>
      ) : (
        groups.map((g) => (
          <Card key={g.unit ?? '-'} withBorder padding="xs">
            <TimeSeriesChart
              series={g.series}
              unit={g.unit}
              xRange={xRange}
              markers={markers}
              bands={bands}
              syncKey={`flight-${flightId}`}
              height={groups.length > 2 ? 200 : 260}
              onZoom={(from, to) => {
                if (to - from > 0.001) {
                  onWindowChange({ fromUs: Math.round(logFromUs + from * 1e6), toUs: Math.round(logFromUs + to * 1e6) });
                }
              }}
              onPick={props.onPickTime ? (t) => props.onPickTime!(Math.round(logFromUs + t * 1e6)) : undefined}
            />
          </Card>
        ))
      )}
      {data.data?.signals.some((s) => s.samplesInRange === 0) && (
        <Text size="xs" c="dimmed">
          Bu aralıkta örneği olmayan sinyaller: {data.data.signals.filter((s) => s.samplesInRange === 0).map((s) => s.name).join(', ')}
        </Text>
      )}
    </Stack>
  );
}
