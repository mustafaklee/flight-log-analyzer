import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import type { Membership, TeamRole } from '../api/types';
import { useAuth } from './AuthContext';

const TEAM_KEY = 'flightlog.teamId';
const ROLE_RANK: Record<TeamRole, number> = { Viewer: 0, Engineer: 1, ProjectManager: 2 };

interface TeamState {
  teams: Membership[];
  team: Membership | null;
  selectTeam(teamId: string): void;
  /** Whether the user has at least this role in the selected team (administrators always). Only hides controls; the API decides. */
  can(role: TeamRole): boolean;
}

const TeamContext = createContext<TeamState | null>(null);

function readStoredTeam(): string | null {
  try {
    return localStorage.getItem(TEAM_KEY);
  } catch {
    return null;
  }
}

export function TeamProvider({ children }: { children: ReactNode }) {
  const { user, isAdministrator } = useAuth();
  const teams = useMemo(() => user?.teams ?? [], [user]);
  const [selected, setSelected] = useState<string | null>(readStoredTeam);

  const team = teams.find((t) => t.teamId === selected) ?? teams[0] ?? null;

  useEffect(() => {
    try {
      if (team) {
        localStorage.setItem(TEAM_KEY, team.teamId);
      }
    } catch {
      // ignore
    }
  }, [team]);

  const value = useMemo<TeamState>(
    () => ({
      teams,
      team,
      selectTeam: setSelected,
      can: (role) => isAdministrator || (team !== null && ROLE_RANK[team.role] >= ROLE_RANK[role]),
    }),
    [teams, team, isAdministrator],
  );

  return <TeamContext.Provider value={value}>{children}</TeamContext.Provider>;
}

export function useTeam(): TeamState {
  const value = useContext(TeamContext);
  if (!value) {
    throw new Error('useTeam must be used inside TeamProvider');
  }

  return value;
}
