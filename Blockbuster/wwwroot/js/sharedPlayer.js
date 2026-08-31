import { createPlayerController } from "./playerController.js";
import { createProgressWriter } from "./progressWriter.js";

const players = new Map();

async function connectHub(roomId, onState, onRoom, onStatus) {
  await import("/lib/signalr/signalr.min.js");

  let stopped = false;
  const connection = new window.signalR.HubConnectionBuilder()
    .withUrl("/hubs/shared-playback")
    .withAutomaticReconnect()
    .build();

  const join = async () => {
    const snapshot = await connection.invoke("JoinRoom", roomId);
    await onState(snapshot);
    onStatus("");
  };

  connection.on("StateChanged", onState);
  connection.on("RoomUpdated", onRoom);
  connection.onreconnecting(() => onStatus("Reconnecting…"));
  connection.onreconnected(async () => {
    try {
      await join();
    } catch {
      onStatus("Unable to rejoin the room.");
    }
  });
  connection.onclose(() => {
    if (!stopped) {
      onStatus("Room connection closed. Rejoin to continue.");
    }
  });

  try {
    await connection.start();
    await join();
  } catch (error) {
    await connection.stop();
    throw error;
  }

  return {
    command: (command) => connection.invoke("SendCommand", roomId, command),
    buffering: (isBuffering, positionSeconds) =>
      connection.invoke("SetBuffering", roomId, isBuffering, positionSeconds),
    async stop() {
      stopped = true;
      await connection.stop();
    },
  };
}

export async function initialize(id, roomId, movieId, resumeSeconds, initialRevision, settings) {
  const root = document.getElementById(id);
  if (!root) {
    return;
  }

  const participantLabel = document.querySelector("[data-participants]");
  const controllerLabel = document.querySelector("[data-controller]");
  const progressIntervalMs = settings?.progressIntervalMs ?? 10000;
  const resumeThresholdSeconds = settings?.resumeThresholdSeconds ?? 30;
  const driftIntervalMs = settings?.driftIntervalMs ?? 5000;
  const rateCorrectionThresholdSeconds = settings?.rateCorrectionThresholdSeconds ?? 0.75;
  const hardSeekThresholdSeconds = settings?.hardSeekThresholdSeconds ?? 3;

  let hub;
  let active = false;
  let suppress = false;
  let buffering = false;
  let progressTimer;
  let driftTimer;
  let snapshot;
  let writer;

  const expectedPosition = (state) => {
    const elapsed = state.isPaused
      ? 0
      : ((Date.now() - Date.parse(state.serverAnchorTime)) / 1000) * state.playbackRate;
    return Math.max(0, state.anchorPositionSeconds + elapsed);
  };

  const updateRoomLabels = (state) => {
    if (controllerLabel) {
      controllerLabel.textContent = state.lastControllingProfile
        ? `Latest action: ${state.lastControllingProfile}`
        : "Room ready. Any participant can control playback.";
    }

    if (participantLabel) {
      participantLabel.textContent = state.participants?.length
        ? state.participants.join(", ")
        : "Waiting for viewers";
    }
  };

  let controller;
  const apply = async (state) => {
    if (!state || (snapshot && state.revision < snapshot.revision)) {
      return;
    }

    snapshot = state;
    suppress = true;

    try {
      const target = expectedPosition(state);
      if (Math.abs(controller.video.currentTime - target) > 0.75) {
        controller.video.currentTime = target;
      }

      controller.video.playbackRate = state.playbackRate;
      if (state.isPaused) {
        controller.video.pause();
      } else {
        try {
          await controller.video.play();
        } catch {
          controller.setStatus("Playback is synchronized but blocked. Press play to resume.");
        }
      }

      controller.sync();
      updateRoomLabels(state);
    } finally {
      suppress = false;
    }
  };

  const send = async (isPaused) => {
    if (!active || suppress) {
      return;
    }

    await hub.command({
      isPaused,
      positionSeconds: controller.video.currentTime,
      playbackRate: 1,
    });
  };

  const reportBuffering = async (isBuffering) => {
    if (!active || suppress || buffering === isBuffering) {
      return;
    }

    buffering = isBuffering;
    try {
      await hub.buffering(isBuffering, controller.video.currentTime);
    } catch {
      buffering = false;
      controller.setStatus("Unable to synchronize buffering with the room.");
    }
  };

  controller = createPlayerController(root, {
    onReady(video) {
      if (
        resumeSeconds >= resumeThresholdSeconds &&
        !snapshot &&
        resumeSeconds < video.duration - 10
      ) {
        video.currentTime = resumeSeconds;
      }
    },
    async onPlay() {
      if (!active || suppress) {
        return;
      }

      writer.save("play");
      await send(false);
    },
    async onPause() {
      if (!active || suppress) {
        return;
      }

      writer.save("pause");
      await send(true);
    },
    onEnded() {
      if (active && !suppress) {
        writer.save("ended");
      }
    },
    onSeekComplete(video) {
      return send(video.paused);
    },
    async onBufferingChange(isBuffering, video) {
      if (isBuffering) {
        controller.setStatus("Buffering locally; pausing the room…");
        video.pause();
      }

      await reportBuffering(isBuffering);
    },
  });

  writer = createProgressWriter({
    movieId,
    initialRevision,
    getPosition: () => controller.video.currentTime,
    setStatus: controller.setStatus,
  });

  const joinRoom = async () => {
    controller.setStatus("Joining…");
    try {
      hub = await connectHub(roomId, apply, apply, controller.setStatus);
      active = true;
    } catch {
      controller.setStatus("Unable to join this shared room.");
      return;
    }

    progressTimer = setInterval(() => writer.save("progress"), progressIntervalMs);
    driftTimer = setInterval(() => {
      if (!snapshot || snapshot.isPaused || controller.video.paused) {
        return;
      }

      const drift = expectedPosition(snapshot) - controller.video.currentTime;
      if (Math.abs(drift) >= hardSeekThresholdSeconds) {
        controller.video.currentTime += drift;
      } else if (Math.abs(drift) >= rateCorrectionThresholdSeconds) {
        controller.video.playbackRate = drift > 0 ? 1.05 : 0.95;
      } else {
        controller.video.playbackRate = snapshot.playbackRate;
      }
    }, driftIntervalMs);
  };

  players.set(id, {
    controller,
    writer,
    async stop() {
      active = false;
      clearInterval(progressTimer);
      clearInterval(driftTimer);
      controller.video.pause();
      await writer.flush("progress");
      await controller.dispose();
      await hub?.stop();
    },
  });

  await joinRoom();
}

export async function dispose(id) {
  const player = players.get(id);
  if (!player) {
    return;
  }

  await player.stop();
  players.delete(id);
}
