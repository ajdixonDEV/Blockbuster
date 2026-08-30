const { defineConfig } = require('@playwright/test');
const path = require('path');
const fs = require('fs');

const dataRoot = path.join(__dirname, '.playwright-data');
fs.mkdirSync(dataRoot, { recursive: true });

module.exports = defineConfig({
  testDir: './tests/browser',
  fullyParallel: false,
  workers: 1,
  timeout: 60_000,
  use: {
    baseURL: 'http://127.0.0.1:5180',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure'
  },
  webServer: {
    command: 'dotnet run --project Blockbuster --no-restore --no-launch-profile -- --urls http://127.0.0.1:5180',
    url: 'http://127.0.0.1:5180/health/live',
    timeout: 60_000,
    reuseExistingServer: true,
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      Storage__DataRoot: dataRoot,
      Scanning__ScanOnStartup: 'false',
      Authentication__BootstrapPin: '1234'
    }
  }
});
