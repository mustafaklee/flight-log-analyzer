import { expect, type Page } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const fixtures = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../tests/fixtures');

/** Registers a fresh user (self-registration is on in the Compose .env) and creates a team and a project. */
export async function signUpWithProject(page: Page): Promise<{ email: string; project: string }> {
  const id = `${Date.now()}-${Math.floor(Math.random() * 1e6)}`;
  const email = `e2e-${id}@flightlog.local`;
  const project = `E2E proje ${id}`;

  await page.goto('/');
  await expect(page).toHaveURL(/\/login/);
  await page.getByRole('button', { name: 'Kayıt olun' }).click();
  await page.getByLabel('Ad soyad').fill('E2E Kullanıcı');
  await page.getByLabel('E-posta').fill(email);
  await page.getByLabel('Parola').fill('E2e-Password-123');
  await page.getByRole('button', { name: 'Hesap oluştur' }).click();

  await expect(page.getByText('Henüz bir ekibiniz yok')).toBeVisible();
  await page.getByLabel('Ekip adı').fill(`E2E ekip ${id}`);
  await page.getByRole('button', { name: 'Ekip oluştur' }).click();
  await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible();

  await page.getByRole('link', { name: 'Projeler' }).click();
  await page.getByRole('button', { name: 'Proje oluştur' }).click();
  await page.getByRole('dialog').getByLabel('Ad').fill(project);
  await page.getByRole('dialog').getByRole('button', { name: 'Kaydet' }).click();
  await expect(page.getByRole('link', { name: project })).toBeVisible();
  return { email, project };
}

/** Uploads a fixture on the upload page and waits until processing and analysis are complete. */
export async function uploadAndWait(page: Page, project: string, fixture: string): Promise<void> {
  await page.getByRole('link', { name: 'Log yükleme' }).click();
  await page.getByRole('textbox', { name: 'Proje' }).click();
  await page.getByRole('option', { name: project }).click();
  await page.locator('input[type=file]').setInputFiles(path.join(fixtures, fixture));
  const name = path.basename(fixture);
  const row = page.getByRole('row').filter({ hasText: name });
  await expect(row.getByText('Tamamlandı')).toBeVisible({ timeout: 90_000 });
  await row.getByRole('link', { name }).click();
}
