const fs = require("node:fs");
const path = require("node:path");
const { defineConfig } = require("@playwright/test");

const port = Number.parseInt(process.env.BLOCKBUSTER_TEST_PORT, 10);
const dataRoot = process.env.BLOCKBUSTER_TEST_DATA_ROOT;

if (!Number.isInteger(port) || port < 1 || port > 65535) {
  throw new Error("BLOCKBUSTER_TEST_PORT must be supplied by the browser-test launcher.");
}

if (!dataRoot || !path.isAbsolute(dataRoot)) {
  throw new Error(
    "BLOCKBUSTER_TEST_DATA_ROOT must be an absolute path supplied by the " +
      "browser-test launcher.",
  );
}

const baseUrl = `http://127.0.0.1:${port}`;
const mediaRoot = path.join(dataRoot, "media");
const fixture = path.join(mediaRoot, "Browser Fixture (2024).mp4");
fs.mkdirSync(mediaRoot, { recursive: true });
fs.copyFileSync(path.join(__dirname, "tests", "fixtures", "Browser Fixture (2024).mp4"), fixture);

module.exports = defineConfig({
  testDir: "./tests/browser",
  fullyParallel: false,
  workers: 1,
  timeout: 60_000,
  use: {
    baseURL: baseUrl,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  webServer: {
    command:
      "dotnet run --project Blockbuster --no-build --no-restore " +
      `--no-launch-profile -- --urls ${baseUrl}`,
    url: `${baseUrl}/health/live`,
    timeout: 60_000,
    reuseExistingServer: false,
    env: {
      ASPNETCORE_ENVIRONMENT: "Development",
      Storage__DataRoot: dataRoot,
      Libraries__Sources__0__Id: "browser-fixtures",
      Libraries__Sources__0__MovieRoots__0: mediaRoot,
      Scanning__ScanOnStartup: "false",
      MediaProbe__ExecutablePath: process.env.MediaProbe__ExecutablePath || "ffprobe",
      Playback__ProgressInterval: "00:00:00.100",
      Rooms__DriftCheckInterval: "00:00:00.100",
      Authentication__BootstrapPin: "1234",
    },
  },
});
