import { Box, Group, Text, Tooltip } from '@mantine/core';
import { useMemo } from 'react';
import type { Finding, FlightEvent } from '../api/types';
import { severityColors } from '../lib/labels';
import { mantineColor } from './palette';
import type { TimeWindow } from './view';

const modePalette = ['blue', 'teal', 'grape', 'orange', 'cyan', 'lime', 'indigo', 'pink', 'violet', 'yellow'];

interface Segment {
  fromUs: number;
  toUs: number;
  label: string;
  color: string;
}

/**
 * Overview of the whole log: flight modes, armed periods and finding ranges as bands, with the current chart window
 * outlined. Shows what the log recorded; it draws nothing for periods without records.
 */
export function FlightTimeline({
  events,
  findings,
  logFromUs,
  logToUs,
  window,
  onSelect,
}: {
  events: FlightEvent[];
  findings: Finding[];
  logFromUs: number;
  logToUs: number;
  window: TimeWindow | null;
  onSelect: (fromUs: number, toUs: number) => void;
}) {
  const span = Math.max(logToUs - logFromUs, 1);
  const pct = (us: number) => `${(((Math.min(Math.max(us, logFromUs), logToUs) - logFromUs) / span) * 100).toFixed(3)}%`;
  const width = (from: number, to: number) => `${Math.max(((Math.min(to, logToUs) - Math.max(from, logFromUs)) / span) * 100, 0.3).toFixed(3)}%`;

  const { modes, armed, legend } = useMemo(() => {
    const modeEvents = events.filter((e) => e.kind === 'ModeChange');
    const colors = new Map<string, string>();
    const modes: Segment[] = modeEvents.map((e, i) => {
      const label = e.data?.mode ?? e.text;
      if (!colors.has(label)) {
        colors.set(label, modePalette[colors.size % modePalette.length]!);
      }

      return { fromUs: e.timestampUs, toUs: modeEvents[i + 1]?.timestampUs ?? logToUs, label, color: colors.get(label)! };
    });

    const armed: Segment[] = [];
    let start: number | null = null;
    for (const e of events) {
      if (e.kind === 'Armed' && start === null) {
        start = e.timestampUs;
      } else if (e.kind === 'Disarmed') {
        // A log that starts with a disarm was recorded while armed.
        armed.push({ fromUs: start ?? logFromUs, toUs: e.timestampUs, label: 'Arm', color: 'green' });
        start = null;
      }
    }

    if (start !== null) {
      armed.push({ fromUs: start, toUs: logToUs, label: 'Arm', color: 'green' });
    }

    return { modes, armed, legend: [...colors.entries()] };
  }, [events, logFromUs, logToUs]);

  const severe = findings.filter((f) => f.severity !== 'Info');

  const row = (label: string, segments: Segment[], empty: string) => (
    <Group gap="xs" wrap="nowrap">
      <Text size="xs" w={70} c="dimmed" style={{ flexShrink: 0 }}>
        {label}
      </Text>
      <Box pos="relative" h={14} style={{ flex: 1, background: 'var(--mantine-color-default-hover)', borderRadius: 3 }}>
        {segments.length === 0 && (
          <Text size="10px" c="dimmed" pos="absolute" left={4} top={0} lh="14px">
            {empty}
          </Text>
        )}
        {segments.map((s, i) => (
          <Tooltip key={i} label={`${s.label}: ${((s.fromUs - logFromUs) / 1e6).toFixed(1)}–${((s.toUs - logFromUs) / 1e6).toFixed(1)} s`}>
            <Box
              component="button"
              type="button"
              aria-label={`${label} ${s.label}`}
              onClick={() => onSelect(s.fromUs, s.toUs)}
              pos="absolute"
              top={0}
              h={14}
              left={pct(s.fromUs)}
              w={width(s.fromUs, s.toUs)}
              style={{ background: mantineColor(s.color, 5), border: 0, padding: 0, cursor: 'pointer', borderRadius: 2 }}
            />
          </Tooltip>
        ))}
      </Box>
    </Group>
  );

  return (
    <Box>
      <Box pos="relative">
        {row('Mod', modes, 'Mod kaydı yok')}
        <Box h={4} />
        {row('Arm', armed, 'Arm kaydı yok')}
        <Box h={4} />
        {row(
          'Bulgular',
          severe.map((f) => ({ fromUs: f.startUs, toUs: Math.max(f.endUs, f.startUs + span / 400), label: f.title, color: severityColors[f.severity] })),
          'Uyarı veya kritik bulgu yok',
        )}
        {window && (
          <Box
            aria-hidden
            pos="absolute"
            top={-2}
            bottom={-2}
            style={{
              left: `calc(70px + var(--mantine-spacing-xs) + (100% - 70px - var(--mantine-spacing-xs)) * ${(window.fromUs - logFromUs) / span})`,
              width: `calc((100% - 70px - var(--mantine-spacing-xs)) * ${(window.toUs - window.fromUs) / span})`,
              border: '2px solid var(--mantine-color-blue-6)',
              borderRadius: 3,
              pointerEvents: 'none',
            }}
          />
        )}
      </Box>
      {legend.length > 0 && (
        <Group gap="sm" mt={6}>
          {legend.map(([mode, color]) => (
            <Group key={mode} gap={4}>
              <Box w={10} h={10} style={{ background: mantineColor(color, 5), borderRadius: 2 }} />
              <Text size="xs">{mode}</Text>
            </Group>
          ))}
        </Group>
      )}
    </Box>
  );
}
