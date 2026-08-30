import { createPlayerController } from './playerController.js';
const players = new Map();
export function initialize(id, movieId, resumeSeconds, initialRevision) {
    const root = document.getElementById(id); if (!root) return;
    let revision = initialRevision, timer, saving = Promise.resolve();
    const save = eventType => saving = saving.then(async () => {
        const player = players.get(id); if (!player) return;
        try { const response = await fetch(`/api/movies/${movieId}/progress`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ positionSeconds: player.video.currentTime, expectedRevision: revision, eventType }), keepalive: eventType !== 'progress' }); const result = await response.json(); revision = result.revision; if (response.status === 409 && Math.abs(player.video.currentTime - result.positionSeconds) > 5) player.setStatus('Progress was updated on another device.'); } catch { player.setStatus('Progress could not be saved.'); }
    });
    const player = createPlayerController(root, { onReady: video => { if (resumeSeconds >= 30 && resumeSeconds < video.duration - 10) video.currentTime = resumeSeconds; }, onPlay: () => { save('play'); clearInterval(timer); timer = setInterval(() => save('progress'), 10000); }, onPause: () => { clearInterval(timer); save('pause'); }, onEnded: () => { clearInterval(timer); save('ended'); } });
    players.set(id, player);
}
export function dispose(id) { const player = players.get(id); if (!player) return; player.dispose(); player.video.pause(); players.delete(id); }
