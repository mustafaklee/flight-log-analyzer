import { Alert, Anchor, Badge, Card, Grid, Group, SegmentedControl, Stack, Tabs, Text } from '@mantine/core';
import { useEffect, useMemo, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router';
import { isActive, useAnalysisRuns, useEvents, useFindings, useFlight, useSignals } from '../api/queries';
import type { Finding, FlightEvent, FlightEventKind } from '../api/types';
import { PageHeader } from '../components/PageHeader';
import { SeverityBadge, StatusBadge } from '../components/StatusBadge';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import { ChartWorkspace, MAX_SIGNALS } from '../flight/ChartWorkspace';
import { EventsPanel } from '../flight/EventsPanel';
import { FindingsPanel } from '../flight/FindingsPanel';
import { FlightMap, GPS_SIGNALS } from '../flight/FlightMap';
import { FlightTimeline } from '../flight/FlightTimeline';
import { MetadataPanel } from '../flight/MetadataPanel';
import { ParametersPanel } from '../flight/ParametersPanel';
import { ProcessingPanel } from '../flight/ProcessingPanel';
import { SignalsPanel } from '../flight/SignalsPanel';
import { windowAround, type TimeWindow } from '../flight/view';
import { formatDateTime, formatDuration } from '../lib/format';
import { autopilotLabels } from '../lib/labels';

const TABS = ['analysis', 'findings', 'signals', 'parameters', 'map', 'processing', 'details'] as const;
type Tab = (typeof TABS)[number];
const DEFAULT_SIGNALS = ['attitude.roll', 'attitude.pitch', 'battery.voltage', 'gps.alt_msl'];
const DEFAULT_MARKERS: FlightEventKind[] = ['ModeChange', 'Armed', 'Disarmed', 'Failsafe', 'Error', 'Dropout'];

/**
 * One flight: charts with event markers and finding ranges, the timeline, events and findings side by side, and the
 * catalog, parameters, map, processing and metadata tabs. Tab, signals and the selected finding live in the URL so a
 * view can be shared and the dashboard can link to a finding.
 */
export function FlightDetailPage() {
  const { flightId = '' } = useParams();
  const [params, setParams] = useSearchParams();
  const flight = useFlight(flightId);
  const file = flight.data?.logFile;
  const hasData = !!file && !['Uploaded', 'Validating', 'Queued', 'Processing'].includes(file.status) && file.status !== 'Cancelled';
  const failedBeforeData = file?.status === 'Failed' && flight.data?.logStartTimestampUs == null;
  const dataReady = hasData && !failedBeforeData;

  const catalog = useSignals(flightId, dataReady);
  const events = useEvents(flightId, {}, dataReady);
  const runs = useAnalysisRuns(flightId);
  const lastSucceeded = runs.data?.find((r) => r.status === 'Succeeded');
  const findings = useFindings(lastSucceeded?.id);

  const [window, setWindow] = useState<TimeWindow | null>(null);
  const [markerKinds, setMarkerKinds] = useState<FlightEventKind[]>(DEFAULT_MARKERS);
  const [showFindings, setShowFindings] = useState(true);
  const [pickedUs, setPickedUs] = useState<number | undefined>();
  const [side, setSide] = useState<'events' | 'findings'>(params.get('finding') ? 'findings' : 'events');

  // Load every page of events in the background: markers, timeline and the list all use the full set.
  useEffect(() => {
    if (events.hasNextPage && !events.isFetchingNextPage && !events.isError) {
      void events.fetchNextPage();
    }
  }, [events]);

  const allEvents = useMemo<FlightEvent[]>(() => events.data?.pages.flatMap((p) => p.items) ?? [], [events.data]);
  const eventTotal = events.data?.pages[0]?.totalCount ?? 0;
  const findingList = useMemo(() => findings.data ?? [], [findings.data]);
  const names = useMemo(() => new Set((catalog.data ?? []).map((s) => s.name)), [catalog.data]);
  const hasGps = GPS_SIGNALS.every((n) => names.has(n)) && (catalog.data ?? []).some((s) => s.name === 'gps.lat' && s.sampleCount > 0);

  const tab: Tab = TABS.includes(params.get('tab') as Tab) ? (params.get('tab') as Tab) : 'analysis';
  const selectedFinding = params.get('finding') ?? undefined;
  const signalParam = params.get('signals');
  const selected = useMemo(
    () => (signalParam === null ? DEFAULT_SIGNALS.filter((n) => names.has(n)) : signalParam.split(',').filter((n) => n && names.has(n))).slice(0, MAX_SIGNALS),
    [signalParam, names],
  );

  const update = (changes: Record<string, string | null>) =>
    setParams(
      (prev) => {
        const next = new URLSearchParams(prev);
        for (const [k, v] of Object.entries(changes)) {
          if (v === null) {
            next.delete(k);
          } else {
            next.set(k, v);
          }
        }

        return next;
      },
      { replace: true },
    );

  if (flight.isPending) {
    return <LoadingState />;
  }

  if (flight.isError) {
    return <ErrorState error={flight.error} onRetry={() => flight.refetch()} />;
  }

  const f = flight.data;
  const logFromUs = f.logStartTimestampUs ?? 0;
  const logToUs = logFromUs + Math.round((f.durationSeconds ?? 0) * 1e6);

  const showFinding = (finding: Finding) => {
    const fromCatalog = finding.signals.filter((n) => names.has(n));
    update({ tab: 'analysis', finding: finding.id, ...(fromCatalog.length > 0 ? { signals: fromCatalog.slice(0, MAX_SIGNALS).join(',') } : {}) });
    setWindow(windowAround(finding.startUs, finding.endUs, logFromUs, logToUs));
    setSide('findings');
  };

  const selectEvent = (e: FlightEvent) => {
    setPickedUs(e.timestampUs);
    setWindow(windowAround(e.timestampUs, e.timestampUs, logFromUs, logToUs));
  };

  const findingsPanel = (
    <FindingsPanel
      flightId={f.id}
      runs={runs.data ?? []}
      findings={findingList}
      findingsLoading={findings.isPending && !!lastSucceeded}
      logFromUs={logFromUs}
      selectedId={selectedFinding}
      onSelect={(x) => update({ finding: x.id })}
      onShowOnChart={showFinding}
    />
  );

  return (
    <>
      <PageHeader
        title={f.name}
        description={
          <Group gap="xs">
            <Anchor component={Link} to={`/flights?projectId=${f.projectId}`} size="sm">
              {f.projectName}
            </Anchor>
            {f.aircraftId && (
              <Anchor component={Link} to={`/fleet/${f.aircraftId}`} size="sm">
                {f.aircraftName}
              </Anchor>
            )}
          </Group>
        }
        actions={<StatusBadge status={file?.status} failureCode={file?.failureCode} />}
      />

      <Card withBorder mb="md" padding="sm">
        <Group gap="xl">
          <Summary label="Tarih" value={formatDateTime(f.startedAt ?? f.occurredAt)} />
          <Summary label="Süre" value={formatDuration(f.durationSeconds)} />
          <Summary label="Otopilot" value={`${autopilotLabels[f.autopilot]}${f.firmwareVersion ? ` ${f.firmwareVersion}` : ''}`} />
          <Summary label="Araç tipi" value={f.vehicleType ?? '—'} />
          <Summary label="Olay" value={dataReady ? String(eventTotal) : '—'} />
          <div>
            <Text size="xs" c="dimmed">
              Bulgular
            </Text>
            {f.latestAnalysis ? (
              <Group gap={4}>
                {f.latestAnalysis.criticalCount > 0 && <SeverityBadge severity="Critical" count={f.latestAnalysis.criticalCount} />}
                {f.latestAnalysis.warningCount > 0 && <SeverityBadge severity="Warning" count={f.latestAnalysis.warningCount} />}
                {f.latestAnalysis.infoCount > 0 && <SeverityBadge severity="Info" count={f.latestAnalysis.infoCount} />}
                {f.latestAnalysis.findingCount === 0 && <Badge color="green" variant="light">Bulgu yok</Badge>}
              </Group>
            ) : (
              <Text size="sm">—</Text>
            )}
          </div>
        </Group>
      </Card>

      <Tabs value={tab} onChange={(v) => update({ tab: v === 'analysis' ? null : v })} keepMounted={false}>
        <Tabs.List mb="md">
          <Tabs.Tab value="analysis">İnceleme</Tabs.Tab>
          <Tabs.Tab value="findings">Bulgular {f.latestAnalysis ? `(${f.latestAnalysis.findingCount})` : ''}</Tabs.Tab>
          <Tabs.Tab value="signals">Sinyaller</Tabs.Tab>
          <Tabs.Tab value="parameters">Parametreler</Tabs.Tab>
          {hasGps && <Tabs.Tab value="map">Harita</Tabs.Tab>}
          <Tabs.Tab value="processing">İşleme</Tabs.Tab>
          <Tabs.Tab value="details">Bilgiler</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="analysis">
          {!dataReady ? (
            <NotReady status={file?.status} message={file?.failureMessage} />
          ) : catalog.isPending ? (
            <LoadingState />
          ) : catalog.isError ? (
            <ErrorState error={catalog.error} onRetry={() => catalog.refetch()} />
          ) : catalog.data.length === 0 ? (
            <EmptyState title="Sinyal yok">Bu logdan okunabilir sinyal çıkmadı.</EmptyState>
          ) : (
            <Grid gutter="md">
              <Grid.Col span={{ base: 12, lg: 8 }}>
                <Stack gap="md">
                  <Card withBorder padding="sm">
                    <FlightTimeline
                      events={allEvents}
                      findings={findingList}
                      logFromUs={logFromUs}
                      logToUs={logToUs}
                      window={window}
                      onSelect={(from, to) => setWindow(windowAround(from, to, logFromUs, logToUs, 0))}
                    />
                  </Card>
                  <ChartWorkspace
                    flightId={f.id}
                    logFromUs={logFromUs}
                    logToUs={logToUs}
                    catalog={catalog.data}
                    selected={selected}
                    onSelectedChange={(v) => update({ signals: v.join(',') })}
                    window={window}
                    onWindowChange={setWindow}
                    events={allEvents}
                    findings={findingList}
                    markerKinds={markerKinds}
                    onMarkerKindsChange={setMarkerKinds}
                    showFindings={showFindings}
                    onShowFindingsChange={setShowFindings}
                    selection={{ eventUs: pickedUs, findingId: selectedFinding }}
                    onPickTime={setPickedUs}
                  />
                  {hasGps && <FlightMap flightId={f.id} logFromUs={logFromUs} window={window} pickedUs={pickedUs} height={280} />}
                </Stack>
              </Grid.Col>
              <Grid.Col span={{ base: 12, lg: 4 }}>
                <Card withBorder padding="sm">
                  <SegmentedControl
                    fullWidth
                    size="xs"
                    mb="sm"
                    value={side}
                    onChange={(v) => setSide(v as 'events' | 'findings')}
                    data={[
                      { value: 'events', label: `Olaylar (${eventTotal})` },
                      { value: 'findings', label: `Bulgular (${findingList.length})` },
                    ]}
                  />
                  {side === 'events' ? (
                    events.isError ? (
                      <ErrorState error={events.error} onRetry={() => events.refetch()} />
                    ) : events.isPending ? (
                      <LoadingState />
                    ) : (
                      <EventsPanel events={allEvents} total={eventTotal} logFromUs={logFromUs} selectedUs={pickedUs} onSelect={selectEvent} height="65vh" />
                    )
                  ) : (
                    findingsPanel
                  )}
                </Card>
              </Grid.Col>
            </Grid>
          )}
        </Tabs.Panel>

        <Tabs.Panel value="findings">{findingsPanel}</Tabs.Panel>

        <Tabs.Panel value="signals">
          {!dataReady ? (
            <NotReady status={file?.status} message={file?.failureMessage} />
          ) : catalog.isPending ? (
            <LoadingState />
          ) : catalog.isError ? (
            <ErrorState error={catalog.error} onRetry={() => catalog.refetch()} />
          ) : (
            <SignalsPanel
              catalog={catalog.data}
              selected={selected}
              canAdd={selected.length < MAX_SIGNALS}
              onAdd={(name) => update({ tab: null, signals: [...selected, name].join(',') })}
            />
          )}
        </Tabs.Panel>

        <Tabs.Panel value="parameters">
          {dataReady ? <ParametersPanel flightId={f.id} logFromUs={logFromUs} /> : <NotReady status={file?.status} message={file?.failureMessage} />}
        </Tabs.Panel>

        {hasGps && (
          <Tabs.Panel value="map">
            <FlightMap flightId={f.id} logFromUs={logFromUs} window={window} pickedUs={pickedUs} />
          </Tabs.Panel>
        )}

        <Tabs.Panel value="processing">
          {file ? <ProcessingPanel file={file} /> : <EmptyState title="Log dosyası yok" />}
        </Tabs.Panel>

        <Tabs.Panel value="details">
          <MetadataPanel key={f.version} flight={f} />
        </Tabs.Panel>
      </Tabs>
    </>
  );
}

function Summary({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <Text size="xs" c="dimmed">
        {label}
      </Text>
      <Text size="sm" fw={500}>
        {value}
      </Text>
    </div>
  );
}

function NotReady({ status, message }: { status: string | undefined; message: string | null | undefined }) {
  if (status === 'Failed' || status === 'Cancelled') {
    return (
      <Alert color={status === 'Failed' ? 'red' : 'gray'} title={status === 'Failed' ? 'Log işlenemedi' : 'İşleme iptal edildi'}>
        {message ?? 'Ayrıntılar İşleme sekmesinde.'}
      </Alert>
    );
  }

  return (
    <Stack align="center" py="xl" gap="xs">
      <LoadingState label={isActive(status) ? 'Log işleniyor; veriler hazır olunca bu sayfa kendiliğinden güncellenir.' : 'Veri bekleniyor…'} />
    </Stack>
  );
}
