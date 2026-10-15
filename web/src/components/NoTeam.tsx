import { Button, Card, Stack, Text, TextInput, Title } from '@mantine/core';
import { useState } from 'react';
import { errorMessage } from '../api/client';
import { useCreateTeam } from '../api/queries';
import { useAuth } from '../auth/AuthContext';

/** Shown when the user belongs to no team yet: every screen works within a team. */
export function NoTeam() {
  const [name, setName] = useState('');
  const create = useCreateTeam();
  const { reloadProfile } = useAuth();

  return (
    <Card withBorder maw={480} mx="auto" mt="xl" padding="lg">
      <Stack>
        <Title order={3}>Henüz bir ekibiniz yok</Title>
        <Text size="sm" c="dimmed">
          Araçlar, projeler ve uçuşlar bir ekibe aittir. Yeni bir ekip oluşturun ya da bir proje yöneticisinden sizi ekibine eklemesini isteyin.
        </Text>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            create.mutate(name.trim(), { onSuccess: () => void reloadProfile() });
          }}
        >
          <Stack>
            <TextInput label="Ekip adı" required value={name} onChange={(e) => setName(e.currentTarget.value)} maxLength={100} error={create.error ? errorMessage(create.error) : undefined} />
            <Button type="submit" loading={create.isPending} disabled={!name.trim()}>
              Ekip oluştur
            </Button>
          </Stack>
        </form>
      </Stack>
    </Card>
  );
}
