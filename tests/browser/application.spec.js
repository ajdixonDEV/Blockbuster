const { test, expect } = require("@playwright/test");

test.describe.configure({ mode: "serial" });

function hasProfile(page, name) {
  return page
    .locator('input[aria-label="Profile name"]')
    .evaluateAll((inputs, expected) => inputs.some((input) => input.value === expected), name);
}

async function submitForm(page, action, button) {
  const [response] = await Promise.all([
    page.waitForResponse(
      (candidate) =>
        new URL(candidate.url()).pathname === action && candidate.request().method() === "POST",
    ),
    button.click(),
  ]);
  expect(response.status()).toBe(302);
}

async function seedCatalog(page) {
  await page.goto("/admin/login");
  await page.waitForLoadState("networkidle");
  await page.locator("#admin-pin").fill("1234");
  await submitForm(page, "/auth/admin/login", page.getByRole("button", { name: "Sign in" }));
  await expect(page).toHaveURL(/\/admin$/);
  await page.waitForLoadState("networkidle");
  for (const name of ["Alice", "Bob"]) {
    if (!(await hasProfile(page, name))) {
      await page.locator('form[action="/admin/profiles/create"] input[name="name"]').fill(name);
      await submitForm(
        page,
        "/admin/profiles/create",
        page.getByRole("button", { name: "Create profile" }),
      );
      await expect(page).toHaveURL(/\/admin$/);
      await page.waitForLoadState("networkidle");
    }
    await expect.poll(() => hasProfile(page, name)).toBe(true);
  }
  if ((await page.locator(".scan-run").count()) === 0) {
    await submitForm(page, "/admin/scans/request", page.getByRole("button", { name: "Scan now" }));
    await expect(page).toHaveURL(/\/admin$/);
    await page.waitForLoadState("networkidle");
    await expect(page.getByText("Browser Fixture (2024)", { exact: true })).toBeVisible();
    const useLocalMetadata = page.getByRole("button", { name: "Use local metadata" });
    await expect(useLocalMetadata).toBeVisible();
    await submitForm(page, "/admin/matches/local", useLocalMetadata);
    await expect(page).toHaveURL(/\/admin$/);
  }
}

async function selectProfile(page, name) {
  await page.goto("/profiles");
  await page.waitForLoadState("networkidle");
  await submitForm(
    page,
    "/auth/profile/select",
    page.getByRole("button", { name: `Watch as ${name}` }),
  );
  await expect(page.getByRole("heading", { name: "Movies", exact: true })).toBeVisible();
}

test("starts the real application and renders profile selection", async ({ page }) => {
  await page.goto("/profiles");
  await expect(page).toHaveTitle(/Choose profile · Blockbuster/);
  await expect(page.getByRole("heading", { name: /choose a profile/i })).toBeVisible();

  await expect(
    page.locator(
      [
        'a:not([draggable="false"])',
        'button:not([draggable="false"])',
        'img:not([draggable="false"])',
        'input:not([draggable="false"])',
        'select:not([draggable="false"])',
        'video:not([draggable="false"])',
      ].join(", "),
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
    const token = document.querySelector('meta[name="csrf-token"]')?.content;
    const post = (positionSeconds) =>
      fetch(`/api/movies/${movieId}/progress`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-CSRF-TOKEN": token,
        },
        body: JSON.stringify({
          positionSeconds,
          expectedRevision: 0,
          eventType: "browser-conflict",
        }),
      }).then(async (response) => ({ status: response.status, body: await response.json() }));
    return Promise.all([post(31), post(32)]);
  });
  expect(conflicts.map((item) => item.status).sort()).toEqual([200, 409]);
  expect(Math.max(...conflicts.map((item) => item.body.revision))).toBeGreaterThan(0);
  const missingTokenStatus = await page.evaluate(async () => {
    const movieId = new URL(location.href).pathname.split("/")[2];
    const response = await fetch(`/api/movies/${movieId}/progress`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        positionSeconds: 3,
        expectedRevision: 0,
        eventType: "missing-antiforgery-token",
      }),
    });
    return response.status;
  });
  expect(missingTokenStatus).toBe(400);
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
  const finalProgressStatus = await page.evaluate(async () => {
    const movieId = new URL(location.href).pathname.split("/")[2];
    const token = document.querySelector('meta[name="csrf-token"]')?.content;
    let expectedRevision = 0;

    for (let attempt = 0; attempt < 5; attempt += 1) {
      const response = await fetch(`/api/movies/${movieId}/progress`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-CSRF-TOKEN": token,
        },
        body: JSON.stringify({
          positionSeconds: 31,
          expectedRevision,
          eventType: "browser-final-progress",
        }),
      });
      const result = await response.json();
      if (response.ok) {
        return response.status;
      }

      expectedRevision = result.revision;
    }

    return 409;
  });
  expect(finalProgressStatus).toBe(200);
  await page.goto("/movies");
  await expect(page.getByRole("link", { name: "Browser Fixture" })).toBeVisible();
  await expect(page.locator(".poster-progress")).toHaveCount(0);
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
    const hookEvents = [];
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
    const controller = createPlayerController(root, {
      onPlay: () => hookEvents.push("play"),
      onPause: () => hookEvents.push("pause"),
      onSeekComplete: () => hookEvents.push("seek"),
      onBufferingChange: (isBuffering) => hookEvents.push(`buffering:${isBuffering}`),
      onFullscreenChange: (isFullscreen) => hookEvents.push(`fullscreen:${isFullscreen}`),
    });
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
    video.dispatchEvent(new Event("waiting"));
    video.dispatchEvent(new Event("canplay"));
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
      hookEvents,
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
    hookEvents: [
      "seek",
      "play",
      "pause",
      "play",
      "pause",
      "buffering:true",
      "buffering:false",
      "fullscreen:true",
      "fullscreen:false",
    ],
  });

  const progressState = await page.evaluate(async () => {
    const originalFetch = window.fetch;
    const requests = [];
    const responders = [];
    let position = 10;
    const statuses = [];
    window.fetch = async (_url, options) => {
      requests.push(JSON.parse(options.body));
      return new Promise((resolve) => responders.push(resolve));
    };

    try {
      const { createProgressWriter } = await import("/js/progressWriter.js");
      const writer = createProgressWriter({
        movieId: "00000000-0000-0000-0000-000000000000",
        initialRevision: 0,
        getPosition: () => position,
        setStatus: (status) => statuses.push(status),
      });
      const first = writer.save("play");
      position = 40;
      const second = writer.save("progress");
      await Promise.resolve();
      const activeBeforeFirstResponse = requests.length;
      responders.shift()(new Response(JSON.stringify({ revision: 1, positionSeconds: 10 })));
      await first;
      await Promise.resolve();
      const secondRequest = requests[1];
      responders.shift()(
        new Response(JSON.stringify({ revision: 5, positionSeconds: 20 }), { status: 409 }),
      );
      await second;

      return {
        activeBeforeFirstResponse,
        eventTypes: requests.map((request) => request.eventType),
        expectedRevisions: requests.map((request) => request.expectedRevision),
        secondPosition: secondRequest.positionSeconds,
        revision: writer.revision,
        statuses,
      };
    } finally {
      window.fetch = originalFetch;
    }
  });
  expect(progressState).toEqual({
    activeBeforeFirstResponse: 1,
    eventTypes: ["play", "progress"],
    expectedRevisions: [0, 1],
    secondPosition: 40,
    revision: 5,
    statuses: ["Progress was updated on another device."],
  });

  const disposalState = await page.evaluate(async () => {
    const originalFetch = window.fetch;
    let respond;
    let request;
    window.fetch = async (_url, options) => {
      request = JSON.parse(options.body);
      return new Promise((resolve) => {
        respond = resolve;
      });
    };

    const root = document.createElement("div");
    root.id = "dispose-test-player";
    root.innerHTML = `<video></video><div class="player-status"></div><div class="player-controls">
      <button data-action="play"></button><span data-current></span><input data-seek type="range">
      <span data-duration></span><button data-action="mute"></button><input data-volume type="range">
      <button data-action="fullscreen"></button></div>`;
    document.body.append(root);

    try {
      const moviePlayer = await import("/js/moviePlayer.js");
      moviePlayer.initialize(root.id, "00000000-0000-0000-0000-000000000000", 0, 0, {});
      let disposed = false;
      const disposal = moviePlayer.dispose(root.id).then(() => {
        disposed = true;
      });
      await Promise.resolve();
      const awaitedResponse = !disposed;
      respond(new Response(JSON.stringify({ revision: 1, positionSeconds: 0 })));
      await disposal;
      return { awaitedResponse, disposed, eventType: request.eventType };
    } finally {
      root.remove();
      window.fetch = originalFetch;
    }
  });
  expect(disposalState).toEqual({
    awaitedResponse: true,
    disposed: true,
    eventType: "progress",
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
      <span data-duration></span><button data-action="mute"></button>
      <input data-volume type="range" value="1"><button data-action="fullscreen"></button></div>`;
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
    await dispose("shared-test-player");
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
  await page.waitForLoadState("networkidle");
  await page.locator("#admin-pin").fill("1234");
  await submitForm(page, "/auth/admin/login", page.getByRole("button", { name: "Sign in" }));
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
