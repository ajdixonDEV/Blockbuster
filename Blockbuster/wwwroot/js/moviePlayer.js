const players = new Map();

export function initialize(id, movieId, resumeSeconds, initialRevision) {
    const root = document.getElementById(id), video = root?.querySelector('video');
    if (!root || !video) return;
    const play = root.querySelector('[data-action=play]'), mute = root.querySelector('[data-action=mute]'), seek = root.querySelector('[data-seek]'), volume = root.querySelector('[data-volume]'), current = root.querySelector('[data-current]'), duration = root.querySelector('[data-duration]'), status = root.querySelector('.player-status');
    let revision = initialRevision, timer, hideTimer, disposed = false;
    const time = value => `${Math.floor(value / 60)}:${String(Math.floor(value % 60)).padStart(2, '0')}`;
    const save = async eventType => {
        if (disposed) return;
        try {
            const response = await fetch(`/api/movies/${movieId}/progress`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ positionSeconds: video.currentTime, expectedRevision: revision, eventType }), keepalive: eventType !== 'progress' });
            const result = await response.json(); revision = result.revision;
            if (response.status === 409 && Math.abs(video.currentTime - result.positionSeconds) > 5) status.textContent = 'Progress was updated on another device.';
        } catch { status.textContent = 'Progress could not be saved.'; }
    };
    const keepControlsVisible = () => { clearTimeout(hideTimer); root.classList.remove('controls-hidden'); };
    const showControls = () => { keepControlsVisible(); if (document.fullscreenElement === root && !video.paused) hideTimer = setTimeout(() => root.classList.add('controls-hidden'), 3000); };
    const fullscreenChanged = () => document.fullscreenElement === root ? showControls() : keepControlsVisible();
    const toggle = () => video.paused ? video.play() : video.pause();
    const key = event => {
        showControls();
        if (['INPUT', 'TEXTAREA', 'SELECT'].includes(event.target.tagName)) return;
        if (event.code === 'Space') { event.preventDefault(); toggle(); }
        if (event.key === 'ArrowLeft') video.currentTime = Math.max(0, video.currentTime - 10);
        if (event.key === 'ArrowRight') video.currentTime = Math.min(video.duration || Infinity, video.currentTime + 10);
        if (event.key.toLowerCase() === 'm') video.muted = !video.muted;
        if (event.key.toLowerCase() === 'f') root.requestFullscreen?.();
    };
    play.onclick = toggle; mute.onclick = () => video.muted = !video.muted; root.querySelector('[data-action=fullscreen]').onclick = () => root.requestFullscreen?.();
    volume.oninput = () => video.volume = Number(volume.value); seek.oninput = () => video.currentTime = (Number(seek.value) / 1000) * (video.duration || 0);
    video.onloadedmetadata = () => { duration.textContent = time(video.duration); if (resumeSeconds >= 30 && resumeSeconds < video.duration - 10) video.currentTime = resumeSeconds; };
    video.ontimeupdate = () => { current.textContent = time(video.currentTime); seek.value = video.duration ? String(video.currentTime / video.duration * 1000) : '0'; };
    video.onplay = () => { play.textContent = '❚❚'; status.textContent = ''; save('play'); timer = setInterval(() => save('progress'), 10000); showControls(); };
    video.onpause = () => { play.textContent = '▶'; clearInterval(timer); keepControlsVisible(); save('pause'); };
    video.onended = () => { clearInterval(timer); keepControlsVisible(); save('ended'); };
    video.onwaiting = () => { keepControlsVisible(); status.textContent = 'Buffering…'; };
    video.onplaying = () => { status.textContent = ''; showControls(); };
    video.onerror = () => { keepControlsVisible(); status.textContent = 'Playback failed. Check this version’s browser compatibility.'; };
    root.addEventListener('pointermove', showControls); root.addEventListener('pointerdown', showControls); document.addEventListener('fullscreenchange', fullscreenChanged); document.addEventListener('keydown', key);
    players.set(id, { video, key, save, showControls, fullscreenChanged, stop: () => { clearInterval(timer); clearTimeout(hideTimer); }, setDisposed: () => disposed = true });
}

export function dispose(id) {
    const player = players.get(id); if (!player) return;
    player.stop(); player.save('progress'); player.setDisposed();
    document.removeEventListener('keydown', player.key); document.removeEventListener('fullscreenchange', player.fullscreenChanged);
    const root = document.getElementById(id); root?.removeEventListener('pointermove', player.showControls); root?.removeEventListener('pointerdown', player.showControls); root?.classList.remove('controls-hidden');
    player.video.pause(); players.delete(id);
}
