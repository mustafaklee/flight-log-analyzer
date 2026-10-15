import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, type Query } from './client';
import type {
  AdminUser,
  Aircraft,
  AnalysisRun,
  Dashboard,
  Finding,
  Flight,
  FlightEvent,
  FlightListItem,
  FlightParameter,
  LogFile,
  Paged,
  ProcessingJob,
  ProcessingStats,
  Project,
  RuleDescriptor,
  SaveAircraftRequest,
  Signal,
  SignalData,
  Team,
  TeamMember,
  TeamRole,
} from './types';

/** Query keys; the realtime connection invalidates by these prefixes. */
export const keys = {
  dashboard: (teamId: string) => ['dashboard', teamId] as const,
  teams: ['teams'] as const,
  members: (teamId: string) => ['teams', teamId, 'members'] as const,
  projects: (teamId: string, query?: Query) => ['projects', teamId, query ?? {}] as const,
  aircraft: (teamId: string, query?: Query) => ['aircraft', teamId, query ?? {}] as const,
  aircraftItem: (id: string) => ['aircraft-item', id] as const,
  flights: (query: Query) => ['flights', query] as const,
  flight: (id: string) => ['flight', id] as const,
  signals: (flightId: string) => ['flight', flightId, 'signals'] as const,
  signalData: (flightId: string, names: string[], fromUs?: number, toUs?: number, maxPoints?: number) =>
    ['flight', flightId, 'signal-data', names, fromUs ?? null, toUs ?? null, maxPoints ?? null] as const,
  events: (flightId: string, query: Query) => ['flight', flightId, 'events', query] as const,
  parameters: (flightId: string) => ['flight', flightId, 'parameters'] as const,
  runs: (flightId: string) => ['flight', flightId, 'analysis-runs'] as const,
  run: (runId: string) => ['analysis-run', runId] as const,
  findings: (runId: string) => ['analysis-run', runId, 'findings'] as const,
  rules: ['analysis-rules'] as const,
  logFile: (id: string) => ['log-file', id] as const,
  jobs: (logFileId: string) => ['log-file', logFileId, 'jobs'] as const,
  adminUsers: (query: Query) => ['admin-users', query] as const,
  stats: (hours: number) => ['processing-stats', hours] as const,
};

const ACTIVE_STATUSES = new Set(['Uploaded', 'Validating', 'Queued', 'Processing', 'Processed', 'AnalysisPending', 'Analyzing']);

/** Whether a file is still moving through processing or analysis. */
export function isActive(status: string | null | undefined): boolean {
  return status != null && ACTIVE_STATUSES.has(status);
}

// Realtime notifications refresh active work; this slow poll only covers a lost connection (ADR-0011).
const FALLBACK_POLL_MS = 15_000;

export function useDashboard(teamId: string | undefined) {
  return useQuery({
    queryKey: keys.dashboard(teamId ?? ''),
    queryFn: ({ signal }) => api<Dashboard>(`/teams/${teamId}/dashboard`, { signal }),
    enabled: !!teamId,
  });
}

export function useTeams() {
  return useQuery({ queryKey: keys.teams, queryFn: ({ signal }) => api<Team[]>('/teams', { signal }) });
}

export function useMembers(teamId: string | undefined) {
  return useQuery({
    queryKey: keys.members(teamId ?? ''),
    queryFn: ({ signal }) => api<TeamMember[]>(`/teams/${teamId}/members`, { signal }),
    enabled: !!teamId,
  });
}

export function useProjects(teamId: string | undefined, query: Query = { pageSize: 100 }) {
  return useQuery({
    queryKey: keys.projects(teamId ?? '', query),
    queryFn: ({ signal }) => api<Paged<Project>>(`/teams/${teamId}/projects`, { query, signal }),
    enabled: !!teamId,
    placeholderData: keepPreviousData,
  });
}

export function useAircraftList(teamId: string | undefined, query: Query = { pageSize: 100 }) {
  return useQuery({
    queryKey: keys.aircraft(teamId ?? '', query),
    queryFn: ({ signal }) => api<Paged<Aircraft>>(`/teams/${teamId}/aircraft`, { query, signal }),
    enabled: !!teamId,
    placeholderData: keepPreviousData,
  });
}

export function useAircraft(id: string | undefined) {
  return useQuery({
    queryKey: keys.aircraftItem(id ?? ''),
    queryFn: ({ signal }) => api<Aircraft>(`/aircraft/${id}`, { signal }),
    enabled: !!id,
  });
}

export function useFlights(query: Query, enabled = true) {
  return useQuery({
    queryKey: keys.flights(query),
    queryFn: ({ signal }) => api<Paged<FlightListItem>>('/flights', { query, signal }),
    enabled,
    placeholderData: keepPreviousData,
    refetchInterval: (q) => (q.state.data?.items.some((f) => isActive(f.processingStatus)) ? FALLBACK_POLL_MS : false),
  });
}

export function useFlight(id: string | undefined) {
  return useQuery({
    queryKey: keys.flight(id ?? ''),
    queryFn: ({ signal }) => api<Flight>(`/flights/${id}`, { signal }),
    enabled: !!id,
    refetchInterval: (q) => (isActive(q.state.data?.logFile?.status) ? FALLBACK_POLL_MS : false),
  });
}

export function useSignals(flightId: string | undefined, enabled = true) {
  return useQuery({
    queryKey: keys.signals(flightId ?? ''),
    queryFn: ({ signal }) => api<Signal[]>(`/flights/${flightId}/signals`, { signal }),
    enabled: !!flightId && enabled,
    staleTime: 5 * 60_000,
  });
}

/** Values of up to 8 signals over a window, reduced to about maxPoints per signal by the API. */
export function useSignalData(flightId: string, names: string[], window: { fromUs?: number; toUs?: number }, maxPoints: number) {
  return useQuery({
    queryKey: keys.signalData(flightId, names, window.fromUs, window.toUs, maxPoints),
    queryFn: ({ signal }) =>
      api<SignalData>(`/flights/${flightId}/signals/data`, { query: { names, fromUs: window.fromUs, toUs: window.toUs, maxPoints }, signal }),
    enabled: names.length > 0,
    placeholderData: keepPreviousData,
    staleTime: 5 * 60_000,
  });
}

const EVENT_PAGE = 1000;

/** All events of a flight matching the filter, loaded page by page. */
export function useEvents(flightId: string, query: Query, enabled = true) {
  return useInfiniteQuery({
    queryKey: keys.events(flightId, query),
    queryFn: ({ pageParam, signal }) =>
      api<Paged<FlightEvent>>(`/flights/${flightId}/events`, { query: { ...query, page: pageParam, pageSize: EVENT_PAGE }, signal }),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined),
    enabled,
    staleTime: 5 * 60_000,
  });
}

export function useParameters(flightId: string, enabled = true) {
  return useQuery({
    queryKey: keys.parameters(flightId),
    queryFn: ({ signal }) => api<FlightParameter[]>(`/flights/${flightId}/parameters`, { signal }),
    enabled,
    staleTime: 5 * 60_000,
  });
}

export function useAnalysisRuns(flightId: string) {
  return useQuery({
    queryKey: keys.runs(flightId),
    queryFn: ({ signal }) => api<AnalysisRun[]>(`/flights/${flightId}/analysis-runs`, { signal }),
    refetchInterval: (q) => (q.state.data?.some((r) => r.status === 'Queued' || r.status === 'Running') ? FALLBACK_POLL_MS : false),
  });
}

export function useAnalysisRun(runId: string | undefined) {
  return useQuery({
    queryKey: keys.run(runId ?? ''),
    queryFn: ({ signal }) => api<AnalysisRun>(`/analysis-runs/${runId}`, { signal }),
    enabled: !!runId,
  });
}

export function useFindings(runId: string | undefined) {
  return useQuery({
    queryKey: keys.findings(runId ?? ''),
    queryFn: async ({ signal }) => {
      // Findings are capped per rule; a run has at most a few hundred in practice.
      const all: Finding[] = [];
      for (let page = 1; ; page++) {
        const result = await api<Paged<Finding>>(`/analysis-runs/${runId}/findings`, { query: { page, pageSize: 200 }, signal });
        all.push(...result.items);
        if (page >= result.totalPages) {
          return all;
        }
      }
    },
    enabled: !!runId,
    staleTime: Infinity,
  });
}

export function useRules() {
  return useQuery({ queryKey: keys.rules, queryFn: ({ signal }) => api<RuleDescriptor[]>('/analysis-rules', { signal }), staleTime: Infinity });
}

export function useLogFile(id: string | undefined) {
  return useQuery({
    queryKey: keys.logFile(id ?? ''),
    queryFn: ({ signal }) => api<LogFile>(`/log-files/${id}`, { signal }),
    enabled: !!id,
    refetchInterval: (q) => (isActive(q.state.data?.status) ? FALLBACK_POLL_MS : false),
  });
}

export function useJobs(logFileId: string | undefined) {
  return useQuery({
    queryKey: keys.jobs(logFileId ?? ''),
    queryFn: ({ signal }) => api<ProcessingJob[]>(`/log-files/${logFileId}/jobs`, { signal }),
    enabled: !!logFileId,
  });
}

export function useAdminUsers(query: Query) {
  return useQuery({
    queryKey: keys.adminUsers(query),
    queryFn: ({ signal }) => api<Paged<AdminUser>>('/admin/users', { query, signal }),
    placeholderData: keepPreviousData,
  });
}

export function useProcessingStats(hours: number) {
  return useQuery({
    queryKey: keys.stats(hours),
    queryFn: ({ signal }) => api<ProcessingStats>('/admin/processing/stats', { query: { hours }, signal }),
    refetchInterval: 30_000,
  });
}

// ---- Mutations -------------------------------------------------------------------------------------------------

export function useCreateTeam() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => api<Team>('/teams', { method: 'POST', body: { name } }),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.teams }),
  });
}

export function useAddMember(teamId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: { email: string; role: TeamRole }) => api<TeamMember>(`/teams/${teamId}/members`, { method: 'POST', body }),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.members(teamId) }),
  });
}

export function useChangeMemberRole(teamId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ userId, role }: { userId: string; role: TeamRole }) => api<TeamMember>(`/teams/${teamId}/members/${userId}`, { method: 'PUT', body: { role } }),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.members(teamId) }),
  });
}

export function useRemoveMember(teamId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (userId: string) => api<void>(`/teams/${teamId}/members/${userId}`, { method: 'DELETE' }),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.members(teamId) }),
  });
}

export function useSaveProject(teamId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (p: { id?: string; name: string; description: string | null; version?: number }) =>
      p.id
        ? api<Project>(`/projects/${p.id}`, { method: 'PUT', body: { name: p.name, description: p.description, version: p.version } })
        : api<Project>(`/teams/${teamId}/projects`, { method: 'POST', body: { name: p.name, description: p.description } }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['projects', teamId] }),
  });
}

export function useDeleteProject(teamId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api<void>(`/projects/${id}`, { method: 'DELETE' }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['projects', teamId] }),
  });
}

export function useSaveAircraft(teamId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, body }: { id?: string; body: SaveAircraftRequest }) =>
      id ? api<Aircraft>(`/aircraft/${id}`, { method: 'PUT', body }) : api<Aircraft>(`/teams/${teamId}/aircraft`, { method: 'POST', body }),
    onSuccess: (saved) => {
      client.invalidateQueries({ queryKey: ['aircraft', teamId] });
      client.setQueryData(keys.aircraftItem(saved.id), saved);
    },
  });
}

export function useDeleteAircraft(teamId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api<void>(`/aircraft/${id}`, { method: 'DELETE' }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['aircraft', teamId] }),
  });
}

export function useUpdateFlight() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: { name: string; notes: string | null; aircraftId: string | null; version: number } }) =>
      api<Flight>(`/flights/${id}`, { method: 'PUT', body }),
    onSuccess: (flight) => {
      client.setQueryData(keys.flight(flight.id), flight);
      client.invalidateQueries({ queryKey: ['flights'] });
    },
  });
}

export function useDeleteFlight() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api<void>(`/flights/${id}`, { method: 'DELETE' }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['flights'] }),
  });
}

export function useLogFileAction() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, action }: { id: string; action: 'cancel' | 'retry' }) => api<LogFile>(`/log-files/${id}/${action}`, { method: 'POST' }),
    onSuccess: (file) => {
      client.setQueryData(keys.logFile(file.id), file);
      client.invalidateQueries({ queryKey: keys.flight(file.flightId) });
      client.invalidateQueries({ queryKey: keys.jobs(file.id) });
      client.invalidateQueries({ queryKey: ['flights'] });
    },
  });
}

export function useRequestAnalysis(flightId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => api<AnalysisRun>(`/flights/${flightId}/analysis-runs`, { method: 'POST' }),
    onSuccess: () => {
      client.invalidateQueries({ queryKey: keys.runs(flightId) });
      client.invalidateQueries({ queryKey: keys.flight(flightId) });
    },
  });
}

export function useCreateUser() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: { email: string; displayName: string; password: string; isAdministrator: boolean }) =>
      api<AdminUser>('/admin/users', { method: 'POST', body }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['admin-users'] }),
  });
}

export function useSetUserDisabled() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, disabled }: { id: string; disabled: boolean }) =>
      api<AdminUser>(`/admin/users/${id}/${disabled ? 'disable' : 'enable'}`, { method: 'POST' }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['admin-users'] }),
  });
}
