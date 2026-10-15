import { expect, test } from '@playwright/test';
import { signUpWithProject, uploadAndWait } from './helpers';

test('a user uploads a PX4 log and inspects its signals, events and findings', async ({ page }) => {
  const { project } = await signUpWithProject(page);
  // Status updates arrive over the SignalR hub (WebSocket through nginx).
  await expect(page.getByLabel('Canlı güncellemeler açık')).toBeVisible();
  await uploadAndWait(page, project, 'px4/sample.ulg');

  // Flight page: summary, default signals drawn, events listed.
  await expect(page.getByRole('heading', { name: /sample/ })).toBeVisible();
  await expect(page.getByText('PX4', { exact: false }).first()).toBeVisible();
  await expect(page.locator('.uplot canvas').first()).toBeVisible();
  const events = page.getByRole('list', { name: 'Olaylar' });
  await expect(events.getByRole('listitem').first()).toBeVisible();

  // Picking an event narrows the chart window.
  const rangeText = page.getByText(/s · seçmek için grafikte sürükleyin/);
  const before = await rangeText.textContent();
  await events.getByRole('listitem').first().getByRole('button').click();
  await expect(rangeText).not.toHaveText(before ?? '');

  // Adding a raw signal from the catalog puts it on the chart.
  await page.getByRole('tab', { name: 'Sinyaller' }).click();
  await page.getByText(/^Ham \(/).click();
  await page.getByLabel('Sinyal ara').fill('vehicle_status');
  const row = page.getByRole('list', { name: 'Sinyaller' }).getByRole('listitem').first();
  await expect(row).toContainText('vehicle_status');
  await row.getByRole('button', { name: 'Grafiğe ekle' }).click();
  await expect(page.getByRole('tab', { name: 'İnceleme' })).toHaveAttribute('aria-selected', 'true');
  await expect(page).toHaveURL(/signals=.*vehicle_status/);

  // Findings carry measured value, threshold and its source; "show on chart" focuses the chart on the finding.
  await page.getByRole('tab', { name: /Bulgular/ }).click();
  const finding = page.getByTestId('finding').first();
  await finding.getByRole('button').first().click();
  await expect(finding.getByText('Eşik kaynağı')).toBeVisible();
  await finding.getByRole('button', { name: 'Grafikte göster' }).click();
  await expect(page).toHaveURL(/finding=/);
  await expect(page.locator('.uplot canvas').first()).toBeVisible();

  // Parameters, processing history and metadata.
  await page.getByRole('tab', { name: 'Parametreler' }).click();
  await page.getByLabel('Yalnızca uçuşta değişenler').check();
  await expect(page.getByText('değişiklik').first()).toBeVisible();
  await page.getByRole('tab', { name: 'İşleme' }).click();
  await expect(page.getByText('İşleme geçmişi')).toBeVisible();
  await expect(page.getByRole('cell', { name: 'Başarılı' })).toBeVisible();

  // The list shows the flight with its analysis summary.
  await page.getByRole('link', { name: 'Uçuş geçmişi' }).click();
  await expect(page.getByRole('link', { name: /sample/ })).toBeVisible();
});

test('the map shows the GPS track of a log with positions', async ({ page }) => {
  const { project } = await signUpWithProject(page);
  await uploadAndWait(page, project, 'ardupilot/sitl-copter-4.6.3.bin');
  await expect(page.getByRole('tab', { name: 'Harita' })).toBeVisible();
  await page.getByRole('tab', { name: 'Harita' }).click();
  await expect(page.getByRole('region', { name: 'GPS izi haritası' })).toBeVisible();
});
