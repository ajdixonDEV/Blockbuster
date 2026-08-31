export function createPlayerController(root, options = {}) {
  const video = root.querySelector("video");
  const playButton = root.querySelector("[data-action=play]");
  const muteButton = root.querySelector("[data-action=mute]");
  const fullscreenButton = root.querySelector("[data-action=fullscreen]");
  const seek = root.querySelector("[data-seek]");
  const volume = root.querySelector("[data-volume]");
  const current = root.querySelector("[data-current]");
  const duration = root.querySelector("[data-duration]");
  const status = root.querySelector(".player-status");

  let hideTimer;
  let scrubbing = false;

  const formatTime = (value) => {
    const normalized = Number.isFinite(value) ? Math.max(0, value) : 0;
    const minutes = Math.floor(normalized / 60);
    const seconds = String(Math.floor(normalized % 60)).padStart(2, "0");
    return `${minutes}:${seconds}`;
  };

  const setStatus = (value) => {
    status.textContent = value || "";
  };

  const emit = (name, ...arguments_) => {
    const handler = options[name];
    if (!handler) {
      return;
    }

    Promise.resolve(handler(...arguments_)).catch(() => {
      setStatus("Playback synchronization failed.");
    });
  };

  const updateDuration = () => {
    const value = Number.isFinite(video.duration) ? video.duration : 0;
    seek.max = String(value);
    duration.textContent = `−${formatTime(Math.max(0, value - (video.currentTime || 0)))}`;
  };

  const sync = () => {
    const paused = video.paused;
    const muted = video.muted || video.volume === 0;

    playButton.textContent = paused ? "▶" : "❚❚";
    playButton.setAttribute("aria-label", paused ? "Play" : "Pause");
    playButton.setAttribute("aria-pressed", String(!paused));
    muteButton.textContent = muted ? "🔇" : "🔊";
    muteButton.setAttribute("aria-label", muted ? "Unmute" : "Mute");
    muteButton.setAttribute("aria-pressed", String(video.muted));
    volume.value = String(video.muted ? 0 : video.volume);
    current.textContent = formatTime(video.currentTime || 0);

    if (!scrubbing) {
      seek.value = String(video.currentTime || 0);
    }

    updateDuration();
  };

  const show = () => {
    clearTimeout(hideTimer);
    root.classList.remove("controls-hidden");

    if (document.fullscreenElement === root && !video.paused) {
      hideTimer = setTimeout(() => root.classList.add("controls-hidden"), 3000);
    }
  };

  const fullscreenChanged = () => {
    const active = document.fullscreenElement === root;
    fullscreenButton.setAttribute("aria-label", active ? "Exit fullscreen" : "Enter fullscreen");
    fullscreenButton.setAttribute("aria-pressed", String(active));
    show();
    emit("onFullscreenChange", active, video);
  };

  const toggleFullscreen = () => {
    if (document.fullscreenElement === root) {
      return document.exitFullscreen?.();
    }

    return root.requestFullscreen?.();
  };

  const togglePlayback = () => {
    if (video.paused) {
      return video.play();
    }

    video.pause();
    return undefined;
  };

  const seekBy = (seconds) => {
    video.currentTime = Math.max(
      0,
      Math.min(video.duration || Infinity, video.currentTime + seconds),
    );
    sync();
    emit("onSeekComplete", video);
  };

  const handleKey = (event) => {
    if (["INPUT", "TEXTAREA", "SELECT"].includes(event.target.tagName)) {
      return;
    }

    show();

    if (event.code === "Space") {
      event.preventDefault();
      togglePlayback();
    } else if (event.key === "ArrowLeft") {
      seekBy(-10);
    } else if (event.key === "ArrowRight") {
      seekBy(10);
    } else if (event.key.toLowerCase() === "m") {
      video.muted = !video.muted;
    } else if (event.key.toLowerCase() === "f" && !fullscreenButton.disabled) {
      toggleFullscreen();
    }
  };

  const ready = () => {
    updateDuration();
    emit("onReady", video);
    sync();
    emit("onPlaybackEvent", "ready", video);
  };

  const beginScrub = () => {
    scrubbing = true;
  };

  const updateScrub = () => {
    scrubbing = true;
    video.currentTime = Number(seek.value);
    current.textContent = formatTime(video.currentTime);
    updateDuration();
  };

  const finishScrub = () => {
    if (!scrubbing) {
      return;
    }

    scrubbing = false;
    sync();
    emit("onSeekComplete", video);
  };

  const toggleMute = () => {
    video.muted = !video.muted;
  };

  const updateVolume = () => {
    video.muted = false;
    video.volume = Number(volume.value);
  };

  const handlePlay = () => {
    sync();
    show();
    emit("onPlay", video);
    emit("onPlaybackEvent", "play", video);
  };

  const handlePause = () => {
    sync();
    root.classList.remove("controls-hidden");
    emit("onPause", video);
    emit("onPlaybackEvent", "pause", video);
  };

  const handleEnded = () => {
    emit("onEnded", video);
    emit("onPlaybackEvent", "ended", video);
  };

  const handleWaiting = () => {
    setStatus("Buffering…");
    emit("onBufferingChange", true, video);
    emit("onPlaybackEvent", "waiting", video);
  };

  const handleCanPlay = () => {
    emit("onBufferingChange", false, video);
    emit("onPlaybackEvent", "canplay", video);
  };

  const handlePlaying = () => {
    setStatus("");
    show();
    emit("onBufferingChange", false, video);
    emit("onPlaybackEvent", "playing", video);
  };

  const handleError = () => {
    setStatus("Playback failed. Check this version’s browser compatibility.");
    emit("onPlaybackEvent", "error", video);
  };

  playButton.addEventListener("click", togglePlayback);
  muteButton.addEventListener("click", toggleMute);
  fullscreenButton.addEventListener("click", toggleFullscreen);
  seek.addEventListener("pointerdown", beginScrub);
  seek.addEventListener("input", updateScrub);
  seek.addEventListener("change", finishScrub);
  seek.addEventListener("pointerup", finishScrub);
  volume.addEventListener("input", updateVolume);
  video.addEventListener("click", togglePlayback);
  video.addEventListener("loadedmetadata", ready);
  video.addEventListener("durationchange", sync);
  video.addEventListener("timeupdate", sync);
  video.addEventListener("volumechange", sync);
  video.addEventListener("play", handlePlay);
  video.addEventListener("pause", handlePause);
  video.addEventListener("ended", handleEnded);
  video.addEventListener("waiting", handleWaiting);
  video.addEventListener("canplay", handleCanPlay);
  video.addEventListener("playing", handlePlaying);
  video.addEventListener("error", handleError);
  root.addEventListener("pointermove", show);
  root.addEventListener("pointerdown", show);
  document.addEventListener("keydown", handleKey);
  document.addEventListener("fullscreenchange", fullscreenChanged);

  fullscreenButton.disabled = !document.fullscreenEnabled;
  if (fullscreenButton.disabled) {
    fullscreenButton.setAttribute("aria-label", "Fullscreen unavailable");
  }

  sync();
  if (video.readyState >= HTMLMediaElement.HAVE_METADATA) {
    ready();
  }

  return {
    video,
    setStatus,
    show,
    sync,
    async dispose() {
      clearTimeout(hideTimer);
      playButton.removeEventListener("click", togglePlayback);
      muteButton.removeEventListener("click", toggleMute);
      fullscreenButton.removeEventListener("click", toggleFullscreen);
      seek.removeEventListener("pointerdown", beginScrub);
      seek.removeEventListener("input", updateScrub);
      seek.removeEventListener("change", finishScrub);
      seek.removeEventListener("pointerup", finishScrub);
      volume.removeEventListener("input", updateVolume);
      video.removeEventListener("click", togglePlayback);
      video.removeEventListener("loadedmetadata", ready);
      video.removeEventListener("durationchange", sync);
      video.removeEventListener("timeupdate", sync);
      video.removeEventListener("volumechange", sync);
      video.removeEventListener("play", handlePlay);
      video.removeEventListener("pause", handlePause);
      video.removeEventListener("ended", handleEnded);
      video.removeEventListener("waiting", handleWaiting);
      video.removeEventListener("canplay", handleCanPlay);
      video.removeEventListener("playing", handlePlaying);
      video.removeEventListener("error", handleError);
      document.removeEventListener("keydown", handleKey);
      document.removeEventListener("fullscreenchange", fullscreenChanged);
      root.removeEventListener("pointermove", show);
      root.removeEventListener("pointerdown", show);
      root.classList.remove("controls-hidden");
    },
  };
}
