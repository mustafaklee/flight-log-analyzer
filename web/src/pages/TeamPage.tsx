import { ActionIcon, Button, Card, Group, Modal, Select, Stack, Table, Text, TextInput, Title, Tooltip } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import { modals } from '@mantine/modals';
import { IconPlus, IconTrash, IconUserPlus } from '@tabler/icons-react';
import { useState } from 'react';
import { errorMessage } from '../api/client';
import { useAddMember, useChangeMemberRole, useCreateTeam, useMembers, useRemoveMember } from '../api/queries';
import type { TeamRole } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { useTeam } from '../auth/TeamContext';
import { PageHeader } from '../components/PageHeader';
import { ErrorState, LoadingState } from '../components/States';
import { formatDate } from '../lib/format';
import { roleLabels } from '../lib/labels';

const roleOptions = (Object.keys(roleLabels) as TeamRole[]).map((value) => ({ value, label: roleLabels[value] }));

export function TeamPage() {
  const { team, can, selectTeam } = useTeam();
  const { user, reloadProfile } = useAuth();
  const members = useMembers(team?.teamId);
  const add = useAddMember(team?.teamId ?? '');
  const change = useChangeMemberRole(team?.teamId ?? '');
  const remove = useRemoveMember(team?.teamId ?? '');
  const createTeam = useCreateTeam();
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<TeamRole>('Engineer');
  const [teamName, setTeamName] = useState('');
  const [creating, { open, close }] = useDisclosure();
  const manage = can('ProjectManager');
  const error = add.error ?? change.error ?? remove.error;

  return (
    <>
      <PageHeader
        title={team?.teamName ?? 'Ekip'}
        description="Üyeler ve roller"
        actions={
          <Button variant="light" leftSection={<IconPlus size={16} />} onClick={open}>
            Yeni ekip
          </Button>
        }
      />
      <Stack>
        {manage && (
          <Card withBorder>
            <Title order={5} mb="sm">
              Üye ekle
            </Title>
            <form
              onSubmit={(e) => {
                e.preventDefault();
                add.mutate({ email: email.trim(), role }, { onSuccess: () => setEmail('') });
              }}
            >
              <Group align="flex-end">
                <TextInput label="E-posta" type="email" required value={email} onChange={(e) => setEmail(e.currentTarget.value)} description="Kişinin hesabı olmalı" w={280} />
                <Select label="Rol" data={roleOptions} value={role} onChange={(v) => v && setRole(v as TeamRole)} allowDeselect={false} w={180} />
                <Button type="submit" leftSection={<IconUserPlus size={16} />} loading={add.isPending}>
                  Ekle
                </Button>
              </Group>
            </form>
          </Card>
        )}
        {error && (
          <Text c="red" role="alert">
            {errorMessage(error)}
          </Text>
        )}
        {members.isPending ? (
          <LoadingState />
        ) : members.isError ? (
          <ErrorState error={members.error} onRetry={() => members.refetch()} />
        ) : (
          <Card withBorder padding={0}>
            <Table.ScrollContainer minWidth={560}>
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Üye</Table.Th>
                    <Table.Th>Rol</Table.Th>
                    <Table.Th>Katıldı</Table.Th>
                    <Table.Th w={50} />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {members.data.map((m) => (
                    <Table.Tr key={m.userId}>
                      <Table.Td>
                        <Text size="sm" fw={500}>
                          {m.displayName} {m.userId === user?.id && <Text span c="dimmed">(siz)</Text>}
                        </Text>
                        <Text size="xs" c="dimmed">
                          {m.email}
                        </Text>
                      </Table.Td>
                      <Table.Td>
                        {manage ? (
                          <Select
                            aria-label={`${m.displayName} rolü`}
                            size="xs"
                            w={170}
                            data={roleOptions}
                            value={m.role}
                            allowDeselect={false}
                            onChange={(v) => v && v !== m.role && change.mutate({ userId: m.userId, role: v as TeamRole })}
                          />
                        ) : (
                          roleLabels[m.role]
                        )}
                      </Table.Td>
                      <Table.Td>{formatDate(m.joinedAt)}</Table.Td>
                      <Table.Td>
                        {(manage || m.userId === user?.id) && (
                          <Tooltip label={m.userId === user?.id ? 'Ekipten ayrıl' : 'Ekipten çıkar'}>
                            <ActionIcon
                              variant="subtle"
                              color="red"
                              aria-label={`${m.displayName} ekipten çıkar`}
                              onClick={() =>
                                modals.openConfirmModal({
                                  title: m.userId === user?.id ? 'Ekipten ayrıl' : 'Üyeyi çıkar',
                                  children: <Text size="sm">{m.displayName} bu ekibin verilerine erişemeyecek.</Text>,
                                  labels: { confirm: 'Onayla', cancel: 'Vazgeç' },
                                  confirmProps: { color: 'red' },
                                  onConfirm: () =>
                                    remove.mutate(m.userId, { onSuccess: () => (m.userId === user?.id ? void reloadProfile() : undefined) }),
                                })
                              }
                            >
                              <IconTrash size={16} />
                            </ActionIcon>
                          </Tooltip>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          </Card>
        )}
        <Text size="xs" c="dimmed">
          İzleyici yalnızca okur. Mühendis log yükler, araç ekler ve analiz başlatır. Proje yöneticisi ayrıca projeleri, üyeleri ve silme işlemlerini yönetir.
        </Text>
      </Stack>
      <Modal opened={creating} onClose={close} title="Yeni ekip">
        <form
          onSubmit={(e) => {
            e.preventDefault();
            createTeam.mutate(teamName.trim(), {
              onSuccess: async (t) => {
                await reloadProfile();
                selectTeam(t.id);
                setTeamName('');
                close();
              },
            });
          }}
        >
          <Stack>
            <TextInput label="Ekip adı" required value={teamName} onChange={(e) => setTeamName(e.currentTarget.value)} maxLength={100} data-autofocus />
            {createTeam.error && <Text c="red" size="sm">{errorMessage(createTeam.error)}</Text>}
            <Group justify="flex-end">
              <Button type="submit" loading={createTeam.isPending} disabled={!teamName.trim()}>
                Oluştur
              </Button>
            </Group>
          </Stack>
        </form>
      </Modal>
    </>
  );
}
