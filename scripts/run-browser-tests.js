const fs = require("node:fs");
const net = require("node:net");
const path = require("node:path");
const { spawn } = require("node:child_process");

function reserveLoopbackPort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.unref();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      server.close((error) => {
        if (error) {
          reject(error);
          return;
        }

        resolve(address.port);
      });
    });
  });
}

async function main() {
  const port = await reserveLoopbackPort();
  const runsRoot = path.join(__dirname, "..", ".playwright-data");
  fs.mkdirSync(runsRoot, { recursive: true });
  const dataRoot = fs.mkdtempSync(path.join(runsRoot, "run-"));
  const playwrightCli = path.join(
    path.dirname(require.resolve("@playwright/test/package.json")),
    "cli.js",
  );
  const child = spawn(process.execPath, [playwrightCli, "test", ...process.argv.slice(2)], {
    cwd: path.join(__dirname, ".."),
    env: {
      ...process.env,
      BLOCKBUSTER_TEST_PORT: String(port),
      BLOCKBUSTER_TEST_DATA_ROOT: dataRoot,
    },
    stdio: "inherit",
  });

  child.once("error", (error) => {
    throw error;
  });
  child.once("exit", (code, signal) => {
    if (signal) {
      process.kill(process.pid, signal);
      return;
    }

    process.exitCode = code ?? 1;
  });
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
