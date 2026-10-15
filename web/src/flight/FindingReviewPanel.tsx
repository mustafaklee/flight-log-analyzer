import { Badge, Button, Divider, Group, Select, Stack, Switch, Text, Textarea, Timeline } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconFlag } from '@tabler/icons-react';
import { useMemo, useState } from 'react';
import { errorMessage } from '../api/client';
import { useComments, useFindingHistory, useMembers, useUpdateReview } from '../api/queries';
import type { Finding, FindingReview, FindingReviewStatus, UpdateFindingReviewRequest } from '../api/types';
import { useTeam } from '../auth/TeamContext';
import { LoadingState } from '../components/States';
import { auditActionLabel, reviewStatusColors, reviewStatusLabels } from '../lib/labels';
import { formatDateTime } from '../lib/format';
import { CommentItem } from './CommentsPanel';

const STATUSES: FindingReviewStatus[] = ['Open', 'Investigating', 'Confirmed', 'Dismissed', 'Resolved'];
const UNASSIGNED = '__none__';

/** "from → to" of a status or assignment change, in Turkish; empty for other entries. */
function change(action: string, data: Record<string, string | null>): string {
  if (action === 'finding.status_changed') {
    const label = (v: string | null | undefined) => (v && v in reviewStatusLabels ? reviewStatusLabels[v as FindingReviewStatus] : (v ?? '—'));
    return ` · ${label(data.from)} → ${label(data.to)}`;
  }

  return action === 'finding.assigned' ? ` · ${data.fromName ?? 'atanmamış'} → ${data.toName ?? 'atanmamış'}` : '';
}

/** A small badge with the review state of a finding (Open when nobody reviewed it yet). */
export function ReviewBadge({ review }: { review: FindingReview | null | undefined }) {
  const status = review?.status ?? 'Open';
  return (
    <Group gap={4} wrap="nowrap">
      {status !== 'Open' && (
        <Badge size="xs" variant="light" color={reviewStatusColors[status]}>
          {reviewStatusLabels[status]}
        </Badge>
      )}
      {review?.flagged && <IconFlag size={14} color="var(--mantine-color-red-6)" aria-label="İşaretli" />}
      {review?.assignee && (
        <Text size="xs" c="dimmed" lineClamp={1}>
          {review.assignee.displayName}
        </Text>
      )}
    </Group>
  );
}

/**
 * Review of one finding: status, assignee, flag and a note, with the comments written on it and the history of changes.
 * The review belongs to the finding's rule and time range, so it carries over to later analyses of the flight.
 */
export function FindingReviewPanel({ finding, flightId, teamId, logFromUs }: { finding: Finding; flightId: string; teamId: string; logFromUs: number }) {
  const { can } = useTeam();
  const editable = can('Engineer');
  const update = useUpdateReview(finding.runId);
  const members = useMembers(editable ? teamId : undefined);
  const history = useFindingHistory(finding.id);
  const comments = useComments(flightId);
  const [note, setNote] = useState('');
  const review = finding.review;

  const thread = useMemo(
    () => (review ? (comments.data ?? []).filter((c) => c.findingReviewId === review.id) : []),
    [comments.data, review],
  );
  const assignable = (members.data ?? []).filter((m) => m.role !== 'Viewer');

  const send = (body: Partial<UpdateFindingReviewRequest>, done?: () => void) =>
    update.mutate(
      { findingId: finding.id, body },
      {
        onSuccess: () => done?.(),
        onError: (e) => notifications.show({ color: 'red', title: 'İnceleme kaydedilemedi', message: errorMessage(e) }),
      },
    );

  return (
    <Stack gap="xs" data-testid="finding-review">
      <Divider label="İnceleme" labelPosition="left" />
      {editable ? (
        <>
          <Group gap="xs" align="flex-end">
            <Select
              size="xs"
              label="Durum"
              w={150}
              allowDeselect={false}
              data={STATUSES.map((s) => ({ value: s, label: reviewStatusLabels[s] }))}
              value={review?.status ?? 'Open'}
              onChange={(v) => v && v !== (review?.status ?? 'Open') && send({ status: v as FindingReviewStatus })}
              disabled={update.isPending}
            />
            <Select
              size="xs"
              label="Atanan"
              w={190}
              allowDeselect={false}
              data={[{ value: UNASSIGNED, label: 'Atanmadı' }, ...assignable.map((m) => ({ value: m.userId, label: m.displayName }))]}
              value={review?.assignee?.id ?? UNASSIGNED}
              onChange={(v) => {
                if (!v || v === (review?.assignee?.id ?? UNASSIGNED)) return;
                send(v === UNASSIGNED ? { unassign: true } : { assigneeId: v });
              }}
              disabled={update.isPending || members.isPending}
            />
            <Switch
              size="xs"
              label="İşaretle"
              checked={review?.flagged ?? false}
              onChange={(e) => send({ flagged: e.currentTarget.checked })}
              disabled={update.isPending}
            />
          </Group>
          <Group gap="xs" align="flex-end" wrap="nowrap">
            <Textarea
              size="xs"
              aria-label="Bulguya not"
              placeholder="Bulguya not ekleyin (ör. neden geçersiz sayıldı)"
              autosize
              minRows={1}
              maxLength={4000}
              style={{ flex: 1 }}
              value={note}
              onChange={(e) => setNote(e.currentTarget.value)}
            />
            <Button size="xs" variant="light" disabled={note.trim().length === 0} loading={update.isPending} onClick={() => send({ note: note.trim() }, () => setNote(''))}>
              Not ekle
            </Button>
          </Group>
        </>
      ) : (
        <Group gap="xs">
          <Text size="xs">Durum: {reviewStatusLabels[review?.status ?? 'Open']}</Text>
          <Text size="xs">Atanan: {review?.assignee?.displayName ?? '—'}</Text>
          {review?.flagged && <Text size="xs">İşaretli</Text>}
        </Group>
      )}

      {thread.map((c) => (
        <CommentItem key={c.id} comment={c} flightId={flightId} logFromUs={logFromUs} />
      ))}

      {review && (
        <>
          <Text size="xs" c="dimmed">
            Son değişiklik: {review.updatedBy.displayName}, {formatDateTime(review.updatedAt)}
          </Text>
          {history.isPending ? (
            <LoadingState />
          ) : (
            (history.data?.length ?? 0) > 0 && (
              <Timeline bulletSize={10} lineWidth={1} data-testid="review-history">
                {history.data!.map((h) => (
                  <Timeline.Item key={h.id} title={<Text size="xs">{auditActionLabel(h.action)}</Text>}>
                    <Text size="xs" c="dimmed">
                      {h.actor.displayName} · {formatDateTime(h.createdAt)}
                      {change(h.action, h.data)}
                    </Text>
                  </Timeline.Item>
                ))}
              </Timeline>
            )
          )}
        </>
      )}
    </Stack>
  );
}
