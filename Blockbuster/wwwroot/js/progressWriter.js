function requestToken() {
  const token = document.querySelector('meta[name="csrf-token"]')?.content;
  if (!token) {
    throw new Error("The antiforgery request token is unavailable.");
  }

  return token;
}

export function createProgressWriter(options) {
  let revision = options.initialRevision;
  let pending = Promise.resolve();

  const save = (eventType) => {
    const positionSeconds = options.getPosition();

    pending = pending.then(async () => {
      try {
        const response = await fetch(`/api/movies/${options.movieId}/progress`, {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "X-CSRF-TOKEN": requestToken(),
          },
          body: JSON.stringify({
            positionSeconds,
            expectedRevision: revision,
            eventType,
          }),
          keepalive: eventType !== "progress",
        });
        const result = await response.json();

        if (!Number.isInteger(result.revision)) {
          throw new Error("The progress response did not contain a revision.");
        }

        revision = result.revision;
        if (response.status === 409 && Math.abs(positionSeconds - result.positionSeconds) > 5) {
          options.setStatus("Progress was updated on another device.");
        } else if (!response.ok) {
          options.setStatus("Progress could not be saved.");
        }

        return result;
      } catch {
        options.setStatus("Progress could not be saved.");
        return undefined;
      }
    });

    return pending;
  };

  return {
    save,
    flush(eventType = "progress") {
      return save(eventType);
    },
    get revision() {
      return revision;
    },
  };
}
