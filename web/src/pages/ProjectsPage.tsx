import { ActionIcon, Anchor, Button, Card, Group, Modal, Stack, Table, Text, TextInput, Textarea, Tooltip } from '@mantine/core';
import { useDebouncedValue, useDisclosure } from '@mantine/hooks';
import { modals } from '@mantine/modals';
import { IconEdit, IconPlus, IconSearch, IconTrash } from '@tabler/icons-react';
import { useState } from 'react';
import { Link } from 'react-router';
import { errorMessage } from '../api/client';
import { useDeleteProject, useProjects, useSaveProject } from '../api/queries';
import type { Project } from '../api/types';
import { useTeam } from '../auth/TeamContext';
import { PageHeader } from '../components/PageHeader';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import { formatDate } from '../lib/format';

function ProjectForm({ teamId, project, onDone }: { teamId: string; project?: Project; onDone: () => void }) {
  const [name, setName] = useState(project?.name ?? '');
  const [description, setDescription] = useState(project?.description ?? '');
  const save = useSaveProject(teamId);
  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        save.mutate({ id: project?.id, name: name.trim(), description: description.trim() || null, version: project?.version }, { onSuccess: onDone });
      }}
    >
      <Stack>
        <TextInput label="Ad" required value={name} onChange={(e) => setName(e.currentTarget.value)} maxLength={100} data-autofocus />
        <Textarea label="Açıklama" value={description} onChange={(e) => setDescription(e.currentTarget.value)} autosize minRows={3} maxLength={2000} />
        {save.error && (
          <Text c="red" size="sm" role="alert">
            {errorMessage(save.error)}
          </Text>
        )}
        <Group justify="flex-end">
          <Button type="submit" loading={save.isPending} disabled={!name.trim()}>
            Kaydet
          </Button>
        </Group>
      </Stack>
    </form>
  );
}

export function ProjectsPage() {
  const { team, can } = useTeam();
  const [search, setSearch] = useState('');
  const [debounced] = useDebouncedValue(search, 300);
  const list = useProjects(team?.teamId, { search: debounced || undefined, pageSize: 100 });
  const remove = useDeleteProject(team?.teamId ?? '');
  const [editing, setEditing] = useState<Project | undefined>();
  const [opened, { open, close }] = useDisclosure();

  const edit = (p?: Project) => {
    setEditing(p);
    open();
  };

  const confirmDelete = (p: Project) =>
    modals.openConfirmModal({
      title: 'Projeyi sil',
      children: <Text size="sm">{p.name} ve projedeki tüm uçuşlar, loglar ve analiz sonuçları kalıcı olarak silinecek.</Text>,
      labels: { confirm: 'Sil', cancel: 'Vazgeç' },
      confirmProps: { color: 'red' },
      onConfirm: () => remove.mutate(p.id),
    });

  return (
    <>
      <PageHeader
        title="Projeler"
        actions={
          can('ProjectManager') && (
            <Button leftSection={<IconPlus size={16} />} onClick={() => edit()}>
              Proje oluştur
            </Button>
          )
        }
      />
      <TextInput mb="md" w={280} placeholder="Proje ara" leftSection={<IconSearch size={14} />} value={search} onChange={(e) => setSearch(e.currentTarget.value)} aria-label="Proje ara" />
      {remove.error && (
        <Text c="red" mb="sm" role="alert">
          {errorMessage(remove.error)}
        </Text>
      )}
      {list.isPending ? (
        <LoadingState />
      ) : list.isError ? (
        <ErrorState error={list.error} onRetry={() => list.refetch()} />
      ) : list.data.items.length === 0 ? (
        <EmptyState title="Proje yok">Uçuşlar projeler altında toplanır. Log yüklemeden önce bir proje oluşturun.</EmptyState>
      ) : (
        <Card withBorder padding={0}>
          <Table.ScrollContainer minWidth={560}>
            <Table highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Proje</Table.Th>
                  <Table.Th>Oluşturuldu</Table.Th>
                  <Table.Th>Güncellendi</Table.Th>
                  <Table.Th w={90} />
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {list.data.items.map((p) => (
                  <Table.Tr key={p.id}>
                    <Table.Td>
                      <Anchor component={Link} to={`/flights?projectId=${p.id}`} fw={500}>
                        {p.name}
                      </Anchor>
                      {p.description && (
                        <Text size="xs" c="dimmed" lineClamp={2}>
                          {p.description}
                        </Text>
                      )}
                    </Table.Td>
                    <Table.Td>{formatDate(p.createdAt)}</Table.Td>
                    <Table.Td>{formatDate(p.updatedAt)}</Table.Td>
                    <Table.Td>
                      {can('ProjectManager') && (
                        <Group gap={4} wrap="nowrap">
                          <Tooltip label="Düzenle">
                            <ActionIcon variant="subtle" onClick={() => edit(p)} aria-label={`${p.name} düzenle`}>
                              <IconEdit size={16} />
                            </ActionIcon>
                          </Tooltip>
                          <Tooltip label="Sil">
                            <ActionIcon variant="subtle" color="red" onClick={() => confirmDelete(p)} aria-label={`${p.name} sil`}>
                              <IconTrash size={16} />
                            </ActionIcon>
                          </Tooltip>
                        </Group>
                      )}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Card>
      )}
      <Modal opened={opened} onClose={close} title={editing ? 'Projeyi düzenle' : 'Proje oluştur'}>
        {team && <ProjectForm key={editing?.id ?? 'new'} teamId={team.teamId} project={editing} onDone={close} />}
      </Modal>
    </>
  );
}
