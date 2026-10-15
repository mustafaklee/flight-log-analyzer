import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Finding, FlightComment, FlightComparison } from '../api/types';
import { ComparisonView } from '../pages/ComparePage';
import { mockApi, renderWithApp } from '../test/render';
import { CommentsPanel } from './CommentsPanel';
import { FindingReviewPanel } from './FindingReviewPanel';

afterEach(() => vi.unstubAllGlobals());

const profile = (role: 'Viewer' | 'Engineer' | 'ProjectManager') => ({
  id: 'u1', email: 'e@x', displayName: 'Engin', roles: [], teams: [{ teamId: 't1', teamName: 'Team', role }],
});

const comment = (over: Partial<FlightComment>): FlightComment => ({
  id: 'c1', flightId: 'f1', findingReviewId: null, author: { id: 'u1', displayName: 'Engin' }, body: 'GPS düşüyor', startUs: 2_000_000, endUs: 4_000_000,
  createdAt: '2026-10-15T10:00:00Z', editedAt: null, deleted: false, canEdit: true, canDelete: true, version: 3, ...over,
});

/** Signs in with a refresh token so the providers load the profile with the given role. */
function signedIn(role: 'Viewer' | 'Engineer' | 'ProjectManager', handler: Parameters<typeof mockApi>[0]) {
  localStorage.setItem('flightlog.refreshToken', 'r');
  return mockApi((url, init) => {
    if (url.pathname === '/api/v1/auth/refresh') return { body: { accessToken: 'a', expiresIn: 3600, refreshToken: 'r', tokenType: 'Bearer' } };
    if (url.pathname === '/api/v1/auth/me') return { body: profile(role) };
    return handler(url, init);
  });
}

describe('CommentsPanel', () => {
  it('adds a note on the range shown in the charts and shows it on the chart', async () => {
    let posted: unknown;
    const onShow = vi.fn();
    signedIn('Engineer', (url, init) => {
      if (url.pathname === '/api/v1/flights/f1/comments' && init?.method === 'POST') {
        posted = JSON.parse(String(init.body));
        return { status: 201, body: comment({ id: 'c2' }) };
      }

      if (url.pathname === '/api/v1/flights/f1/comments') return { body: [comment({}), comment({ id: 'c3', deleted: true, body: null, canEdit: false, canDelete: false, startUs: null, endUs: null })] };
      return undefined;
    });

    renderWithApp(<CommentsPanel flightId="f1" logFromUs={1_000_000} window={{ fromUs: 3_000_000, toUs: 5_000_000 }} onShowRange={onShow} />);

    const items = await screen.findAllByTestId('comment');
    expect(within(items[0]!).getByText('GPS düşüyor')).toBeInTheDocument();
    expect(within(items[0]!).getByText('1.000 s – 3.000 s')).toBeInTheDocument();
    expect(within(items[1]!).getByText('Bu yorum silindi.')).toBeInTheDocument();
    await userEvent.click(within(items[0]!).getByRole('button', { name: 'Grafikte göster' }));
    expect(onShow).toHaveBeenCalledWith(2_000_000, 4_000_000);

    await userEvent.type(await screen.findByLabelText('Yeni yorum'), 'Anten kontrol edilecek');
    await userEvent.click(screen.getByLabelText(/Grafikteki aralığa bağla/));
    await userEvent.click(screen.getByRole('button', { name: 'Ekle' }));
    await waitFor(() => expect(posted).toEqual({ body: 'Anten kontrol edilecek', startUs: 3_000_000, endUs: 5_000_000 }));
  });

  it('is read-only for viewers', async () => {
    signedIn('Viewer', (url) =>
      url.pathname === '/api/v1/flights/f1/comments' ? { body: [comment({ canEdit: false, canDelete: false })] } : undefined,
    );
    renderWithApp(<CommentsPanel flightId="f1" logFromUs={0} window={null} onShowRange={() => {}} />);
    const item = await screen.findByTestId('comment');
    expect(within(item).queryByRole('button', { name: 'Sil' })).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Yeni yorum')).not.toBeInTheDocument();
  });
});

const finding: Finding = {
  id: 'fd1', runId: 'run-1', ruleId: 'data.gaps', ruleVersion: 1, category: 'data', title: 'Veri boşluğu', description: '', severity: 'Warning',
  confidence: 'High', startUs: 5_000_000, endUs: 6_000_000, signals: [], measured: {}, threshold: {}, thresholdSource: 'heuristic', dataCoverage: null,
  occurrenceCount: 1, limitations: null,
  review: { id: 'rv1', status: 'Investigating', assignee: { id: 'u1', displayName: 'Engin' }, flagged: true, updatedBy: { id: 'u2', displayName: 'Pam' }, updatedAt: '2026-10-15T11:00:00Z', commentCount: 1 },
};

describe('FindingReviewPanel', () => {
  it('changes the review status and shows the history in Turkish', async () => {
    let patched: unknown;
    signedIn('Engineer', (url, init) => {
      if (url.pathname === '/api/v1/findings/fd1/review') {
        patched = JSON.parse(String(init!.body));
        return { body: { ...finding.review, status: 'Confirmed' } };
      }

      if (url.pathname === '/api/v1/teams/t1/members') return { body: [{ userId: 'u1', email: 'e', displayName: 'Engin', role: 'Engineer', joinedAt: '' }, { userId: 'u3', email: 'v', displayName: 'Vera', role: 'Viewer', joinedAt: '' }] };
      if (url.pathname === '/api/v1/flights/f1/comments') return { body: [comment({ findingReviewId: 'rv1', body: 'SD kart?', startUs: null, endUs: null })] };
      if (url.pathname === '/api/v1/findings/fd1/history')
        return {
          body: [
            { id: 'h1', action: 'finding.status_changed', entityType: 'FindingReview', entityId: 'rv1', flightId: 'f1', actor: { id: 'u2', displayName: 'Pam' }, summary: null, data: { from: 'Open', to: 'Investigating' }, correlationId: 'c', createdAt: '2026-10-15T11:00:00Z' },
            { id: 'h2', action: 'finding.assigned', entityType: 'FindingReview', entityId: 'rv1', flightId: 'f1', actor: { id: 'u2', displayName: 'Pam' }, summary: null, data: { fromName: null, toName: 'Engin' }, correlationId: 'c', createdAt: '2026-10-15T10:59:00Z' },
          ],
        };
      return undefined;
    });

    renderWithApp(<FindingReviewPanel finding={finding} flightId="f1" teamId="t1" logFromUs={0} />);

    expect(await screen.findByText('SD kart?')).toBeInTheDocument();
    const history = await screen.findByTestId('review-history');
    expect(within(history).getByText(/Açık → İnceleniyor/)).toBeInTheDocument();
    expect(within(history).getByText(/atanmamış → Engin/)).toBeInTheDocument();

    await userEvent.click(await screen.findByRole('textbox', { name: 'Durum' }));
    await userEvent.click(await screen.findByRole('option', { name: 'Doğrulandı', hidden: true }));
    await waitFor(() => expect(patched).toEqual({ status: 'Confirmed' }));

    await userEvent.click(screen.getByRole('textbox', { name: 'Atanan' }));
    expect(await screen.findByRole('option', { name: 'Engin', hidden: true })).toBeInTheDocument();
    expect(screen.queryByRole('option', { name: 'Vera', hidden: true })).not.toBeInTheDocument();
  });
});

describe('ComparisonView', () => {
  it('shows notes, values per flight and missing data', async () => {
    const flight = (id: string, name: string, autopilot: 'Px4' | 'ArduPilot') => ({
      id, name, projectName: 'P', aircraftName: null, autopilot, firmwareVersion: '1', vehicleType: 'Copter', hardware: null, startedAt: null, durationSeconds: 60,
      analyzedSeconds: 50, armingKnown: true, analysisRunId: 'r', engineVersion: 'rules-1', infoCount: 1, warningCount: 0, criticalCount: 0,
      findingsByCategory: { data: 1 }, eventCounts: { ModeChange: 2 }, modes: [{ mode: 'LOITER', seconds: 50, share: 1 }], battery: null, vibration: null, latestAiReport: null,
    });
    const comparison: FlightComparison = {
      version: 'comparison-v1', generatedAt: '2026-10-15T12:00:00Z',
      flights: [flight('a', 'px4 uçuşu', 'Px4'), flight('b', 'sitl uçuşu', 'ArduPilot')],
      signals: [{ name: 'battery.voltage', unit: 'V', comparable: true, note: 'No valid samples in 1 of 2 flights.', values: [{ samples: 10, rateHz: 5, min: 15, max: 16.8, mean: 15.9, p5: 15.1, p50: 15.9, p95: 16.7 }, null] }],
      rules: [{ ruleId: 'data.gaps', values: [{ status: 'Applied', findings: 1, highestSeverity: 'Info' }, null] }],
      notes: [{ code: 'different_autopilot', message: 'Farklı otopilotlar.', flightIds: [] }, { code: 'no_vibration_metric', message: 'Titreşim yok.', flightIds: ['a'] }],
      limitations: ['Sınırlama 1'],
    };
    mockApi(() => undefined);
    renderWithApp(<ComparisonView comparison={comparison} />);

    const notes = screen.getByTestId('comparison-notes');
    expect(within(notes).getByText('Farklı otopilotlar.')).toBeInTheDocument();
    expect(within(notes).getByText('(px4 uçuşu)')).toBeInTheDocument();
    const row = screen.getByTestId('signal-row');
    expect(within(row).getByText('Batarya voltajı (V)')).toBeInTheDocument();
    expect(within(row).getByText('15.9')).toBeInTheDocument();
    expect(within(row).getByText('veri yok')).toBeInTheDocument();
    await userEvent.click(screen.getByText('P95'));
    expect(within(row).getByText('16.7')).toBeInTheDocument();
    expect(screen.getByText('Mod: LOITER')).toBeInTheDocument();
  });
});
