import { Alert, Button, Center, Group, Loader, Stack, Text, ThemeIcon } from '@mantine/core';
import { IconAlertTriangle, IconInbox } from '@tabler/icons-react';
import type { ReactNode } from 'react';
import { ApiError, errorMessage } from '../api/client';

export function LoadingState({ label = 'Yükleniyor…' }: { label?: string }) {
  return (
    <Center py="xl" role="status">
      <Group gap="sm">
        <Loader size="sm" />
        <Text c="dimmed">{label}</Text>
      </Group>
    </Center>
  );
}

export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const notFound = error instanceof ApiError && error.status === 404;
  return (
    <Alert color={notFound ? 'gray' : 'red'} icon={<IconAlertTriangle />} title={notFound ? 'Bulunamadı' : 'Veri alınamadı'} role="alert">
      <Stack gap="xs" align="flex-start">
        <Text size="sm">{notFound ? 'Bu kayıt yok ya da erişim yetkiniz bulunmuyor.' : errorMessage(error)}</Text>
        {error instanceof ApiError && error.problem.traceId && (
          <Text size="xs" c="dimmed">
            İz kimliği: {error.problem.traceId}
          </Text>
        )}
        {onRetry && !notFound && (
          <Button size="xs" variant="light" onClick={onRetry}>
            Tekrar dene
          </Button>
        )}
      </Stack>
    </Alert>
  );
}

export function EmptyState({ title, children, action }: { title: string; children?: ReactNode; action?: ReactNode }) {
  return (
    <Center py="xl">
      <Stack align="center" gap="xs" maw={420}>
        <ThemeIcon variant="light" size="xl" radius="xl" color="gray">
          <IconInbox />
        </ThemeIcon>
        <Text fw={600}>{title}</Text>
        {children && (
          <Text size="sm" c="dimmed" ta="center">
            {children}
          </Text>
        )}
        {action}
      </Stack>
    </Center>
  );
}
