const players = new Map();
async function connectHub(roomId, onState, onRoom, onStatus) {
    await import('/lib/signalr/signalr.min.js');
    let stopped = false;
    const connection = new window.signalR.HubConnectionBuilder()
        .withUrl('/hubs/shared-playback')
        .withAutomaticReconnect()
        .build();
    const join = async () => {
        const snapshot = await connection.invoke('JoinRoom', roomId);
        onState(snapshot);
        onStatus('');
    };
    connection.on('StateChanged', onState);
    connection.on('RoomUpdated', onRoom);
    connection.onreconnecting(() => onStatus('Reconnecting…'));
    connection.onreconnected(async () => { try { await join(); } catch { onStatus('Unable to rejoin the room.'); } });
    connection.onclose(() => { if (!stopped) onStatus('Room connection closed. Rejoin to continue.'); });
    try { await connection.start(); await join(); }
    catch (error) { await connection.stop(); throw error; }
    return { command: command => connection.invoke('SendCommand', roomId, command), stop: () => { stopped = true; return connection.stop(); } };
}

export async function initialize(id, roomId, movieId, resumeSeconds, initialRevision, settings) {
    const root = document.getElementById(id), video = root?.querySelector('video'); if (!root || !video) return;
    const status = root.querySelector('.player-status'), join = root.querySelector('[data-action=join]'), play = root.querySelector('[data-action=play]');
    const seek = root.querySelector('[data-seek]'), current = root.querySelector('[data-current]'), duration = root.querySelector('[data-duration]');
    const participantLabel = document.querySelector('[data-participants]'), controllerLabel = document.querySelector('[data-controller]');
    const progressIntervalMs = settings?.progressIntervalMs ?? 10000, resumeThresholdSeconds = settings?.resumeThresholdSeconds ?? 30, driftIntervalMs = settings?.driftIntervalMs ?? 5000, rateCorrectionThresholdSeconds = settings?.rateCorrectionThresholdSeconds ?? .75, hardSeekThresholdSeconds = settings?.hardSeekThresholdSeconds ?? 3;
    let hub, active = false, suppress = false, revision = initialRevision, progressTimer, driftTimer, snapshot;
    const time = value => `${Math.floor(value / 60)}:${String(Math.floor(value % 60)).padStart(2, '0')}`;
    const expectedPosition = state => Math.max(0, state.anchorPositionSeconds + (state.isPaused ? 0 : (Date.now() - Date.parse(state.serverAnchorTime)) / 1000 * state.playbackRate));
    const saveProgress = async eventType => { try { const response = await fetch(`/api/movies/${movieId}/progress`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ positionSeconds: video.currentTime, expectedRevision: revision, eventType }), keepalive: eventType !== 'progress' }); const result = await response.json(); revision = result.revision; } catch { status.textContent = 'Progress could not be saved.'; } };
    const apply = async state => {
        if (!state || (snapshot && state.revision < snapshot.revision)) return; snapshot = state;
        const target = expectedPosition(state); suppress = true;
        if (Math.abs(video.currentTime - target) > .75) video.currentTime = target;
        video.playbackRate = state.playbackRate;
        if (state.isPaused) video.pause(); else try { await video.play(); } catch { status.textContent = 'Playback is synchronized but blocked. Press play to resume.'; }
        play.textContent = state.isPaused ? '▶' : '❚❚'; suppress = false;
        if (controllerLabel) controllerLabel.textContent = state.lastControllingProfile ? `Latest action: ${state.lastControllingProfile}` : 'Room ready. Any participant can control playback.';
        if (participantLabel) participantLabel.textContent = state.participants?.length ? state.participants.join(', ') : 'Waiting for viewers';
    };
    const send = async isPaused => { if (!active || suppress) return; await hub.command({ isPaused, positionSeconds: video.currentTime, playbackRate: 1 }); };
    join.onclick = async () => {
        join.disabled = true; status.textContent = 'Joining…';
        hub = await connectHub(roomId, apply, apply, text => status.textContent = text); active = true; join.remove();
        progressTimer = setInterval(() => saveProgress('progress'), progressIntervalMs);
        driftTimer = setInterval(() => {
            if (!snapshot || snapshot.isPaused || video.paused) return;
            const drift = expectedPosition(snapshot) - video.currentTime;
            if (Math.abs(drift) >= hardSeekThresholdSeconds) video.currentTime += drift;
            else if (Math.abs(drift) >= rateCorrectionThresholdSeconds) video.playbackRate = drift > 0 ? 1.05 : .95;
            else video.playbackRate = snapshot.playbackRate;
        }, driftIntervalMs);
    };
    play.onclick = () => video.paused ? video.play().then(() => send(false)).catch(() => {}) : (video.pause(), send(true));
    seek.onchange = () => send(video.paused);
    seek.oninput = () => { video.currentTime = Number(seek.value) / 1000 * (video.duration || 0); };
    root.querySelector('[data-action=mute]').onclick = () => video.muted = !video.muted;
    root.querySelector('[data-volume]').oninput = event => video.volume = Number(event.target.value);
    root.querySelector('[data-action=fullscreen]').onclick = () => root.requestFullscreen?.();
    video.onloadedmetadata = () => { duration.textContent = time(video.duration); if (resumeSeconds >= resumeThresholdSeconds && !snapshot) video.currentTime = resumeSeconds; };
    video.ontimeupdate = () => { current.textContent = time(video.currentTime); seek.value = video.duration ? String(video.currentTime / video.duration * 1000) : '0'; };
    video.onplay = () => { play.textContent = '❚❚'; if (active && !suppress) saveProgress('play'); };
    video.onpause = () => { play.textContent = '▶'; if (active && !suppress) saveProgress('pause'); };
    video.onwaiting = () => status.textContent = 'Buffering locally…';
    video.onplaying = () => { if (!status.textContent.includes('blocked')) status.textContent = ''; };
    players.set(id, { video, stop: () => { active = false; clearInterval(progressTimer); clearInterval(driftTimer); hub?.stop(); saveProgress('progress'); } });
}

export function dispose(id) { const player = players.get(id); if (!player) return; player.stop(); player.video.pause(); players.delete(id); }
