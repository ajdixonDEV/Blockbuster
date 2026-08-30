const { test, expect } = require('@playwright/test');

test.describe.configure({ mode: 'serial' });

async function seedCatalog(page) {
  await page.goto('/admin/login');
  await page.locator('#admin-pin').fill('1234');
  await page.getByRole('button', { name: 'Sign in' }).click();
  for (const name of ['Alice', 'Bob']) {
    if (await page.getByText(name, { exact: true }).count() === 0) {
      await page.locator('form[action="/admin/profiles/create"] input[name="name"]').fill(name);
      await Promise.all([
        page.waitForURL('**/admin'),
        page.getByRole('button', { name: 'Create profile' }).click()
      ]);
    }
    await expect(page.locator(`input[aria-label="Profile name"][value="${name}"]`)).toBeVisible();
  }
  await page.getByRole('button', { name: 'Scan now' }).click();
  await expect(page.getByText('Browser Fixture (2024)', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Use local metadata' }).click();
}

async function selectProfile(page, name) {
  await page.goto('/profiles');
  await page.getByRole('button', { name: `Watch as ${name}` }).click();
  await expect(page.getByRole('heading', { name: 'Movies' })).toBeVisible();
}

test('starts the real application and renders profile selection', async ({ page }) => {
  await page.goto('/profiles');
  await expect(page).toHaveTitle(/Choose profile · Blockbuster/);
  await expect(page.getByRole('heading', { name: /choose a profile/i })).toBeVisible();
});

test('scans a real fixture and persists direct-play progress', async ({ page }) => {
  await seedCatalog(page);
  await selectProfile(page, 'Alice');
  await page.getByRole('link', { name: 'Browser Fixture' }).click();
  await page.getByRole('link', { name: 'Play' }).click();
  const video = page.locator('#movie-player video');
  await expect(video).toHaveJSProperty('readyState', 4);
  await page.locator('#movie-player [data-action="play"]').click();
  await expect(page.locator('#movie-player [data-action="play"]')).toHaveAttribute('aria-label', 'Pause');
  await page.waitForTimeout(250);
  await page.locator('#movie-player [data-action="play"]').click();
  await page.goto('/movies');
  await expect(page.locator('.poster-progress')).toBeVisible();
});

test('player controller keeps controls, keyboard shortcuts, and fullscreen state synchronized', async ({ page }) => {
  await page.goto('/profiles');
  const state = await page.evaluate(async () => {
    const { createPlayerController } = await import('/js/playerController.js');
    const root = document.createElement('div');
    root.innerHTML = `<video></video><div class="player-status"></div><div class="player-controls">
      <button data-action="play" aria-label="Play"></button><span data-current></span>
      <input data-seek type="range" min="0" max="0" value="0"><span data-duration></span>
      <button data-action="mute" aria-label="Mute"></button><input data-volume type="range" value="1">
      <button data-action="fullscreen" aria-label="Enter fullscreen"></button></div>`;
    document.body.append(root);
    const video = root.querySelector('video');
    let paused = true;
    Object.defineProperties(video, {
      paused: { configurable: true, get: () => paused },
      duration: { configurable: true, get: () => 120 },
      currentTime: { configurable: true, writable: true, value: 20 }
    });
    video.play = async () => { paused = false; video.dispatchEvent(new Event('play')); };
    video.pause = () => { paused = true; video.dispatchEvent(new Event('pause')); };
    let fullscreen = null;
    Object.defineProperties(document, {
      fullscreenEnabled: { configurable: true, get: () => true },
      fullscreenElement: { configurable: true, get: () => fullscreen }
    });
    root.requestFullscreen = async () => { fullscreen = root; document.dispatchEvent(new Event('fullscreenchange')); };
    document.exitFullscreen = async () => { fullscreen = null; document.dispatchEvent(new Event('fullscreenchange')); };
    const controller = createPlayerController(root);
    video.dispatchEvent(new Event('loadedmetadata'));
    root.querySelector('[data-action="play"]').click();
    document.dispatchEvent(new KeyboardEvent('keydown', { code: 'Space', key: ' ' }));
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'm' }));
    root.querySelector('[data-action="fullscreen"]').click();
    const entered = root.querySelector('[data-action="fullscreen"]').getAttribute('aria-label');
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'f' }));
    const exited = root.querySelector('[data-action="fullscreen"]').getAttribute('aria-label');
    const result = {
      duration: root.querySelector('[data-duration]').textContent,
      playLabel: root.querySelector('[data-action="play"]').getAttribute('aria-label'),
      muted: video.muted,
      entered,
      exited
    };
    controller.dispose();
    root.remove();
    return result;
  });
  expect(state).toEqual({ duration: '2:00', playLabel: 'Play', muted: true, entered: 'Exit fullscreen', exited: 'Enter fullscreen' });
});
