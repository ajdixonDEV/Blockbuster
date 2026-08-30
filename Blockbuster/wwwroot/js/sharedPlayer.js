const players = new Map();
const recordSeparator = String.fromCharCode(0x1e);

async function connectHub(roomId, onState, onRoom, onStatus) {
    let socket, stopped = false, reconnectTimer, invocation = 0, joined = false, handshakeReady;
    const pending = new Map();
    const send = message => socket?.readyState === WebSocket.OPEN && socket.send(JSON.stringify(message) + recordSeparator);
    const invoke = (target, args) => new Promise((resolve, reject) => {
        const invocationId = String(++invocation); pending.set(invocationId, { resolve, reject });
        send({ type: 1, invocationId, target, arguments: args });
    });
    const open = async () => {
        try {
            const response = await fetch('/hubs/shared-playback/negotiate?negotiateVersion=1', { method: 'POST' });
            if (!response.ok) throw new Error('Room connection was refused.');
            const negotiation = await response.json();
            const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
            const handshake = new Promise((resolve, reject) => { handshakeReady = { resolve, reject }; setTimeout(() => reject(new Error('Hub handshake timed out.')), 8000); });
            socket = new WebSocket(`${scheme}//${location.host}/hubs/shared-playback?id=${encodeURIComponent(negotiation.connectionToken)}`);
            socket.onopen = () => send({ protocol: 'json', version: 1 });
            socket.onmessage = event => {
                for (const frame of event.data.split(recordSeparator).filter(Boolean)) {
                    const message = JSON.parse(frame);
                    if (message.type === undefined) { message.error ? handshakeReady.reject(new Error(message.error)) : handshakeReady.resolve(); continue; }
                    if (message.type === 1 && message.target === 'StateChanged') onState(message.arguments[0]);
                    if (message.type === 1 && message.target === 'RoomUpdated') onRoom(message.arguments[0]);
                    if (message.type === 3 && pending.has(message.invocationId)) {
                        const waiter = pending.get(message.invocationId); pending.delete(message.invocationId);
                        message.error ? waiter.reject(new Error(message.error)) : waiter.resolve(message.result);
                    }
                    if (message.type === 6 && joined) send({ type: 6 });
                    if (message.type === 7) socket.close();
                }
            };
            socket.onclose = () => {
                joined = false;
                for (const waiter of pending.values()) waiter.reject(new Error('Disconnected')); pending.clear();
                if (!stopped) { onStatus('Reconnecting…'); reconnectTimer = setTimeout(open, 1500); }
            };
            await new Promise((resolve, reject) => { const started = Date.now(); const check = () => socket?.readyState === WebSocket.OPEN ? resolve() : Date.now() - started > 8000 ? reject(new Error('Timed out')) : setTimeout(check, 25); check(); });
            await handshake;
            const snapshot = await invoke('JoinRoom', [roomId]); joined = true; onState(snapshot); onStatus('');
        } catch (error) { onStatus(error.message || 'Unable to connect.'); if (!stopped) reconnectTimer = setTimeout(open, 2000); }
    };
    await open();
    return { command: command => invoke('SendCommand', [roomId, command]), stop: () => { stopped = true; clearTimeout(reconnectTimer); socket?.close(); } };
}

export async function initialize(id, roomId, movieId, resumeSeconds, initialRevision) {
    const root = document.getElementById(id), video = root?.querySelector('video'); if (!root || !video) return;
    const status = root.querySelector('.player-status'), join = root.querySelector('[data-action=join]'), play = root.querySelector('[data-action=play]');
    const seek = root.querySelector('[data-seek]'), current = root.querySelector('[data-current]'), duration = root.querySelector('[data-duration]');
    const participantLabel = document.querySelector('[data-participants]'), controllerLabel = document.querySelector('[data-controller]');
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
        progressTimer = setInterval(() => saveProgress('progress'), 10000);
        driftTimer = setInterval(() => {
            if (!snapshot || snapshot.isPaused || video.paused) return;
            const drift = expectedPosition(snapshot) - video.currentTime;
            if (Math.abs(drift) >= 3) video.currentTime += drift;
            else if (Math.abs(drift) >= .75) video.playbackRate = drift > 0 ? 1.05 : .95;
            else video.playbackRate = snapshot.playbackRate;
        }, 5000);
    };
    play.onclick = () => video.paused ? video.play().then(() => send(false)).catch(() => {}) : (video.pause(), send(true));
    seek.onchange = () => send(video.paused);
    seek.oninput = () => { video.currentTime = Number(seek.value) / 1000 * (video.duration || 0); };
    root.querySelector('[data-action=mute]').onclick = () => video.muted = !video.muted;
    root.querySelector('[data-volume]').oninput = event => video.volume = Number(event.target.value);
    root.querySelector('[data-action=fullscreen]').onclick = () => root.requestFullscreen?.();
    video.onloadedmetadata = () => { duration.textContent = time(video.duration); if (resumeSeconds >= 30 && !snapshot) video.currentTime = resumeSeconds; };
    video.ontimeupdate = () => { current.textContent = time(video.currentTime); seek.value = video.duration ? String(video.currentTime / video.duration * 1000) : '0'; };
    video.onplay = () => { play.textContent = '❚❚'; if (active && !suppress) saveProgress('play'); };
    video.onpause = () => { play.textContent = '▶'; if (active && !suppress) saveProgress('pause'); };
    video.onwaiting = () => status.textContent = 'Buffering locally…';
    video.onplaying = () => { if (!status.textContent.includes('blocked')) status.textContent = ''; };
    players.set(id, { video, stop: () => { active = false; clearInterval(progressTimer); clearInterval(driftTimer); hub?.stop(); saveProgress('progress'); } });
}

export function dispose(id) { const player = players.get(id); if (!player) return; player.stop(); player.video.pause(); players.delete(id); }
