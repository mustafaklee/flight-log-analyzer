import { Button, Menu, TagsInput, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconDownload, IconTags } from '@tabler/icons-react';
import { useState } from 'react';
import { download, errorMessage } from '../api/client';
import { useFlightTags, useSetFlightTags } from '../api/queries';
import type { Flight } from '../api/types';
import { useTeam } from '../auth/TeamContext';

const MAX_TAGS = 20;
const MAX_TAG_LENGTH = 40;

/** Tags of a flight; engineers edit them in place, others see them read-only. */
export function FlightTags({ flight }: { flight: Flight }) {
  const { can } = useTeam();
  const known = useFlightTags(flight.teamId);
  const save = useSetFlightTags(flight.id, flight.teamId);
  const [draft, setDraft] = useState<string[] | null>(null);

  if (!can('Engineer')) {
    return flight.tags.length > 0 ? <Text size="sm">{flight.tags.join(', ')}</Text> : null;
  }

  const commit = (tags: string[]) => {
    setDraft(null);
    if (tags.join('\u0000') === flight.tags.join('\u0000')) {
      return;
    }

    save.mutate(tags, { onError: (e) => notifications.show({ color: 'red', title: 'Etiketler kaydedilemedi', message: errorMessage(e) }) });
  };

  return (
    <TagsInput
      size="xs"
      w={320}
      aria-label="Etiketler"
      leftSection={<IconTags size={14} />}
      placeholder={flight.tags.length === 0 ? 'Etiket ekleyin' : undefined}
      data={(known.data ?? []).map((t) => t.tag)}
      value={draft ?? flight.tags}
      onChange={(v) => setDraft(v.filter((t) => t.trim().length > 0 && t.length <= MAX_TAG_LENGTH))}
      onBlur={() => draft && commit(draft)}
      onKeyDown={(e) => e.key === 'Enter' && draft && e.currentTarget.value === '' && commit(draft)}
      maxTags={MAX_TAGS}
      clearable
      onClear={() => commit([])}
      disabled={save.isPending}
    />
  );
}

/** Downloads of the flight: JSON report, findings and events as CSV. Each download is recorded in the audit log. */
export function ExportMenu({ flight }: { flight: Flight }) {
  const [busy, setBusy] = useState(false);
  const run = async (path: string, fallbackName: string) => {
    setBusy(true);
    try {
      await download(`/flights/${flight.id}/export/${path}`, fallbackName);
    } catch (e) {
      notifications.show({ color: 'red', title: 'Dışa aktarılamadı', message: errorMessage(e) });
    } finally {
      setBusy(false);
    }
  };

  const safe = flight.name.replace(/[^\p{L}\p{N}_-]+/gu, '-').slice(0, 60) || 'ucus';
  return (
    <Menu position="bottom-end">
      <Menu.Target>
        <Button size="xs" variant="default" leftSection={<IconDownload size={14} />} loading={busy}>
          Dışa aktar
        </Button>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item onClick={() => run('report.json', `${safe}-rapor.json`)}>Uçuş raporu (JSON)</Menu.Item>
        <Menu.Item onClick={() => run('findings.csv', `${safe}-bulgular.csv`)}>Bulgular (CSV)</Menu.Item>
        <Menu.Item onClick={() => run('events.csv', `${safe}-olaylar.csv`)}>Olaylar (CSV)</Menu.Item>
        <Menu.Divider />
        <Menu.Label>İndirmeler denetim kaydına yazılır.</Menu.Label>
      </Menu.Dropdown>
    </Menu>
  );
}
