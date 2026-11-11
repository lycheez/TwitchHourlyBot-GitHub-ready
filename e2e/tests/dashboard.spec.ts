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
  await page.getByPlaceholder('channel name').fill(name);
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(page.getByRole('heading', { name: '#' + name })).toBeVisible();
  await expect(page.getByPlaceholder('channel name')).toHaveValue('');
  await page.getByPlaceholder('channel name').fill(name.toUpperCase());
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('already exists');
});

test('save Twitch credentials; the bot cannot start before login', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByText('Not connected.')).toBeVisible();

  await page.getByLabel('Twitch client id').fill('e2e-client');
  await page.getByLabel('Twitch client secret').fill('e2e-secret');
  await page.getByRole('button', { name: 'Save credentials' }).click();
  await expect(page.getByText('Credentials saved.')).toBeVisible();
  await expect(page.getByLabel('Twitch client secret')).toHaveAttribute('placeholder', /saved/);
  await expect(page.getByRole('button', { name: 'Log in with Twitch' })).toBeEnabled();

  await page.getByRole('button', { name: 'Start bot' }).click();
  await expect(page.getByRole('alert')).toContainText('Not connected to Twitch');
  await expect(page.getByText('STOPPED')).toBeVisible();
  await page.screenshot({ path: 'screenshots/bot-panel.png', fullPage: true });
});
