import { expect, test } from '@playwright/test';
import { signUpWithProject, uploadAndWait } from './helpers';

test('an engineer tags, comments, reviews a finding, compares two flights and reads the audit log', async ({ page }) => {
  const { project } = await signUpWithProject(page);
  await uploadAndWait(page, project, 'ardupilot/sitl-copter-4.6.3.bin');

  // Tags are saved on blur and offered as a filter in the history.
  const tags = page.getByRole('textbox', { name: 'Etiketler' });
  await tags.fill('e2e-kabul');
  await tags.press('Enter');
  await tags.blur();
  await expect(page.getByText('e2e-kabul').first()).toBeVisible();

  // A note on the range shown in the charts: picking an event narrows the charts to it.
  await page.getByRole('list', { name: 'Olaylar' }).getByRole('listitem').first().getByRole('button').click();
  await page.getByRole('tab', { name: 'Yorumlar' }).click();
  await page.getByLabel('Yeni yorum').fill('Anten kontrol edilecek');
  await page.getByLabel(/Grafikteki aralığa bağla/).check();
  await page.getByRole('button', { name: 'Ekle' }).click();
  await expect(page.getByTestId('comment').filter({ hasText: 'Anten kontrol edilecek' })).toBeVisible();

  // Reviewing a finding: status and a note, then the history shows who changed what.
  await page.getByRole('tab', { name: /Bulgular/ }).click();
  const finding = page.getByTestId('finding').first();
  await finding.getByRole('button').first().click();
  const review = finding.getByTestId('finding-review');
  await review.getByRole('textbox', { name: 'Durum' }).click();
  await page.getByRole('option', { name: 'İnceleniyor' }).click();
  await expect(review.getByTestId('review-history')).toContainText('Açık → İnceleniyor');
  await review.getByLabel('Bulguya not').fill('SD kart kontrol edilecek');
  await review.getByRole('button', { name: 'Not ekle' }).click();
  await expect(review.getByText('SD kart kontrol edilecek')).toBeVisible();

  // The activity tab lists the changes of this flight.
  await page.getByRole('tab', { name: 'Etkinlik' }).click();
  await expect(page.getByTestId('audit-table')).toContainText('İnceleme durumu değişti');

  // A second flight from another autopilot; the comparison says what is not directly comparable.
  await uploadAndWait(page, project, 'px4/sample.ulg');
  await page.goto('/flights');
  await page.getByLabel(/sitl-copter-4.6.3 karşılaştırmaya ekle/).check();
  await page.getByLabel(/sample karşılaştırmaya ekle/).check();
  await page.getByRole('button', { name: /Karşılaştır \(2\)/ }).click();
  await expect(page.getByRole('heading', { name: 'Uçuş karşılaştırma' })).toBeVisible();
  await expect(page.getByTestId('comparison-notes')).toContainText('different autopilots');
  await expect(page.getByTestId('signal-row').first()).toBeVisible();

  // The work list and the audit log (the creator of a team is its project manager).
  await page.getByRole('link', { name: 'İncelemeler' }).click();
  await page.getByText('Sonuçlanmamış').click();
  await expect(page.getByTestId('reviews-table')).toContainText('İnceleniyor');
  await page.getByRole('link', { name: 'Denetim kaydı' }).click();
  await expect(page.getByTestId('audit-table')).toContainText('Etiketler değişti');
});
