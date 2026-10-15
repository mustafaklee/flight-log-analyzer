import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AiAnalysis, AiReportItem } from '../api/types';
import { mockApi, renderWithApp } from '../test/render';
import { AiReportPanel } from './AiReportPanel';

afterEach(() => vi.unstubAllGlobals());

const item = (index: number, supported: boolean, title: string): AiReportItem => ({
  index, title, kind: 'Issue', statement: `${title} ifadesi`, severity: 'Warning', confidence: 'Medium', confidenceReason: 'Tek ölçüm.',
  supported,
  evidence: supported
    ? [{ ref: 'F1', kind: 'Finding', label: 'Yüksek titreşim', relevance: 'eşiği aşıyor', startUs: 61_000_000, endUs: 71_000_000, findingId: 'finding-1', signal: 'VIBE.VibeX' }]
    : [],
  unknownRefs: supported ? [] : ['F99'],
  unverifiedNumbers: supported ? [] : ['987.6'],
  possibleCauses: ['Pervane dengesizliği'], alternativeExplanations: ['Gevşek montaj'], missingData: [], recommendedChecks: ['Pervaneleri kontrol edin'],
});

const analysis = (overrides: Partial<AiAnalysis> = {}): AiAnalysis => ({
  id: 'ai-1', flightId: 'f1', analysisRunId: 'run-1', status: 'Succeeded', provider: 'Anthropic', model: 'claude-opus-5-5',
  responseModel: 'claude-opus-5-5', promptVersion: 'flight-report-v1', contextVersion: 'flight-context-v1', contextHash: 'abcdef0123456789',
  contextSummary: {}, logStartUs: 1_000_000, requestedBy: 'u1', attempt: 1, retryCount: 0, createdAt: '2026-10-15T10:00:00Z', nextAttemptAt: null,
  startedAt: '2026-10-15T10:00:00Z', finishedAt: '2026-10-15T10:00:20Z', durationMs: 20000, providerCalls: 1, latencyMs: 18500, inputTokens: 12000,
  outputTokens: 2500, cacheReadTokens: 0, cacheWriteTokens: 0, costUsd: 0.098, stopReason: 'end_turn', failureCode: null, failureMessage: null,
  correlationId: 'c', report: null,
  ...overrides,
});

const report = {
  summary: 'Uçuşta yüksek titreşim görüldü.', plainSummary: 'Titreşim yüksekti.', overallAssessment: 'SignificantIssues' as const,
  items: [item(1, true, 'Yüksek titreşim'), item(2, false, 'Motor arızası')],
  followUpQuestions: ['Pervaneler değişti mi?'], limitations: ['Batarya kuralı uygulanamadı.'],
  validation: { items: 2, supportedItems: 1, unsupportedItems: 1, unknownRefs: ['F99'], unverifiedNumbers: 1, summaryUnverifiedNumbers: [] },
};

function api(status: { enabled: boolean }, analyses: AiAnalysis[], detail?: AiAnalysis) {
  return mockApi((url) => {
    if (url.pathname === '/api/v1/teams/t1/ai-status') return { body: { ...status, provider: 'Anthropic', model: 'claude-opus-5-5', promptVersion: 'flight-report-v1', dailyLimit: 20, usedToday: 2 } };
    if (url.pathname === '/api/v1/flights/f1/ai-analyses') return { body: analyses };
    if (detail && url.pathname === `/api/v1/ai-analyses/${detail.id}`) return { body: detail };
    return undefined;
  });
}

describe('AiReportPanel', () => {
  it('shows supported items with their evidence and unsupported claims apart', async () => {
    api({ enabled: true }, [analysis()], analysis({ report }));
    const onShow = vi.fn();

    renderWithApp(<AiReportPanel flightId="f1" teamId="t1" logFromUs={1_000_000} hasRuleAnalysis onShowEvidence={onShow} />);

    expect(await screen.findByText('Uçuşta yüksek titreşim görüldü.')).toBeInTheDocument();
    expect(screen.getByText('Model yorumu')).toBeInTheDocument();
    expect(screen.getByText('Önemli sorunlar')).toBeInTheDocument();
    expect(screen.getByTestId('ai-validation')).toHaveTextContent('2 madde, 1 kanıtla destekli, 1 desteklenmeyen');

    const [supported, unsupported] = screen.getAllByTestId('ai-item');
    expect(supported).toHaveAttribute('data-supported', 'true');
    expect(within(supported!).getByText('Bulgu: Yüksek titreşim')).toBeInTheDocument();
    expect(within(supported!).getByText('Pervane dengesizliği')).toBeInTheDocument();
    expect(unsupported).toHaveAttribute('data-supported', 'false');
    expect(screen.getByText('Desteklenmeyen iddialar')).toBeInTheDocument();
    expect(within(unsupported!).getByText(/Bağlamda bulunmayan referanslar \(yok sayıldı\): F99/)).toBeInTheDocument();
    expect(within(unsupported!).getByText(/Bağlamda bulunmayan sayılar: 987.6/)).toBeInTheDocument();
    expect(screen.getByText(/12\.000 girdi \/ 2\.500 çıktı token · \$0\.0980/)).toBeInTheDocument();

    await userEvent.click(within(supported!).getByRole('button', { name: 'Grafikte göster' }));
    expect(onShow).toHaveBeenCalledWith(expect.objectContaining({ ref: 'F1', findingId: 'finding-1' }));
  });

  it('explains a failed analysis in Turkish and that rule findings are unaffected', async () => {
    const failed = analysis({ status: 'Failed', failureCode: 'ai.refused', failureMessage: 'The model declined.' });
    api({ enabled: true }, [failed], failed);

    renderWithApp(<AiReportPanel flightId="f1" teamId="t1" logFromUs={0} hasRuleAnalysis onShowEvidence={() => {}} />);

    expect(await screen.findByText('Model bu raporu yazmayı reddetti.')).toBeInTheDocument();
    expect(screen.getByText('Kural bulguları bundan etkilenmez.')).toBeInTheDocument();
  });

  it('says when AI analysis is switched off', async () => {
    api({ enabled: false }, []);

    renderWithApp(<AiReportPanel flightId="f1" teamId="t1" logFromUs={0} hasRuleAnalysis={false} onShowEvidence={() => {}} />);

    expect(await screen.findByText('AI analizi kapalı')).toBeInTheDocument();
    expect(screen.getByText('Bu uçuş için henüz AI raporu yok.')).toBeInTheDocument();
  });
});
