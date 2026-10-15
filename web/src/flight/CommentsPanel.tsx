import { ActionIcon, Badge, Button, Card, Checkbox, Group, Stack, Text, Textarea, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconChartLine, IconPencil, IconTrash } from '@tabler/icons-react';
import { useState } from 'react';
import { errorMessage } from '../api/client';
import { useAddComment, useComments, useDeleteComment, useEditComment } from '../api/queries';
import type { FlightComment } from '../api/types';
import { useTeam } from '../auth/TeamContext';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import { formatDateTime, formatLogTime } from '../lib/format';
import type { TimeWindow } from './view';

const MAX_LENGTH = 4000;

/**
 * Comments of a flight and notes on a time range of it. A note can take the range shown in the charts; "Grafikte
 * göster" brings the range back. Comments written on findings appear here too, marked as such.
 */
export function CommentsPanel({
  flightId,
  logFromUs,
  window,
  onShowRange,
}: {
  flightId: string;
  logFromUs: number;
  window: TimeWindow | null;
  onShowRange: (startUs: number, endUs: number) => void;
}) {
  const { can } = useTeam();
  const comments = useComments(flightId);
  const add = useAddComment(flightId);
  const [body, setBody] = useState('');
  const [useRange, setUseRange] = useState(false);

  const submit = () => {
    const range = useRange && window ? { startUs: Math.round(window.fromUs), endUs: Math.round(window.toUs) } : {};
    add.mutate(
      { body: body.trim(), ...range },
      {
        onSuccess: () => {
          setBody('');
          setUseRange(false);
        },
        onError: (e) => notifications.show({ color: 'red', title: 'Yorum eklenemedi', message: errorMessage(e) }),
      },
    );
  };

  return (
    <Stack gap="sm">
      {can('Engineer') && (
        <Card withBorder padding="sm">
          <Stack gap="xs">
            <Textarea
              aria-label="Yeni yorum"
              placeholder="Yorum veya not yazın"
              autosize
              minRows={2}
              maxLength={MAX_LENGTH}
              value={body}
              onChange={(e) => setBody(e.currentTarget.value)}
            />
            <Group justify="space-between">
              <Checkbox
                size="xs"
                disabled={!window}
                checked={useRange && !!window}
                onChange={(e) => setUseRange(e.currentTarget.checked)}
                label={
                  window
                    ? `Grafikteki aralığa bağla (${formatLogTime(window.fromUs, logFromUs)} – ${formatLogTime(window.toUs, logFromUs)})`
                    : 'Aralığa bağlamak için İnceleme sekmesinde grafiği yakınlaştırın'
                }
              />
              <Button size="xs" onClick={submit} loading={add.isPending} disabled={body.trim().length === 0}>
                Ekle
              </Button>
            </Group>
          </Stack>
        </Card>
      )}

      {comments.isPending ? (
        <LoadingState />
      ) : comments.isError ? (
        <ErrorState error={comments.error} onRetry={() => comments.refetch()} />
      ) : comments.data.length === 0 ? (
        <EmptyState title="Yorum yok">Bu uçuşa henüz yorum veya not eklenmedi.</EmptyState>
      ) : (
        comments.data.map((c) => <CommentItem key={c.id} comment={c} flightId={flightId} logFromUs={logFromUs} onShowRange={onShowRange} />)
      )}
    </Stack>
  );
}

/** One comment with its author, time range and, for its author or a manager, edit and delete. */
export function CommentItem({
  comment: c,
  flightId,
  logFromUs,
  onShowRange,
}: {
  comment: FlightComment;
  flightId: string;
  logFromUs: number;
  onShowRange?: (startUs: number, endUs: number) => void;
}) {
  const edit = useEditComment(flightId);
  const remove = useDeleteComment(flightId);
  const [editing, setEditing] = useState<string | null>(null);

  const save = () =>
    edit.mutate(
      { id: c.id, body: editing!.trim(), version: c.version },
      {
        onSuccess: () => setEditing(null),
        onError: (e) => notifications.show({ color: 'red', title: 'Yorum kaydedilemedi', message: errorMessage(e) }),
      },
    );

  const destroy = () => {
    if (!globalThis.confirm('Yorum silinsin mi? Silinen yorumun metni denetim kaydında kalır.')) {
      return;
    }

    remove.mutate(c.id, { onError: (e) => notifications.show({ color: 'red', title: 'Yorum silinemedi', message: errorMessage(e) }) });
  };

  return (
    <Card withBorder padding="xs" data-testid="comment">
      <Group justify="space-between" align="flex-start" wrap="nowrap">
        <Stack gap={2} style={{ minWidth: 0, flex: 1 }}>
          <Group gap="xs">
            <Text size="xs" fw={600}>
              {c.author.displayName}
            </Text>
            <Text size="xs" c="dimmed">
              {formatDateTime(c.createdAt)}
              {c.editedAt ? ' · düzenlendi' : ''}
            </Text>
            {c.findingReviewId && (
              <Badge size="xs" variant="light" color="grape">
                Bulgu üzerine
              </Badge>
            )}
            {c.startUs != null && c.endUs != null && (
              <Badge size="xs" variant="light">
                {c.startUs === c.endUs ? formatLogTime(c.startUs, logFromUs) : `${formatLogTime(c.startUs, logFromUs)} – ${formatLogTime(c.endUs, logFromUs)}`}
              </Badge>
            )}
          </Group>
          {c.deleted ? (
            <Text size="sm" c="dimmed" fs="italic">
              Bu yorum silindi.
            </Text>
          ) : editing !== null ? (
            <Stack gap={4}>
              <Textarea aria-label="Yorumu düzenle" autosize minRows={2} maxLength={MAX_LENGTH} value={editing} onChange={(e) => setEditing(e.currentTarget.value)} />
              <Group gap="xs">
                <Button size="xs" onClick={save} loading={edit.isPending} disabled={editing.trim().length === 0}>
                  Kaydet
                </Button>
                <Button size="xs" variant="subtle" onClick={() => setEditing(null)}>
                  Vazgeç
                </Button>
              </Group>
            </Stack>
          ) : (
            <Text size="sm" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>
              {c.body}
            </Text>
          )}
        </Stack>
        <Group gap={2} wrap="nowrap">
          {onShowRange && c.startUs != null && c.endUs != null && (
            <Tooltip label="Grafikte göster">
              <ActionIcon variant="subtle" size="sm" aria-label="Grafikte göster" onClick={() => onShowRange(c.startUs!, c.endUs!)}>
                <IconChartLine size={14} />
              </ActionIcon>
            </Tooltip>
          )}
          {c.canEdit && editing === null && (
            <ActionIcon variant="subtle" size="sm" aria-label="Düzenle" onClick={() => setEditing(c.body ?? '')}>
              <IconPencil size={14} />
            </ActionIcon>
          )}
          {c.canDelete && (
            <ActionIcon variant="subtle" color="red" size="sm" aria-label="Sil" onClick={destroy} loading={remove.isPending}>
              <IconTrash size={14} />
            </ActionIcon>
          )}
        </Group>
      </Group>
    </Card>
  );
}
