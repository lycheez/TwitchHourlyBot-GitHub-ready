import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  reporter: [['list']],
  use: { baseURL: 'http://localhost:4200', viewport: { width: 1100, height: 700 } },
  webServer: [
    {
      command: 'dotnet run --project ../src/TwitchHourlyBot.Api --urls http://localhost:5000',
      url: 'http://localhost:5000/api/health',
      timeout: 180_000,
      reuseExistingServer: !process.env.CI,
    },
    {
      command: 'npm start --prefix ../web',
      url: 'http://localhost:4200',
      timeout: 180_000,
      reuseExistingServer: !process.env.CI,
    },
  ],
});
