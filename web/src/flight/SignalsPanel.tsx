import { Badge, Button, Group, SegmentedControl, Stack, Text, TextInput, Tooltip } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconSearch } from '@tabler/icons-react';
import { useMemo, useState } from 'react';
import type { Signal } from '../api/types';
import { EmptyState } from '../components/States';
import { VirtualList } from '../components/VirtualList';
import { formatNumber } from '../lib/format';

/**
 * The signal catalog: normalized signals with the log field they come from and the conversion applied, and the raw
 * log fields. Adding a signal here puts it on the charts.
 */
export function SignalsPanel({
  catalog,
  selected,
  canAdd,
  onAdd,
}: {
  catalog: Signal[];
  selected: string[];
  canAdd: boolean;
  onAdd: (name: string) => void;
}) {
  const [kind, setKind] = useState<'Normalized' | 'Raw'>('Normalized');
  const [search, setSearch] = useState('');
  const [term] = useDebouncedValue(search.trim().toLowerCase(), 200);

  const filtered = useMemo(
    () => catalog.filter((s) => s.kind === kind && (!term || s.name.toLowerCase().includes(term))),
    [catalog, kind, term],
  );
  const counts = { Normalized: catalog.filter((s) => s.kind === 'Normalized').length, Raw: catalog.filter((s) => s.kind === 'Raw').length };

  return (
    <Stack gap="xs">
      <Group gap="md">
        <SegmentedControl
          size="xs"
          value={kind}
          onChange={(v) => setKind(v as 'Normalized' | 'Raw')}
          data={[
            { value: 'Normalized', label: `Normalize (${counts.Normalized})` },
            { value: 'Raw', label: `Ham (${counts.Raw})` },
          ]}
        />
        <TextInput
          aria-label="Sinyal ara"
          size="xs"
          placeholder="Sinyal adı"
          leftSection={<IconSearch size={12} />}
          value={search}
          onChange={(e) => setSearch(e.currentTarget.value)}
          w={240}
        />
      </Group>
      {kind === 'Normalized' && (
        <Text size="xs" c="dimmed">
          Normalize sinyaller PX4 ve ArduPilot loglarını aynı ad ve birimle sunar; her birinin hangi log alanından ve hangi dönüşümle
          üretildiği yanında yazar.
        </Text>
      )}
      {filtered.length === 0 ? (
        <EmptyState title="Sinyal yok">{catalog.length === 0 ? 'Bu logda sinyal bulunmuyor.' : 'Aramaya uyan sinyal yok.'}</EmptyState>
      ) : (
        <VirtualList
          label="Sinyaller"
          items={filtered}
          rowHeight={52}
          height="60vh"
          renderRow={(s) => {
            const isSelected = selected.includes(s.name);
            return (
              <Group h="100%" px="xs" gap="md" wrap="nowrap" style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}>
                <Stack gap={0} style={{ flex: 1, minWidth: 0 }}>
                  <Group gap={6} wrap="nowrap">
                    <Text size="sm" ff="monospace" truncate>
                      {s.name}
                    </Text>
                    {s.unit && (
                      <Badge size="xs" variant="outline" color="gray">
                        {s.unit}
                      </Badge>
                    )}
                    {s.invalidCount > 0 && (
                      <Tooltip label="Geçersiz (NaN/sonsuz) örnek sayısı">
                        <Badge size="xs" variant="light" color="yellow">
                          {s.invalidCount} geçersiz
                        </Badge>
                      </Tooltip>
                    )}
                  </Group>
                  <Text size="xs" c="dimmed" truncate>
                    {s.source
                      ? `${s.source.message}[${s.source.instance}].${s.source.fields} · ${s.source.conversion}`
                      : `${s.message}[${s.instance}].${s.field} · ${s.sourceType}`}
                  </Text>
                </Stack>
                <Text size="xs" c="dimmed" w={180} ta="right" style={{ flexShrink: 0 }}>
                  {s.sampleCount} örnek · {formatNumber(s.min, 2)} … {formatNumber(s.max, 2)}
                </Text>
                <Button size="compact-xs" variant="light" disabled={isSelected || !canAdd || s.sampleCount === 0} onClick={() => onAdd(s.name)}>
                  {isSelected ? 'Grafikte' : 'Grafiğe ekle'}
                </Button>
              </Group>
            );
          }}
        />
      )}
    </Stack>
  );
}
