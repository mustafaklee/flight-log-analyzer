import { Badge, Loader, Tooltip } from '@mantine/core';
import type { FindingSeverity, ProcessingStatus } from '../api/types';
import { isActive } from '../api/queries';
import { severityColors, severityLabels, statusColors, statusLabels } from '../lib/labels';

export function StatusBadge({ status, failureCode }: { status: ProcessingStatus | null | undefined; failureCode?: string | null }) {
  if (!status) {
    return <Badge color="gray" variant="light">Dosya yok</Badge>;
  }

  const badge = (
    <Badge
      color={statusColors[status]}
      variant="light"
      leftSection={isActive(status) ? <Loader size={10} color={statusColors[status]} aria-hidden /> : undefined}
    >
      {statusLabels[status]}
    </Badge>
  );
  return failureCode ? <Tooltip label={failureCode}>{badge}</Tooltip> : badge;
}

export function SeverityBadge({ severity, count }: { severity: FindingSeverity; count?: number }) {
  return (
    <Badge color={severityColors[severity]} variant={severity === 'Info' ? 'light' : 'filled'}>
      {count != null ? `${count} ` : ''}
      {severityLabels[severity]}
    </Badge>
  );
}
