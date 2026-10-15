import { ActionIcon, AppShell, Burger, Group, Indicator, Menu, NavLink, ScrollArea, Select, Text, Title, Tooltip, useComputedColorScheme, useMantineColorScheme } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import {
  IconChartLine,
  IconDrone,
  IconFolders,
  IconLayoutDashboard,
  IconLogout,
  IconMoon,
  IconSettings,
  IconSun,
  IconUpload,
  IconUser,
  IconUsersGroup,
} from '@tabler/icons-react';
import { NavLink as RouterLink, Outlet, useLocation } from 'react-router';
import { useRealtime } from '../api/realtime';
import { useAuth } from '../auth/AuthContext';
import { useTeam } from '../auth/TeamContext';
import { roleLabels } from '../lib/labels';

const links = [
  { to: '/', label: 'Dashboard', icon: IconLayoutDashboard, end: true },
  { to: '/flights', label: 'Uçuş geçmişi', icon: IconChartLine },
  { to: '/upload', label: 'Log yükleme', icon: IconUpload },
  { to: '/fleet', label: 'Filo', icon: IconDrone },
  { to: '/projects', label: 'Projeler', icon: IconFolders },
  { to: '/team', label: 'Ekip', icon: IconUsersGroup },
];

const realtimeLabels = {
  connected: 'Canlı güncellemeler açık',
  connecting: 'Canlı güncellemelere bağlanılıyor',
  reconnecting: 'Bağlantı yeniden kuruluyor',
  disconnected: 'Canlı güncelleme yok; veriler aralıklarla yenileniyor',
} as const;

export function Layout() {
  const [opened, { toggle, close }] = useDisclosure();
  const { user, logout, isAdministrator } = useAuth();
  const { teams, team, selectTeam } = useTeam();
  const realtime = useRealtime(true);
  const location = useLocation();
  const { setColorScheme } = useMantineColorScheme();
  const scheme = useComputedColorScheme('light');

  return (
    <AppShell header={{ height: 56 }} navbar={{ width: 240, breakpoint: 'sm', collapsed: { mobile: !opened } }} padding="md">
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between" wrap="nowrap">
          <Group gap="sm" wrap="nowrap">
            <Burger opened={opened} onClick={toggle} hiddenFrom="sm" size="sm" aria-label="Menüyü aç" />
            <Title order={4} style={{ whiteSpace: 'nowrap' }}>
              FlightLog
            </Title>
            {teams.length > 0 && (
              <Select
                aria-label="Ekip"
                size="xs"
                w={200}
                data={teams.map((t) => ({ value: t.teamId, label: t.teamName }))}
                value={team?.teamId ?? null}
                onChange={(v) => v && selectTeam(v)}
                allowDeselect={false}
                visibleFrom="xs"
              />
            )}
          </Group>
          <Group gap="xs" wrap="nowrap">
            <Tooltip label={realtimeLabels[realtime]}>
              <Indicator color={realtime === 'connected' ? 'green' : realtime === 'disconnected' ? 'gray' : 'yellow'} size={10} processing={realtime !== 'connected' && realtime !== 'disconnected'}>
                <Text size="xs" c="dimmed" aria-label={realtimeLabels[realtime]}>
                  Canlı
                </Text>
              </Indicator>
            </Tooltip>
            <ActionIcon
              variant="subtle"
              aria-label={scheme === 'dark' ? 'Açık temaya geç' : 'Koyu temaya geç'}
              onClick={() => setColorScheme(scheme === 'dark' ? 'light' : 'dark')}
            >
              {scheme === 'dark' ? <IconSun size={18} /> : <IconMoon size={18} />}
            </ActionIcon>
            <Menu position="bottom-end">
              <Menu.Target>
                <ActionIcon variant="subtle" aria-label="Hesap">
                  <IconUser size={18} />
                </ActionIcon>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Label>
                  {user?.displayName}
                  <br />
                  {user?.email}
                </Menu.Label>
                {team && <Menu.Label>Rol: {roleLabels[team.role]}</Menu.Label>}
                <Menu.Divider />
                <Menu.Item leftSection={<IconLogout size={16} />} onClick={logout}>
                  Çıkış yap
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          </Group>
        </Group>
      </AppShell.Header>
      <AppShell.Navbar p="xs">
        <AppShell.Section grow component={ScrollArea}>
          {teams.length > 0 && (
            <Select
              hiddenFrom="xs"
              mb="sm"
              aria-label="Ekip"
              data={teams.map((t) => ({ value: t.teamId, label: t.teamName }))}
              value={team?.teamId ?? null}
              onChange={(v) => v && selectTeam(v)}
              allowDeselect={false}
            />
          )}
          {links.map((link) => (
            <NavLink
              key={link.to}
              component={RouterLink}
              to={link.to}
              end={link.end}
              label={link.label}
              leftSection={<link.icon size={18} stroke={1.6} />}
              active={link.end ? location.pathname === link.to : location.pathname.startsWith(link.to)}
              onClick={close}
            />
          ))}
          {isAdministrator && (
            <NavLink
              component={RouterLink}
              to="/admin"
              label="Yönetim"
              leftSection={<IconSettings size={18} stroke={1.6} />}
              active={location.pathname.startsWith('/admin')}
              onClick={close}
            />
          )}
        </AppShell.Section>
      </AppShell.Navbar>
      <AppShell.Main>
        <Outlet />
      </AppShell.Main>
    </AppShell>
  );
}
