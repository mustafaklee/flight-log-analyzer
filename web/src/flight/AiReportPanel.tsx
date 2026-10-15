import { Alert, Badge, Button, Card, Divider, Group, List, Select, SimpleGrid, Stack, Text, Title, Tooltip } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertTriangle, IconChartLine, IconDownload, IconFileCode, IconRobot, IconSparkles } from '@tabler/icons-react';
import { useState } from 'react';
import { download, errorMessage } from '../api/client';
import { useAiAnalyses, useAiAnalysis, useAiStatus, useRequestAiAnalysis } from '../api/queries';
import type { AiAnalysis, AiEvidence, AiReport, AiReportItem } from '../api/types';
import { useTeam } from '../auth/TeamContext';
import { SeverityBadge } from '../components/StatusBadge';
import { ErrorState, LoadingState } from '../components/States';
import { formatDateTime, formatLogTime } from '../lib/format';
import { confidenceLabels } from '../lib/labels';

const statusLabels: Record<AiAnalysis['status'], string> = { Queued: 'Kuyrukta', Running: 'Yazılıyor', Succeeded: 'Tamamlandı', Failed: 'Başarısız' };
const statusColors: Record<AiAnalysis['status'], string> = { Queued: 'blue', Running: 'cyan', Succeeded: 'green', Failed: 'red' };
const kindLabels: Record<AiReportItem['kind'], string> = { Issue: 'Sorun', Observation: 'Gözlem', DataQuality: 'Veri kalitesi' };
const assessmentLabels: Record<AiReport['overallAssessment'], { label: string; color: string }> = {
  NoIssuesFound: { label: 'Sorun bulunmadı', color: 'green' },
  MinorIssues: { label: 'Küçük sorunlar', color: 'blue' },
  SignificantIssues: { label: 'Önemli sorunlar', color: 'yellow' },
  CriticalIssues: { label: 'Kritik sorunlar', color: 'red' },
  Inconclusive: { label: 'Belirsiz', color: 'gray' },
};
const evidenceLabels: Record<AiEvidence['kind'], string> = { Finding: 'Bulgu', Event: 'Olay', Signal: 'Sinyal', Parameter: 'Parametre', Rule: 'Kural' };

/** Turkish explanations of the failure codes the worker records. */
export const aiFailureLabels: Record<string, string> = {
  'ai.not_configured': 'Sunucuda AI sağlayıcısı yapılandırılmamış (API anahtarı yok).',
  'ai.refused': 'Model bu raporu yazmayı reddetti.',
  'ai.truncated': 'Modelin yanıtı çıktı sınırında kesildi.',
  'ai.context_too_large': 'Uçuş bağlamı modelin bağlam penceresine sığmadı.',
  'ai.invalid_output': 'Modelin yanıtı, bir düzeltme denemesinden sonra da geçerli bir rapor değildi.',
  'ai.provider_auth': 'Sağlayıcı API anahtarını reddetti; yöneticinin yapılandırmayı kontrol etmesi gerekiyor.',
  'ai.provider_rejected': 'Sağlayıcı isteği reddetti.',
  'ai.provider_unavailable': 'Sağlayıcıya ulaşılamadı ya da zamanında yanıt vermedi; otomatik denemeler tükendi.',
  'ai.rate_limited': 'Sağlayıcının istek sınırına ulaşıldı; otomatik denemeler tükendi.',
  'ai.rule_analysis_missing': 'Raporun dayandığı kural analizi artık yok (log yeniden işlenmiş olabilir).',
  'ai.interrupted': 'Analiz yarıda kaldı (worker yanıt vermedi).',
};

function formatCost(value: number | null | undefined) {
  return value == null ? 'fiyat tanımlı değil' : `$${value.toFixed(4)}`;
}

/**
 * The AI report tab. A report is the model's interpretation of the rule findings, kept visibly apart from them:
 * every item shows the context items it cites (resolved and checked by the server), items whose references do not
 * exist are listed separately as unsupported, and numbers not found in the context are flagged.
 */
export function AiReportPanel({
  flightId,
  teamId,
  logFromUs,
  hasRuleAnalysis,
  onShowEvidence,
}: {
  flightId: string;
  teamId: string;
  logFromUs: number;
  hasRuleAnalysis: boolean;
  onShowEvidence: (evidence: AiEvidence) => void;
}) {
  const { can } = useTeam();
  const status = useAiStatus(teamId);
  const analyses = useAiAnalyses(flightId);
  const request = useRequestAiAnalysis(flightId, teamId);
  const [picked, setPicked] = useState<string | null>(null);
  const selectedId = picked ?? analyses.data?.[0]?.id;
  const analysis = useAiAnalysis(selectedId);

  const pending = analyses.data?.some((a) => a.status === 'Queued' || a.status === 'Running') ?? false;
  const quotaLeft = status.data && status.data.dailyLimit > 0 ? status.data.dailyLimit - status.data.usedToday : null;

  const start = () =>
    request.mutate(undefined, {
      onSuccess: (a) => {
        setPicked(a.id);
        notifications.show({ color: 'green', message: 'AI analizi kuyruğa alındı.' });
      },
      onError: (e) => notifications.show({ color: 'red', title: 'AI analizi başlatılamadı', message: errorMessage(e) }),
    });

  if (status.isPending || analyses.isPending) {
    return <LoadingState />;
  }

  if (status.isError || analyses.isError) {
    return <ErrorState error={status.error ?? analyses.error} onRetry={() => void Promise.all([status.refetch(), analyses.refetch()])} />;
  }

  return (
    <Stack>
      <Alert variant="light" color="violet" icon={<IconRobot size={18} />} title="Model yorumu">
        Bu sekme bir dil modelinin kural bulgularını yorumlamasıdır; bulguların yerine geçmez. Her madde, sunucunun doğruladığı kanıtlara
        bağlanır. Kanıtı olmayan iddialar ayrı gösterilir, bağlamda bulunmayan sayılar işaretlenir.
      </Alert>

      <Group justify="space-between" align="flex-end">
        {analyses.data.length > 0 ? (
          <Select
            label="Rapor"
            w={340}
            allowDeselect={false}
            value={selectedId ?? null}
            onChange={setPicked}
            data={analyses.data.map((a) => ({ value: a.id, label: `${formatDateTime(a.createdAt)} · ${statusLabels[a.status]} · ${a.model}` }))}
          />
        ) : (
          <Text size="sm" c="dimmed">
            Bu uçuş için henüz AI raporu yok.
          </Text>
        )}
        <Group gap="xs" align="center">
          {quotaLeft != null && (
            <Text size="xs" c="dimmed">
              Bugün kalan: {Math.max(0, quotaLeft)} / {status.data.dailyLimit}
            </Text>
          )}
          {can('Engineer') && (
            <Tooltip
              disabled={status.data.enabled && hasRuleAnalysis && !pending}
              label={!status.data.enabled ? 'AI analizi bu sunucuda kapalı.' : !hasRuleAnalysis ? 'Önce başarılı bir kural analizi gerekiyor.' : 'Bir AI analizi zaten sürüyor.'}
            >
              <Button
                leftSection={<IconSparkles size={16} />}
                onClick={start}
                loading={request.isPending}
                disabled={!status.data.enabled || !hasRuleAnalysis || pending || quotaLeft === 0}
              >
                AI raporu oluştur
              </Button>
            </Tooltip>
          )}
        </Group>
      </Group>

      {!status.data.enabled && (
        <Alert color="gray" title="AI analizi kapalı">
          Yönetici AI analizini açmadıkça uçuş verisi dış bir sağlayıcıya gönderilmez. Açmak için sunucu yapılandırmasında <code>Ai__Enabled</code> ve worker için
          API anahtarı gerekir.
        </Alert>
      )}

      {selectedId &&
        (analysis.isPending ? (
          <LoadingState />
        ) : analysis.isError ? (
          <ErrorState error={analysis.error} onRetry={() => analysis.refetch()} />
        ) : (
          <AnalysisView analysis={analysis.data} logFromUs={logFromUs} canSeeContext={can('Engineer')} onShowEvidence={onShowEvidence} />
        ))}
    </Stack>
  );
}

function AnalysisView({
  analysis,
  logFromUs,
  canSeeContext,
  onShowEvidence,
}: {
  analysis: AiAnalysis;
  logFromUs: number;
  canSeeContext: boolean;
  onShowEvidence: (evidence: AiEvidence) => void;
}) {
  const save = (path: string, name: string) =>
    download(path, name).catch((e) => notifications.show({ color: 'red', title: 'İndirilemedi', message: errorMessage(e) }));

  return (
    <Stack>
      <Card withBorder padding="sm">
        <Group justify="space-between" align="flex-start">
          <Stack gap={2}>
            <Group gap="xs">
              <Badge color={statusColors[analysis.status]} variant="light">
                {statusLabels[analysis.status]}
              </Badge>
              <Text size="sm">
                {analysis.provider} · {analysis.responseModel ?? analysis.model} · {analysis.promptVersion}
              </Text>
            </Group>
            <Text size="xs" c="dimmed">
              {analysis.inputTokens.toLocaleString('tr-TR')} girdi / {analysis.outputTokens.toLocaleString('tr-TR')} çıktı token · {formatCost(analysis.costUsd)} ·{' '}
              {analysis.providerCalls} çağrı · model süresi {(analysis.latencyMs / 1000).toFixed(1)} s
              {analysis.contextHash && ` · bağlam ${analysis.contextHash.slice(0, 12)}`}
            </Text>
          </Stack>
          <Group gap="xs">
            {canSeeContext && analysis.contextHash && (
              <Button size="xs" variant="default" leftSection={<IconFileCode size={14} />} onClick={() => save(`/ai-analyses/${analysis.id}/context`, `ai-baglam-${analysis.id.slice(0, 8)}.json`)}>
                Modele gönderilen veri
              </Button>
            )}
            {analysis.status === 'Succeeded' && (
              <Button size="xs" variant="default" leftSection={<IconDownload size={14} />} onClick={() => save(`/ai-analyses/${analysis.id}/report.md`, `ai-rapor-${analysis.id.slice(0, 8)}.md`)}>
                Markdown indir
              </Button>
            )}
          </Group>
        </Group>
      </Card>

      {(analysis.status === 'Queued' || analysis.status === 'Running') && (
        <LoadingState label={analysis.retryCount > 0 ? `Sağlayıcı geçici olarak yanıt vermedi; yeniden denenecek (${analysis.retryCount}. deneme).` : 'Model raporu yazıyor; bitince bu sayfa kendiliğinden güncellenir.'} />
      )}

      {analysis.status === 'Failed' && (
        <Alert color="red" title={`Rapor oluşturulamadı (${analysis.failureCode})`}>
          <Text size="sm">{(analysis.failureCode && aiFailureLabels[analysis.failureCode]) ?? 'AI analizi başarısız oldu.'}</Text>
          {analysis.failureMessage && (
            <Text size="xs" c="dimmed" mt={4}>
              {analysis.failureMessage}
            </Text>
          )}
          <Text size="xs" c="dimmed" mt={4}>
            Kural bulguları bundan etkilenmez.
          </Text>
        </Alert>
      )}

      {analysis.report && <ReportView report={analysis.report} logFromUs={analysis.logStartUs ?? logFromUs} onShowEvidence={onShowEvidence} />}
    </Stack>
  );
}

function ReportView({ report, logFromUs, onShowEvidence }: { report: AiReport; logFromUs: number; onShowEvidence: (e: AiEvidence) => void }) {
  const supported = report.items.filter((i) => i.supported);
  const unsupported = report.items.filter((i) => !i.supported);
  const v = report.validation;
  const assessment = assessmentLabels[report.overallAssessment];

  return (
    <Stack>
      <Card withBorder>
        <Group gap="xs" mb="xs">
          <Title order={5}>Özet</Title>
          <Badge color={assessment.color} variant="light">
            {assessment.label}
          </Badge>
        </Group>
        <Text size="sm">{report.summary}</Text>
        {report.plainSummary && (
          <Text size="sm" c="dimmed" mt="xs">
            Sade anlatım: {report.plainSummary}
          </Text>
        )}
        {v.summaryUnverifiedNumbers.length > 0 && <Unverified numbers={v.summaryUnverifiedNumbers} />}
        <Divider my="sm" />
        <Text size="xs" c="dimmed" data-testid="ai-validation">
          Doğrulama: {v.items} madde, {v.supportedItems} kanıtla destekli, {v.unsupportedItems} desteklenmeyen
          {v.unknownRefs.length > 0 && ` · bağlamda olmayan referanslar: ${v.unknownRefs.join(', ')}`}
          {v.unverifiedNumbers > 0 && ` · doğrulanamayan sayı: ${v.unverifiedNumbers}`}
        </Text>
      </Card>

      {supported.length === 0 ? (
        <Text size="sm" c="dimmed">
          Model kanıtla desteklenen bir madde yazmadı.
        </Text>
      ) : (
        supported.map((item) => <ItemCard key={item.index} item={item} logFromUs={logFromUs} onShowEvidence={onShowEvidence} />)
      )}

      {unsupported.length > 0 && (
        <Card withBorder style={{ borderColor: 'var(--mantine-color-orange-6)' }}>
          <Group gap="xs" mb="xs">
            <IconAlertTriangle size={18} color="var(--mantine-color-orange-6)" />
            <Title order={5}>Desteklenmeyen iddialar</Title>
          </Group>
          <Text size="sm" c="dimmed" mb="sm">
            Model bu maddeler için bağlamda var olan bir kanıt göstermedi. Doğrulanmamış kabul edin.
          </Text>
          <Stack>
            {unsupported.map((item) => (
              <ItemCard key={item.index} item={item} logFromUs={logFromUs} onShowEvidence={onShowEvidence} />
            ))}
          </Stack>
        </Card>
      )}

      <SimpleGrid cols={{ base: 1, md: 2 }}>
        <TextList title="Açık sorular" values={report.followUpQuestions} />
        <TextList title="Sınırlamalar" values={report.limitations} />
      </SimpleGrid>
    </Stack>
  );
}

function ItemCard({ item, logFromUs, onShowEvidence }: { item: AiReportItem; logFromUs: number; onShowEvidence: (e: AiEvidence) => void }) {
  return (
    <Card withBorder data-testid="ai-item" data-supported={item.supported}>
      <Group gap="xs" mb={4}>
        <SeverityBadge severity={item.severity} />
        <Badge variant="outline" color="gray">
          {kindLabels[item.kind]}
        </Badge>
        <Tooltip label={item.confidenceReason || 'Gerekçe verilmedi'} multiline w={320}>
          <Badge variant="light" color="gray">
            Güven: {confidenceLabels[item.confidence]}
          </Badge>
        </Tooltip>
      </Group>
      <Text fw={600}>{item.title}</Text>
      <Text size="sm" mt={4}>
        {item.statement}
      </Text>
      {item.unverifiedNumbers.length > 0 && <Unverified numbers={item.unverifiedNumbers} />}

      {item.evidence.length > 0 && (
        <Stack gap={4} mt="sm">
          <Text size="xs" fw={600} c="dimmed">
            Kanıt
          </Text>
          {item.evidence.map((e) => (
            <Group key={e.ref} gap="xs" wrap="nowrap" align="flex-start">
              <Badge size="sm" variant="light" color="violet" style={{ flexShrink: 0 }}>
                {e.ref}
              </Badge>
              <Text size="xs" style={{ flex: 1 }}>
                <Text span fw={500}>
                  {evidenceLabels[e.kind]}: {e.label}
                </Text>
                {e.startUs != null && (
                  <Text span c="dimmed">
                    {' '}
                    ({formatLogTime(e.startUs, logFromUs)}
                    {e.endUs != null && e.endUs !== e.startUs ? `–${formatLogTime(e.endUs, logFromUs)}` : ''})
                  </Text>
                )}
                {e.relevance && <Text span c="dimmed">{` · ${e.relevance}`}</Text>}
              </Text>
              {e.startUs != null && (
                <Button size="compact-xs" variant="subtle" leftSection={<IconChartLine size={12} />} onClick={() => onShowEvidence(e)}>
                  Grafikte göster
                </Button>
              )}
            </Group>
          ))}
        </Stack>
      )}
      {item.unknownRefs.length > 0 && (
        <Text size="xs" c="orange" mt={4}>
          Bağlamda bulunmayan referanslar (yok sayıldı): {item.unknownRefs.join(', ')}
        </Text>
      )}

      <SimpleGrid cols={{ base: 1, sm: 2 }} mt="sm" spacing="xs">
        <TextList title="Olası nedenler (hipotez)" values={item.possibleCauses} compact />
        <TextList title="Alternatif açıklamalar" values={item.alternativeExplanations} compact />
        <TextList title="Eksik veri" values={item.missingData} compact />
        <TextList title="Önerilen kontroller" values={item.recommendedChecks} compact />
      </SimpleGrid>
    </Card>
  );
}

function Unverified({ numbers }: { numbers: string[] }) {
  return (
    <Text size="xs" c="orange" mt={4}>
      Bağlamda bulunmayan sayılar: {numbers.join(', ')}. Bu değerleri grafiklerden kontrol edin.
    </Text>
  );
}

function TextList({ title, values, compact = false }: { title: string; values: string[]; compact?: boolean }) {
  if (values.length === 0) {
    return null;
  }

  const content = (
    <>
      <Text size="xs" fw={600} c="dimmed" mb={2}>
        {title}
      </Text>
      <List size="xs" spacing={2}>
        {values.map((v) => (
          <List.Item key={v}>{v}</List.Item>
        ))}
      </List>
    </>
  );
  return compact ? <div>{content}</div> : <Card withBorder>{content}</Card>;
}
