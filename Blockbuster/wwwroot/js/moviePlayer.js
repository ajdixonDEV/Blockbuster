import { createPlayerController } from "./playerController.js";
import { createProgressWriter } from "./progressWriter.js";

const players = new Map();

export function initialize(id, movieId, resumeSeconds, initialRevision, settings) {
  const root = document.getElementById(id);
  if (!root) {
    return;
  }

  const progressIntervalMs = settings?.progressIntervalMs ?? 10000;
  const resumeThresholdSeconds = settings?.resumeThresholdSeconds ?? 30;
  let timer;
  let writer;

  const controller = createPlayerController(root, {
    onReady(video) {
      if (resumeSeconds >= resumeThresholdSeconds && resumeSeconds < video.duration - 10) {
        video.currentTime = resumeSeconds;
      }
    },
    onPlay() {
      writer.save("play");
      clearInterval(timer);
      timer = setInterval(() => writer.save("progress"), progressIntervalMs);
    },
    onPause() {
      clearInterval(timer);
      writer.save("pause");
    },
    onEnded() {
      clearInterval(timer);
      writer.save("ended");
    },
  });

  writer = createProgressWriter({
    movieId,
    initialRevision,
    getPosition: () => controller.video.currentTime,
    setStatus: controller.setStatus,
  });

  players.set(id, {
    controller,
    writer,
    clearTimer: () => clearInterval(timer),
  });
}

export async function dispose(id) {
  const player = players.get(id);
  if (!player) {
    return;
  }

  player.clearTimer();
  player.controller.video.pause();
  await player.writer.flush("progress");
  await player.controller.dispose();
  players.delete(id);
}
