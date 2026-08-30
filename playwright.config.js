const { defineConfig } = require('@playwright/test');
const path = require('path');
const fs = require('fs');
const { execFileSync } = require('child_process');

const dataRoot = path.join(__dirname, '.playwright-data', `run-${process.pid}`);
const mediaRoot = path.join(dataRoot, 'media');
const fixture = path.join(mediaRoot, 'Browser Fixture (2024).mp4');
fs.mkdirSync(mediaRoot, { recursive: true });
execFileSync('C:\\Tools\\ffmpeg\\bin\\ffmpeg.exe', [
  '-y', '-f', 'lavfi', '-i', 'color=c=navy:s=320x180:d=4:r=24',
  '-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=stereo', '-shortest',
  '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac', fixture
], { stdio: 'ignore' });

module.exports = defineConfig({
  testDir: './tests/browser',
  fullyParallel: false,
  workers: 1,
  timeout: 60_000,
  use: {
    baseURL: 'http://127.0.0.1:5181',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure'
  },
  webServer: {
    command: 'dotnet run --project Blockbuster --no-build --no-restore --no-launch-profile -- --urls http://127.0.0.1:5181',
    url: 'http://127.0.0.1:5181/health/live',
    timeout: 60_000,
    reuseExistingServer: true,
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      Storage__DataRoot: dataRoot,
      Libraries__Sources__0__Id: 'browser-fixtures',
      Libraries__Sources__0__MovieRoots__0: mediaRoot,
      Scanning__ScanOnStartup: 'false',
      MediaProbe__ExecutablePath: 'C:\\Tools\\ffmpeg\\bin\\ffprobe.exe',
      Playback__ProgressInterval: '00:00:00.100',
      Rooms__DriftCheckInterval: '00:00:00.100',
      Authentication__BootstrapPin: '1234'
    }
  }
});
