import { expect, test } from '@playwright/test';

test('the sign-in screen and navigation work on a phone', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('button', { name: 'Giriş yap' })).toBeVisible();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth);
  expect(overflow).toBe(false);
});
