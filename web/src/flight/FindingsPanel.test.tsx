import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AnalysisRun, Finding } from '../api/types';
import { mockApi, renderWithApp } from '../test/render';
import { FindingsPanel } from './FindingsPanel';

afterEach(() => vi.unstubAllGlobals());

const run: AnalysisRun = {
  id: 'run-1', flightId: 'f1', logFileId: 'l1', kind: 'Rules', trigger: 'Automatic', status: 'Succeeded', engineVersion: 'rules-1',
  requestedBy: null, attempt: 1, retryCount: 0, createdAt: '2026-10-15T10:00:00Z', nextAttemptAt: null, startedAt: '2026-10-15T10:00:00Z',
  finishedAt: '2026-10-15T10:00:01Z', durationMs: 120, findingCount: 2, infoCount: 1, warningCount: 1, criticalCount: 0, rulesApplied: 12,
  rulesNotApplicable: 1, rulesFailed: 0, inputSummary: {}, failureCode: null, failureMessage: null, correlationId: 'c', rules: null,
};

const finding = (id: string, severity: Finding['severity'], title: string, startUs: number): Finding => ({
  id, runId: 'run-1', ruleId: 'battery.voltage', ruleVersion: 1, category: 'battery', title, description: `${title} açıklaması`, severity,
  confidence: 'High', startUs, endUs: startUs + 2_000_000, signals: ['battery.voltage'], measured: { min_voltage: 13.91 },
  threshold: { BATT_LOW_VOLT: 14.2 }, thresholdSource: 'parameter BATT_LOW_VOLT', dataCoverage: 0.98, occurrenceCount: 1, limitations: null,
});

describe('FindingsPanel', () => {
  it('lists findings by severity and shows measured value, threshold and its source', async () => {
    mockApi((url) => {
      if (url.pathname === '/api/v1/analysis-rules') return { body: [{ id: 'battery.voltage', version: 1, category: 'battery', title: 'Batarya voltajı', description: '', requiredData: [], applicability: '' }] };
      if (url.pathname === '/api/v1/analysis-runs/run-1') return { body: { ...run, rules: [{ ruleId: 'battery.voltage', ruleVersion: 1, status: 'Applied', reason: null, findingCount: 1, signals: [], durationMs: 3 }] } };
      return undefined;
    });
    const onShow = vi.fn();
    const findings = [finding('a', 'Info', 'Mod değişimi', 1_000_000), finding('b', 'Warning', 'Düşük voltaj', 5_000_000)];

    renderWithApp(
      <FindingsPanel flightId="f1" runs={[run]} findings={findings} findingsLoading={false} logFromUs={0} onSelect={() => {}} onShowOnChart={onShow} />,
    );

    const items = screen.getAllByTestId('finding');
    expect(within(items[0]!).getByText('Düşük voltaj')).toBeInTheDocument();
    expect(within(items[1]!).getByText('Mod değişimi')).toBeInTheDocument();

    await userEvent.click(screen.getByText('Düşük voltaj'));
    const warning = within(items[0]!);
    expect(warning.getByText('min_voltage = 13.91')).toBeInTheDocument();
    expect(warning.getByText('BATT_LOW_VOLT = 14.2')).toBeInTheDocument();
    expect(warning.getByText('parameter BATT_LOW_VOLT')).toBeInTheDocument();
    expect(await warning.findByText('Batarya voltajı', { exact: false })).toBeInTheDocument();

    await userEvent.click(warning.getByRole('button', { name: 'Grafikte göster', hidden: true }));
    expect(onShow).toHaveBeenCalledWith(expect.objectContaining({ id: 'b' }));
  });

  it('says when the flight has not been analyzed', () => {
    mockApi((url) => (url.pathname === '/api/v1/analysis-rules' ? { body: [] } : undefined));
    renderWithApp(<FindingsPanel flightId="f1" runs={[]} findings={[]} findingsLoading={false} logFromUs={0} onSelect={() => {}} onShowOnChart={() => {}} />);
    expect(screen.getByText('Bu uçuş henüz analiz edilmedi.')).toBeInTheDocument();
  });
});
