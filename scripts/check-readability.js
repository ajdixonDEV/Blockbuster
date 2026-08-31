const fs = require("node:fs");
const path = require("node:path");

const repositoryRoot = path.join(__dirname, "..");
const authoredExtensions = new Set([".cs", ".razor", ".js", ".css", ".sql"]);
const excludedDirectories = new Set([
  ".git",
  ".playwright-data",
  "artifacts",
  "bin",
  "node_modules",
  "obj",
  "playwright-report",
  "test-results",
]);
const excludedPrefixes = [path.join("Blockbuster", "wwwroot", "lib") + path.sep];
const violations = [];

function isExcluded(relativePath, entry) {
  if (entry.isDirectory() && excludedDirectories.has(entry.name)) {
    return true;
  }

  return excludedPrefixes.some((prefix) => relativePath.startsWith(prefix));
}

function inspectDirectory(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const absolutePath = path.join(directory, entry.name);
    const relativePath = path.relative(repositoryRoot, absolutePath);
    if (isExcluded(relativePath, entry)) {
      continue;
    }

    if (entry.isDirectory()) {
      inspectDirectory(absolutePath);
      continue;
    }

    if (!authoredExtensions.has(path.extname(entry.name).toLowerCase())) {
      continue;
    }

    const lines = fs.readFileSync(absolutePath, "utf8").split(/\r?\n/u);
    lines.forEach((line, index) => {
      if (line.length > 160) {
        violations.push(`${relativePath}:${index + 1} (${line.length} characters)`);
      }
    });
  }
}

inspectDirectory(repositoryRoot);

if (violations.length > 0) {
  console.error("Authored source contains lines longer than 160 characters:");
  for (const violation of violations) {
    console.error(`  ${violation}`);
  }
  process.exitCode = 1;
} else {
  console.log("Authored source readability check passed.");
}
