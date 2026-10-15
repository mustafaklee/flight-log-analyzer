// Names for the DTOs of the API. The shapes come from the generated schema (npm run api:types); do not edit them here.
import type { components } from './schema';

type Schemas = components['schemas'];

export type AccessTokenResponse = Schemas['AccessTokenResponse'];
export type UserProfile = Schemas['UserProfileDto'];
export type Membership = Schemas['MembershipDto'];
export type Team = Schemas['TeamDto'];
export type TeamMember = Schemas['TeamMemberDto'];
export type TeamRole = Schemas['TeamRole'];
export type Project = Schemas['ProjectDto'];
export type Aircraft = Schemas['AircraftDto'];
export type AircraftType = Schemas['AircraftType'];
export type SaveAircraftRequest = Schemas['SaveAircraftRequest'];
export type Autopilot = Schemas['Autopilot'];
export type Flight = Schemas['FlightDto'];
export type FlightListItem = Schemas['FlightListItemDto'];
export type FlightSort = Schemas['FlightSort'];
export type LogFile = Schemas['LogFileDto'];
export type ProcessingStatus = Schemas['ProcessingStatus'];
export type ProcessingJob = Schemas['ProcessingJobDto'];
export type UploadLogResult = Schemas['UploadLogResult'];
export type Signal = Schemas['SignalDto'];
export type SignalData = Schemas['SignalDataDto'];
export type SignalSeriesData = Schemas['SignalSeriesDataDto'];
export type FlightEvent = Schemas['FlightEventDto'];
export type FlightEventKind = Schemas['FlightEventKind'];
export type EventSeverity = Schemas['EventSeverity'];
export type FlightParameter = Schemas['FlightParameterDto'];
export type AnalysisRun = Schemas['AnalysisRunDto'];
export type AnalysisSummary = Schemas['AnalysisSummaryDto'];
export type RuleResult = Schemas['RuleResultDto'];
export type RuleDescriptor = Schemas['RuleDescriptorDto'];
export type Finding = Schemas['FindingDto'];
export type FindingSeverity = Schemas['FindingSeverity'];
export type FindingConfidence = Schemas['FindingConfidence'];
export type Dashboard = Schemas['DashboardDto'];
export type DashboardFinding = Schemas['DashboardFindingDto'];
export type AdminUser = Schemas['AdminUserDto'];
export type ProcessingStats = Schemas['ProcessingStatsDto'];
export type DurationStats = Schemas['DurationStats'];
export type AiAnalysis = Schemas['AiAnalysisDto'];
export type AiAnalysisStatus = Schemas['AiAnalysisStatus'];
export type AiReport = Schemas['AiReportDto'];
export type AiReportItem = Schemas['AiReportItemDto'];
export type AiEvidence = Schemas['AiEvidenceDto'];
export type AiStatus = Schemas['AiStatusDto'];
export type AiUsage = Schemas['AiUsageDto'];

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

/** RFC 9457 problem details with the API's stable error code. */
export interface Problem {
  type?: string | null;
  title?: string | null;
  status?: number | null;
  detail?: string | null;
  code?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
  /** Extra fields of some errors, e.g. flightId of log_file.duplicate. */
  [extension: string]: unknown;
}

/** A status change pushed by the processing hub (ADR-0011). */
export interface StatusNotification {
  kind: 'logFile' | 'analysisRun' | 'aiAnalysis';
  teamId: string;
  flightId: string;
  id: string;
  status: string;
  failureCode?: string | null;
}
