const { test, expect } = require("@playwright/test");

test.describe.configure({ mode: "serial" });

async function seedCatalog(page) {
  await page.goto("/admin/login");
  await page.locator("#admin-pin").fill("1234");
  await Promise.all([
    page.waitForURL("**/admin"),
    page.getByRole("button", { name: "Sign in" }).click(),
  ]);
  for (const name of ["Alice", "Bob"]) {
    if ((await page.getByText(name, { exact: true }).count()) === 0) {
      await page.locator('form[action="/admin/profiles/create"] input[name="name"]').fill(name);
      await Promise.all([
        page.waitForURL("**/admin"),
        page.getByRole("button", { name: "Create profile" }).click(),
      ]);
    }
    await expect(page.locator(`input[aria-label="Profile name"][value="${name}"]`)).toBeVisible();
  }
  await page.getByRole("button", { name: "Scan now" }).click();
  await expect(page.getByText("Browser Fixture (2024)", { exact: true })).toBeVisible();
  const useLocalMetadata = page.getByRole("button", { name: "Use local metadata" });
  if ((await useLocalMetadata.count()) > 0) {
    await Promise.all([page.waitForURL("**/admin"), useLocalMetadata.click()]);
  }
}

async function selectProfile(page, name) {
  await page.goto("/profiles");
  await page.getByRole("button", { name: `Watch as ${name}` }).click();
  await expect(page.getByRole("heading", { name: "Movies" })).toBeVisible();
}

test("starts the real application and renders profile selection", async ({ page }) => {
  await page.goto("/profiles");
  await expect(page).toHaveTitle(/Choose profile · Blockbuster/);
  await expect(page.getByRole("heading", { name: /choose a profile/i })).toBeVisible();

  await expect(
    page.locator(
      'a:not([draggable="false"]), button:not([draggable="false"]), img:not([draggable="false"]), input:not([draggable="false"]), select:not([draggable="false"]), video:not([draggable="false"])',
    ),
  ).toHaveCount(0);
});

test("navigates between distinct library pages without terminating the circuit", async ({
  page,
}) => {
  const circuitErrors = [];
  page.on("console", (message) => {
    if (
      message.type() === "error" &&
      /unhandled exception|circuit will be terminated/i.test(message.text())
    ) {
      circuitErrors.push(message.text());
    }
  });

  await seedCatalog(page);
  await selectProfile(page, "Alice");

  for (const tab of [
    { name: "TV", path: "tv" },
    { name: "Videos", path: "videos" },
    { name: "Music", path: "music" },
  ]) {
    await page.getByRole("link", { name: tab.name, exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/${tab.path}$`));
    await expect(page.getByRole("heading", { name: tab.name, exact: true })).toBeVisible();
    await expect(page.getByText(`${tab.name} is coming later`, { exact: true })).toBeVisible();
    await expect(page.locator(".primary-nav a.active")).toHaveText(tab.name);
  }

  await page.getByRole("link", { name: "Shared", exact: true }).click();
  await expect(page).toHaveURL(/\/shared$/);
  await expect(page.getByRole("heading", { name: "Shared rooms" })).toBeVisible();

  await page.getByRole("link", { name: "Movies", exact: true }).click();
  await expect(page).toHaveURL(/\/movies$/);
  await expect(page.getByRole("heading", { name: "Movies", exact: true })).toBeVisible();
  await expect(page.getByPlaceholder("Search movies")).toBeVisible();
  await page.getByPlaceholder("Search movies").fill("Browser Fixture");
  await page.getByRole("button", { name: "Apply" }).click();
  await expect(page).toHaveURL(/\/movies\?q=Browser\+Fixture/);
  await expect(page.getByRole("link", { name: "Browser Fixture" })).toBeVisible();
  expect(circuitErrors).toEqual([]);
});

test("scans a real fixture and persists direct-play progress", async ({ page }) => {
  await seedCatalog(page);
  await selectProfile(page, "Alice");
  await page.getByRole("link", { name: "Browser Fixture" }).click();
  await page.getByRole("link", { name: "Play" }).click();
  const video = page.locator("#movie-player video");
  await expect(video).toHaveJSProperty("readyState", 4);

  const conflicts = await page.evaluate(async () => {
    const movieId = new URL(location.href).pathname.split("/")[2];
    const post = (positionSeconds) =>
      fetch(`/api/movies/${movieId}/progress`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          positionSeconds,
          expectedRevision: 0,
          eventType: "browser-conflict",
        }),
      }).then(async (response) => ({ status: response.status, body: await response.json() }));
    return Promise.all([post(1), post(2)]);
  });
  expect(conflicts.map((item) => item.status).sort()).toEqual([200, 409]);
  expect(Math.max(...conflicts.map((item) => item.body.revision))).toBeGreaterThan(0);
  await page.locator('#movie-player [data-action="play"]').click();
  await expect(page.locator('#movie-player [data-action="play"]')).toHaveAttribute(
    "aria-label",
    "Pause",
  );
  await page.keyboard.press("m");
  await expect(page.locator('#movie-player [data-action="mute"]')).toHaveAttribute(
    "aria-label",
    "Unmute",
  );
  await page.keyboard.press("ArrowRight");
  await page.waitForTimeout(250);
  await page.locator('#movie-player [data-action="play"]').click();
  await page.goto("/movies");
  await expect(page.locator(".poster-progress")).toBeVisible();
});

test("two profiles synchronize a real shared room and clean up membership", async ({
  browser,
  page,
  context,
}) => {
  await seedCatalog(page);
  await selectProfile(page, "Alice");
  await page.getByRole("link", { name: "Browser Fixture" }).click();
  await page.getByRole("button", { name: "Start a watch party" }).click();
  await expect(page).toHaveURL(/\/shared\//);
  const roomUrl = page.url();
  await expect(page.locator("#shared-player video")).toHaveJSProperty("readyState", 4);
  await expect(page.locator("[data-participants]")).toContainText("Alice");

  const bobContext = await browser.newContext();
  const bobPage = await bobContext.newPage();
  try {
    await selectProfile(bobPage, "Bob");
    await bobPage.goto(roomUrl);
    await expect(bobPage.locator("#shared-player video")).toHaveJSProperty("readyState", 4);
    await expect(page.locator("[data-participants]")).toContainText("Bob");
    await expect(bobPage.locator("[data-participants]")).toContainText("Alice");

    await page.locator('#shared-player [data-action="play"]').click();
    await expect(bobPage.locator('#shared-player [data-action="play"]')).toHaveText("❚❚");
    await page.locator("#shared-player [data-seek]").evaluate((seek) => {
      seek.value = String(Math.min(0.1, Number(seek.max)));
      seek.dispatchEvent(new Event("input", { bubbles: true }));
      seek.dispatchEvent(new Event("change", { bubbles: true }));
    });
    await expect
      .poll(() => bobPage.locator("#shared-player video").evaluate((video) => video.currentTime))
      .toBeGreaterThanOrEqual(0);

    await page.locator("#shared-player [data-volume]").fill("0.25");
    await page.locator('#shared-player [data-action="mute"]').click();
    await expect(page.locator("#shared-player video")).toHaveJSProperty("muted", true);
    await expect(bobPage.locator("#shared-player video")).toHaveJSProperty("muted", false);

    await page.close();
    await expect(bobPage.locator("[data-participants]")).not.toContainText("Alice");

    const aliceRejoined = await context.newPage();
    try {
      await aliceRejoined.goto(roomUrl);
      await expect(aliceRejoined.locator("[data-participants]")).toContainText("Bob");
      await expect(bobPage.locator("[data-participants]")).toContainText("Alice");
    } finally {
      await aliceRejoined.close();
    }
    await expect(bobPage.locator("[data-participants]")).not.toContainText("Alice");
  } finally {
    await bobContext.close();
  }
});

test("player controller keeps controls, keyboard shortcuts, and fullscreen state synchronized", async ({
  page,
}) => {
  await page.goto("/profiles");
  const state = await page.evaluate(async () => {
    const { createPlayerController } = await import("/js/playerController.js");
    const root = document.createElement("div");
    root.innerHTML = `<video tabindex="-1"></video><div class="player-status"></div><div class="player-controls">
      <button data-action="play" aria-label="Play"></button><span data-current></span>
      <input data-seek type="range" min="0" max="0" value="0"><span data-duration></span>
      <button data-action="mute" aria-label="Mute"></button><input data-volume type="range" value="1">
      <button data-action="fullscreen" aria-label="Enter fullscreen"></button></div>`;
    document.body.append(root);
    const video = root.querySelector("video");
    let paused = true;
    Object.defineProperties(video, {
      paused: { configurable: true, get: () => paused },
      duration: { configurable: true, get: () => 120 },
      currentTime: { configurable: true, writable: true, value: 20 },
    });
    video.play = async () => {
      paused = false;
      video.dispatchEvent(new Event("play"));
    };
    video.pause = () => {
      paused = true;
      video.dispatchEvent(new Event("pause"));
    };
    let fullscreen = null;
    Object.defineProperties(document, {
      fullscreenEnabled: { configurable: true, get: () => true },
      fullscreenElement: { configurable: true, get: () => fullscreen },
    });
    root.requestFullscreen = async () => {
      fullscreen = root;
      document.dispatchEvent(new Event("fullscreenchange"));
    };
    document.exitFullscreen = async () => {
      fullscreen = null;
      document.dispatchEvent(new Event("fullscreenchange"));
    };
    const controller = createPlayerController(root);
    video.dispatchEvent(new Event("loadedmetadata"));
    const seek = root.querySelector("[data-seek]");
    seek.dispatchEvent(new Event("pointerdown"));
    seek.value = "70";
    seek.dispatchEvent(new Event("input"));
    video.dispatchEvent(new Event("timeupdate"));
    const whileScrubbing = {
      value: seek.value,
      currentTime: video.currentTime,
      duration: root.querySelector("[data-duration]").textContent,
    };
    seek.dispatchEvent(new Event("change"));
    video.dispatchEvent(new Event("click"));
    const afterVideoClick = root.querySelector('[data-action="play"]').getAttribute("aria-label");
    video.pause();
    root.querySelector('[data-action="play"]').click();
    document.dispatchEvent(new KeyboardEvent("keydown", { code: "Space", key: " " }));
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "m" }));
    root.querySelector('[data-action="fullscreen"]').click();
    const entered = root.querySelector('[data-action="fullscreen"]').getAttribute("aria-label");
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "f" }));
    const exited = root.querySelector('[data-action="fullscreen"]').getAttribute("aria-label");
    const result = {
      duration: root.querySelector("[data-duration]").textContent,
      playLabel: root.querySelector('[data-action="play"]').getAttribute("aria-label"),
      muted: video.muted,
      entered,
      exited,
      tabIndex: video.tabIndex,
      whileScrubbing,
      afterVideoClick,
    };
    controller.dispose();
    root.remove();
    return result;
  });
  expect(state).toEqual({
    duration: "−0:50",
    playLabel: "Play",
    muted: true,
    entered: "Exit fullscreen",
    exited: "Enter fullscreen",
    tabIndex: -1,
    whileScrubbing: { value: "70", currentTime: 70, duration: "−0:50" },
    afterVideoClick: "Pause",
  });
});

test("shared player joins automatically and keeps the real timeline range while scrubbing", async ({
  page,
}) => {
  await page.goto("/profiles");
  const state = await page.evaluate(async () => {
    await import("/lib/signalr/signalr.min.js");
    const calls = [];
    let stopped = 0;
    const connection = {
      on: () => {},
      onreconnecting: () => {},
      onreconnected: () => {},
      onclose: () => {},
      start: async () => {},
      stop: async () => {
        stopped += 1;
      },
      invoke: async (method) => {
        calls.push(method);
        return {
          revision: 1,
          anchorPositionSeconds: 20,
          serverAnchorTime: new Date().toISOString(),
          isPaused: false,
          playbackRate: 1,
          participants: ["Viewer"],
        };
      },
    };
    window.signalR = {
      HubConnectionBuilder: class {
        withUrl() {
          return this;
        }
        withAutomaticReconnect() {
          return this;
        }
        build() {
          return connection;
        }
      },
    };
    const { initialize, dispose } = await import("/js/sharedPlayer.js");
    const root = document.createElement("div");
    root.id = "shared-test-player";
    root.innerHTML = `<video></video><div class="player-status"></div><div class="player-controls">
      <button data-action="play"></button><span data-current></span><input data-seek type="range" min="0" max="0" value="0">
      <span data-duration></span><button data-action="mute"></button><input data-volume type="range" value="1"><button data-action="fullscreen"></button></div>`;
    document.body.append(root);
    const video = root.querySelector("video");
    Object.defineProperties(video, {
      duration: { configurable: true, get: () => 120 },
      currentTime: { configurable: true, writable: true, value: 20 },
      paused: { configurable: true, get: () => true },
    });
    video.pause = () => {};
    video.play = async () => {
      throw new Error("Autoplay blocked");
    };
    await initialize(
      "shared-test-player",
      "room",
      "00000000-0000-0000-0000-000000000000",
      0,
      0,
      {},
    );
    video.dispatchEvent(new Event("loadedmetadata"));
    const seek = root.querySelector("[data-seek]");
    seek.dispatchEvent(new Event("pointerdown"));
    seek.value = "70";
    seek.dispatchEvent(new Event("input"));
    video.dispatchEvent(new Event("timeupdate"));
    video.dispatchEvent(new Event("waiting"));
    await Promise.resolve();
    video.dispatchEvent(new Event("canplay"));
    await Promise.resolve();
    const result = {
      max: seek.max,
      duration: root.querySelector("[data-duration]").textContent,
      value: seek.value,
      currentTime: video.currentTime,
      calls,
      status: root.querySelector(".player-status").textContent,
      joinControl: root.querySelector('[data-action="join"]') !== null,
    };
    dispose("shared-test-player");
    result.stopped = stopped;
    root.remove();
    return result;
  });
  expect(state).toEqual({
    max: "120",
    duration: "−0:50",
    value: "70",
    currentTime: 70,
    calls: ["JoinRoom", "SetBuffering", "SetBuffering"],
    status: "Buffering locally; pausing the room…",
    joinControl: false,
    stopped: 1,
  });
});

test("switching identities clears the previously active session", async ({ page, context }) => {
  await seedCatalog(page);
  await selectProfile(page, "Alice");
  const aliceProfile = (await context.cookies()).find(
    (cookie) => cookie.name === "blockbuster.profile",
  )?.value;
  await selectProfile(page, "Bob");
  const bobProfile = (await context.cookies()).find(
    (cookie) => cookie.name === "blockbuster.profile",
  )?.value;
  expect(bobProfile).toBeTruthy();
  expect(bobProfile).not.toBe(aliceProfile);

  await page.goto("/admin/login");
  await page.locator("#admin-pin").fill("1234");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("heading", { name: "Administration" })).toBeVisible();
  expect((await context.cookies()).some((cookie) => cookie.name === "blockbuster.profile")).toBe(
    false,
  );
  expect((await context.cookies()).some((cookie) => cookie.name === "blockbuster.admin")).toBe(
    true,
  );

  await selectProfile(page, "Alice");
  expect((await context.cookies()).some((cookie) => cookie.name === "blockbuster.admin")).toBe(
    false,
  );
  expect((await context.cookies()).some((cookie) => cookie.name === "blockbuster.profile")).toBe(
    true,
  );
});
