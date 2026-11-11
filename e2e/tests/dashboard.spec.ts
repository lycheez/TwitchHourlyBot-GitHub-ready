import { test, expect } from '@playwright/test';

test('add a channel and a rule, then check chat messages', async ({ page }) => {
  const name = 'e2e_' + Date.now().toString(36);
  await page.goto('/');

  await page.getByPlaceholder('channel name').fill(name);
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(page.getByRole('heading', { name: '#' + name })).toBeVisible();
  await expect(page.getByText('No keywords.')).toBeVisible();

  await page.getByPlaceholder('keyword', { exact: true }).fill('spoiler');
  await page.getByLabel('Timeout seconds').selectOption('120');
  await page.getByRole('button', { name: 'Add rule' }).click();
  await expect(page.getByRole('cell', { name: 'spoiler', exact: true })).toBeVisible();
  await expect(page.getByRole('cell', { name: '120s', exact: true })).toBeVisible();

  await page.getByPlaceholder('type a chat message').fill('no SPOILER please');
  await page.getByRole('button', { name: 'Check' }).click();
  await expect(page.getByText('Would time out for 120s')).toBeVisible();

  await page.getByPlaceholder('type a chat message').fill('hello there');
  await page.getByRole('button', { name: 'Check' }).click();
  await expect(page.getByText('No rule matches.')).toBeVisible();

  await page.screenshot({ path: 'screenshots/dashboard.png', fullPage: true });
});

test('duplicate channel shows an error', async ({ page }) => {
  const name = 'dup_' + Date.now().toString(36);
  await page.goto('/');
  for (let i = 0; i < 2; i++) {
    await page.getByPlaceholder('channel name').fill(name);
    await page.getByRole('button', { name: 'Add', exact: true }).click();
  }
  await expect(page.getByRole('alert')).toContainText('already exists');
});
