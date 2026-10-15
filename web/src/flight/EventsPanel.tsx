import { Badge, Group, Select, Stack, Text, TextInput, UnstyledButton } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconSearch } from '@tabler/icons-react';
import { useMemo, useState } from 'react';
import type { FlightEvent, FlightEventKind } from '../api/types';
import { EmptyState } from '../components/States';
import { VirtualList } from '../components/VirtualList';
import { formatLogTime } from '../lib/format';
import { eventKindColors, eventKindLabels, eventSeverityColor } from '../lib/labels';

/** All events of the flight (already loaded for the chart markers), filtered locally for quick browsing. */
export function EventsPanel({
  events,
  total,
  logFromUs,
  selectedUs,
  onSelect,
  height,
}: {
  events: FlightEvent[];
  total: number;
  logFromUs: number;
  selectedUs?: number;
  onSelect: (e: FlightEvent) => void;
  height: number | string;
}) {
  const [kind, setKind] = useState<FlightEventKind | null>(null);
  const [search, setSearch] = useState('');
  const [term] = useDebouncedValue(search.trim().toLowerCase(), 200);

  const filtered = useMemo(
    () => events.filter((e) => (!kind || e.kind === kind) && (!term || e.text.toLowerCase().includes(term) || e.source.toLowerCase().includes(term))),
    [events, kind, term],
  );

  return (
    <Stack gap="xs">
      <Group gap="xs" grow>
        <Select
          aria-label="Olay türü"
          placeholder="Tüm türler"
          clearable
          size="xs"
          data={Object.entries(eventKindLabels).map(([value, label]) => ({ value, label }))}
          value={kind}
          onChange={(v) => setKind(v as FlightEventKind | null)}
        />
        <TextInput aria-label="Olaylarda ara" size="xs" placeholder="Metinde ara" leftSection={<IconSearch size={12} />} value={search} onChange={(e) => setSearch(e.currentTarget.value)} />
      </Group>
      <Text size="xs" c="dimmed">
        {filtered.length} / {total} olay · bir olaya tıklayınca grafik o ana odaklanır
      </Text>
      {filtered.length === 0 ? (
        <EmptyState title="Olay yok">{events.length === 0 ? 'Bu logda olay kaydı bulunmuyor.' : 'Filtreye uyan olay yok.'}</EmptyState>
      ) : (
        <VirtualList
          label="Olaylar"
          items={filtered}
          rowHeight={52}
          height={height}
          renderRow={(e) => (
            <UnstyledButton
              onClick={() => onSelect(e)}
              w="100%"
              h="100%"
              px="xs"
              aria-current={e.timestampUs === selectedUs}
              style={{
                borderBottom: '1px solid var(--mantine-color-default-border)',
                background: e.timestampUs === selectedUs ? 'var(--mantine-color-blue-light)' : undefined,
              }}
            >
              <Group gap={6} wrap="nowrap">
                <Text size="xs" ff="monospace" w={80} style={{ flexShrink: 0 }}>
                  {formatLogTime(e.timestampUs, logFromUs)}
                </Text>
                <Badge size="xs" variant="light" color={eventKindColors[e.kind]} style={{ flexShrink: 0 }}>
                  {eventKindLabels[e.kind]}
                </Badge>
                {e.severity && (
                  <Badge size="xs" variant="dot" color={eventSeverityColor(e.severity)} style={{ flexShrink: 0 }}>
                    {e.severity}
                  </Badge>
                )}
              </Group>
              <Text size="xs" lineClamp={1} title={e.text}>
                {e.text}
              </Text>
            </UnstyledButton>
          )}
        />
      )}
    </Stack>
  );
}
