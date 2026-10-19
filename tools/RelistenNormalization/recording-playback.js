/* Exact source/file browsing shared by the venue and credited-person readers.
 * Load only when the server advertises recordingPlayback; older readers keep
 * their existing source link. No dates, names or sets determine file ownership.
 */
window.recordingTracks = function (sourceUuid) {
  const section = document.createElement("section");
  const toggle = document.createElement("button");
  toggle.className = "button";
  toggle.textContent = "Show tracks";
  toggle.type = "button";
  const content = document.createElement("div");
  section.append(toggle, content);
  let loaded = false;
  async function read(path, values) {
    const response = await fetch(path + "?" + new URLSearchParams(values));
    const result = await response.json();
    if (!response.ok) {
      throw Error(result.error || "Recording files unavailable.");
    }
    return result;
  }
  function text(tag, value) {
    const element = document.createElement(tag);
    element.textContent = value;
    return element;
  }
  async function load(offset = 0) {
    content.replaceChildren(text("p", "Loading tracks…"));
    try {
      const page = await read("/api/recording-files", {source_uuid: sourceUuid, offset});
      content.replaceChildren(text("p", page.total ? page.total.toLocaleString() + " files in this recording" : "No catalog tracks found. The original source evidence remains available."));
      for (const file of page.items) {
        const row = document.createElement("div");
        row.append(text("p", file.title));
        const play = text("button", "Play");
        play.className = "button";
        play.type = "button";
        row.append(play);
        play.onclick = async () => {
          play.disabled = true;
          try {
            const detail = await read("/api/recording-file", {source_uuid: sourceUuid, id: file.id});
            if (!detail.playback.length) {
              row.append(text("p", "Catalog audio is unavailable for this file. Open the original source evidence for provider links."));
              play.remove();
              return;
            }
            const audio = document.createElement("audio");
            audio.controls = true;
            audio.preload = "none";
            audio.style.width = "100%";
            audio.src = detail.playback[0];
            audio.onplay = () => {
              document.querySelectorAll("audio").forEach(other => {
                if (other !== audio) {
                  other.pause();
                }
              });
            };
            audio.onerror = () => {
              row.append(text("p", "This source’s audio could not load. Try another recording or open its original source evidence."));
              audio.onerror = null;
            };
            row.append(audio);
            play.remove();
            // Mobile browsers may require another tap after the detail request.
            audio.play().catch(() => {});
          } catch (error) {
            row.append(text("p", error.message));
            play.disabled = false;
          }
        };
        content.append(row);
      }
      const controls = document.createElement("div");
      for (const [label, next] of [["Previous tracks", page.offset - page.limit], ["More tracks", page.offset + page.limit]]) {
        if (next < 0 || next >= page.total) {
          continue;
        }
        const button = text("button", label);
        button.className = "button";
        button.type = "button";
        button.onclick = () => load(next);
        controls.append(button);
      }
      content.append(controls);
      loaded = true;
    } catch (error) {
      content.replaceChildren(text("p", error.message));
      loaded = false;
    }
  }
  toggle.onclick = () => {
    if (!loaded) {
      content.hidden = false;
      load();
    } else {
      content.hidden = !content.hidden;
    }
    toggle.textContent = content.hidden ? "Show tracks" : "Hide tracks";
  };
  return section;
};
