import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, type Query } from './client';
import type {
  AdminUser,
  AiAnalysis,
  AiStatus,
  AiUsage,
  Aircraft,
  AnalysisRun,
  AuditEntry,
  Dashboard,
  Finding,
  Flight,
  FlightEvent,
  FlightComment,
  FlightComparison,
  FindingReview,
  FlightListItem,
  FlightParameter,
  LogFile,
  Paged,
  ProcessingJob,
  ProcessingStats,
  Project,
  ReviewListItem,
  RuleDescriptor,
  SaveAircraftRequest,
  Signal,
  SignalData,
  TagCount,
  Team,
  TeamMember,
  TeamRole,
  UpdateFindingReviewRequest,
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
  aiAnalyses: (flightId: string) => ['flight', flightId, 'ai-analyses'] as const,
  aiAnalysis: (id: string) => ['ai-analysis', id] as const,
  aiStatus: (teamId: string) => ['ai-status', teamId] as const,
  aiUsage: (days: number) => ['ai-usage', days] as const,
  comments: (flightId: string) => ['flight', flightId, 'comments'] as const,
  activity: (flightId: string, query: Query) => ['flight', flightId, 'activity', query] as const,
  history: (findingId: string) => ['finding-history', findingId] as const,
  reviews: (teamId: string, query: Query) => ['finding-reviews', teamId, query] as const,
  auditLog: (teamId: string, query: Query) => ['audit-log', teamId, query] as const,
  tags: (teamId: string) => ['flight-tags', teamId] as const,
  comparison: (ids: string[]) => ['comparison', ids] as const,
};

/** Query string with a repeated parameter (?ids=a&ids=b), which the compare endpoints bind as a list. */
export function repeatedQuery(name: string, values: string[], extra: Record<string, string> = {}): string {
  const params = new URLSearchParams();
  values.forEach((v) => params.append(name, v));
  Object.entries(extra).forEach(([k, v]) => params.set(k, v));
  return `?${params.toString()}`;
}

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

const isAiPending = (a: AiAnalysis | undefined) => a?.status === 'Queued' || a?.status === 'Running';

/** AI analyses of a flight, newest first, without report bodies. */
export function useAiAnalyses(flightId: string) {
  return useQuery({
    queryKey: keys.aiAnalyses(flightId),
    queryFn: ({ signal }) => api<AiAnalysis[]>(`/flights/${flightId}/ai-analyses`, { signal }),
    refetchInterval: (q) => (q.state.data?.some(isAiPending) ? FALLBACK_POLL_MS : false),
  });
}

export function useAiAnalysis(id: string | undefined) {
  return useQuery({
    queryKey: keys.aiAnalysis(id ?? ''),
    queryFn: ({ signal }) => api<AiAnalysis>(`/ai-analyses/${id}`, { signal }),
    enabled: !!id,
    refetchInterval: (q) => (isAiPending(q.state.data) ? FALLBACK_POLL_MS : false),
  });
}

export function useAiStatus(teamId: string | undefined) {
  return useQuery({
    queryKey: keys.aiStatus(teamId ?? ''),
    queryFn: ({ signal }) => api<AiStatus>(`/teams/${teamId}/ai-status`, { signal }),
    enabled: !!teamId,
  });
}

export function useAiUsage(days: number) {
  return useQuery({
    queryKey: keys.aiUsage(days),
    queryFn: ({ signal }) => api<AiUsage>('/admin/ai/usage', { query: { days }, signal }),
    refetchInterval: 60_000,
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

export function useRequestAiAnalysis(flightId: string, teamId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => api<AiAnalysis>(`/flights/${flightId}/ai-analyses`, { method: 'POST' }),
    onSettled: () => {
      client.invalidateQueries({ queryKey: keys.aiAnalyses(flightId) });
      client.invalidateQueries({ queryKey: keys.aiStatus(teamId) });
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

// ---- Collaboration (Stage 7) -------------------------------------------------------------------------------------

/** All comments and notes of a flight, oldest first, including those on findings. */
export function useComments(flightId: string) {
  return useQuery({
    queryKey: keys.comments(flightId),
    queryFn: ({ signal }) => api<FlightComment[]>(`/flights/${flightId}/comments`, { signal }),
  });
}

export function useAddComment(flightId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: { body: string; startUs?: number | null; endUs?: number | null; findingId?: string | null }) =>
      api<FlightComment>(`/flights/${flightId}/comments`, { method: 'POST', body }),
    onSuccess: () => {
      client.invalidateQueries({ queryKey: keys.comments(flightId) });
      client.invalidateQueries({ queryKey: ['analysis-run'] });
      client.invalidateQueries({ queryKey: ['finding-history'] });
    },
  });
}

export function useEditComment(flightId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ id, body, version }: { id: string; body: string; version: number }) =>
      api<FlightComment>(`/comments/${id}`, { method: 'PUT', body: { body, version } }),
    onSuccess: () => client.invalidateQueries({ queryKey: keys.comments(flightId) }),
  });
}

export function useDeleteComment(flightId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api<void>(`/comments/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      client.invalidateQueries({ queryKey: keys.comments(flightId) });
      client.invalidateQueries({ queryKey: ['analysis-run'] });
    },
  });
}

/** Changes the review of a finding; the findings of the run are refreshed so lists show the new state. */
export function useUpdateReview(runId: string | undefined) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ findingId, body }: { findingId: string; body: Partial<UpdateFindingReviewRequest> }) =>
      api<FindingReview>(`/findings/${findingId}/review`, { method: 'PATCH', body }),
    onSuccess: (_, { findingId }) => {
      if (runId) {
        client.invalidateQueries({ queryKey: keys.findings(runId) });
      }

      client.invalidateQueries({ queryKey: keys.history(findingId) });
      client.invalidateQueries({ queryKey: ['finding-reviews'] });
      client.invalidateQueries({ queryKey: ['flight'] });
    },
  });
}

/** Who changed the review of a finding and when, with its comments; newest first. */
export function useFindingHistory(findingId: string | undefined) {
  return useQuery({
    queryKey: keys.history(findingId ?? ''),
    queryFn: ({ signal }) => api<AuditEntry[]>(`/findings/${findingId}/history`, { signal }),
    enabled: !!findingId,
  });
}

export function useFlightActivity(flightId: string, query: Query) {
  return useQuery({
    queryKey: keys.activity(flightId, query),
    queryFn: ({ signal }) => api<Paged<AuditEntry>>(`/flights/${flightId}/activity`, { query, signal }),
    placeholderData: keepPreviousData,
  });
}

export function useTeamAuditLog(teamId: string | undefined, query: Query) {
  return useQuery({
    queryKey: keys.auditLog(teamId ?? '', query),
    queryFn: ({ signal }) => api<Paged<AuditEntry>>(`/teams/${teamId}/audit-log`, { query, signal }),
    enabled: !!teamId,
    placeholderData: keepPreviousData,
  });
}

export function useTeamReviews(teamId: string | undefined, query: Query) {
  return useQuery({
    queryKey: keys.reviews(teamId ?? '', query),
    queryFn: ({ signal }) => api<Paged<ReviewListItem>>(`/teams/${teamId}/finding-reviews`, { query, signal }),
    enabled: !!teamId,
    placeholderData: keepPreviousData,
  });
}

export function useFlightTags(teamId: string | undefined) {
  return useQuery({
    queryKey: keys.tags(teamId ?? ''),
    queryFn: ({ signal }) => api<TagCount[]>(`/teams/${teamId}/flight-tags`, { signal }),
    enabled: !!teamId,
  });
}

export function useSetFlightTags(flightId: string, teamId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (tags: string[]) => api<Flight>(`/flights/${flightId}/tags`, { method: 'PUT', body: { tags } }),
    onSuccess: (flight) => {
      client.setQueryData(keys.flight(flight.id), flight);
      client.invalidateQueries({ queryKey: ['flights'] });
      client.invalidateQueries({ queryKey: keys.tags(teamId) });
    },
  });
}

export function useComparison(ids: string[]) {
  return useQuery({
    queryKey: keys.comparison(ids),
    queryFn: ({ signal }) => api<FlightComparison>(`/flights/compare${repeatedQuery('ids', ids)}`, { signal }),
    enabled: ids.length >= 2,
    staleTime: 60_000,
  });
}

/** Audit entries of all teams (administrators). */
export function useAdminAuditLog(query: Query) {
  return useQuery({
    queryKey: ['admin-audit-log', query],
    queryFn: ({ signal }) => api<Paged<AuditEntry>>('/admin/audit-log', { query, signal }),
    placeholderData: keepPreviousData,
  });
}
