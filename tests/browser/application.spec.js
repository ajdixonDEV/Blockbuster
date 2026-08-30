const { test, expect } = require('@playwright/test');

test.describe.configure({ mode: 'serial' });

test('starts the real application and renders profile selection', async ({ page }) => {
  await page.goto('/profiles');
  await expect(page).toHaveTitle(/Choose profile · Blockbuster/);
  await expect(page.getByRole('heading', { name: /choose a profile/i })).toBeVisible();
});
