import { Badge, Checkbox, Group, Stack, Text, TextInput } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconSearch } from '@tabler/icons-react';
import { useMemo, useState } from 'react';
import { useParameters } from '../api/queries';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import { VirtualList } from '../components/VirtualList';
import { formatLogTime, formatNumber } from '../lib/format';

/** Vehicle parameters as logged: initial value, default where the log has one, and changes during the log. */
export function ParametersPanel({ flightId, logFromUs }: { flightId: string; logFromUs: number }) {
  const parameters = useParameters(flightId);
  const [search, setSearch] = useState('');
  const [changedOnly, setChangedOnly] = useState(false);
  const [nonDefaultOnly, setNonDefaultOnly] = useState(false);
  const [term] = useDebouncedValue(search.trim().toUpperCase(), 200);

  const filtered = useMemo(
    () =>
      (parameters.data ?? []).filter(
        (p) =>
          (!term || p.name.toUpperCase().includes(term)) &&
          (!changedOnly || p.changes.length > 0) &&
          (!nonDefaultOnly || (p.defaultValue != null && p.initialValue !== p.defaultValue)),
      ),
    [parameters.data, term, changedOnly, nonDefaultOnly],
  );

  if (parameters.isPending) {
    return <LoadingState />;
  }

  if (parameters.isError) {
    return <ErrorState error={parameters.error} onRetry={() => parameters.refetch()} />;
  }

  const hasDefaults = parameters.data.some((p) => p.defaultValue != null);

  return (
    <Stack gap="xs">
      <Group gap="md">
        <TextInput
          aria-label="Parametre ara"
          placeholder="Parametre adı"
          leftSection={<IconSearch size={14} />}
          value={search}
          onChange={(e) => setSearch(e.currentTarget.value)}
          w={260}
        />
        <Checkbox label="Yalnızca uçuşta değişenler" checked={changedOnly} onChange={(e) => setChangedOnly(e.currentTarget.checked)} />
        {hasDefaults && (
          <Checkbox label="Yalnızca varsayılandan farklı" checked={nonDefaultOnly} onChange={(e) => setNonDefaultOnly(e.currentTarget.checked)} />
        )}
        <Text size="xs" c="dimmed">
          {filtered.length} / {parameters.data.length}
        </Text>
      </Group>
      {filtered.length === 0 ? (
        <EmptyState title="Parametre yok">{parameters.data.length === 0 ? 'Bu log parametre kaydı içermiyor.' : 'Filtreye uyan parametre yok.'}</EmptyState>
      ) : (
        <VirtualList
          label="Parametreler"
          items={filtered}
          rowHeight={36}
          height="60vh"
          renderRow={(p) => (
            <Group
              h="100%"
              px="xs"
              gap="md"
              wrap="nowrap"
              style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}
            >
              <Text size="sm" ff="monospace" w={200} style={{ flexShrink: 0 }}>
                {p.name}
              </Text>
              <Text size="sm" ff="monospace" w={110} style={{ flexShrink: 0 }}>
                {formatNumber(p.initialValue, 6)}
              </Text>
              {hasDefaults && (
                <Text size="xs" c="dimmed" w={130} style={{ flexShrink: 0 }}>
                  {p.defaultValue == null ? '' : `varsayılan ${formatNumber(p.defaultValue, 6)}`}
                </Text>
              )}
              {p.changes.length > 0 && (
                <Group gap={4} wrap="nowrap" style={{ overflow: 'hidden' }}>
                  <Badge size="xs" color="orange" variant="light">
                    {p.changes.length} değişiklik
                  </Badge>
                  <Text size="xs" ff="monospace" truncate>
                    {p.changes.map((c) => `${formatLogTime(c.timestampUs, logFromUs)} → ${formatNumber(c.value, 6)}`).join(' · ')}
                  </Text>
                </Group>
              )}
            </Group>
          )}
        />
      )}
    </Stack>
  );
}
