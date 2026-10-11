(() => {
  "use strict";

  // Presentation-only examples. This demo has no service connections or Work state.
  const scenes = [
    {
      id: "slack",
      name: "Slack",
      location: "#development",
      sender: "Josh Aspinwall",
      prompts: [
        {
          label: "Investigate a bug",
          request: "Hey Goblin, can you take a look at the login bug?",
          reply: "On it. I’ll trace the login flow and bring a proposed fix back here for review.",
          receipt: "A clear plan. Right in the thread.",
        },
        {
          label: "Plan a small feature",
          request: "Hey Goblin, how should we add a dark mode?",
          reply:
            "Let’s start with shared color tokens, then a theme switch. I’ll put together a small plan for you to review.",
          receipt: "A small ask becomes a next step.",
        },
        {
          label: "Summarize the work",
          request: "Goblin, give me a quick recap of this thread.",
          reply:
            "Here’s the recap: investigate the login bug, review the proposed fix, then decide what to ship.",
          receipt: "The conversation, brought together.",
        },
      ],
    },
    {
      id: "teams",
      name: "Microsoft Teams",
      location: "Engineering · Daily standup",
      sender: "Anders Hejlsberg",
      prompts: [
        {
          label: "Prepare a standup update",
          request: "Goblin, help me turn these notes into a standup update.",
          reply:
            "Here’s a draft: yesterday, the login flow; today, a proposed fix; next, a review with the team.",
          receipt: "Ready for your team to review.",
        },
        {
          label: "Plan the next step",
          request: "What’s a sensible next step for the new settings screen?",
          reply:
            "Let’s agree on the fields and validation first. I can turn that into a focused implementation plan.",
          receipt: "Everyone starts with the same context.",
        },
        {
          label: "Capture a decision",
          request: "Let’s keep the new dashboard simple for the first version.",
          reply:
            "Got it. I’ll draft that decision as: focus on the essential view first, and leave extra widgets for later.",
          receipt: "A decision made easier to remember.",
        },
      ],
    },
    {
      id: "github",
      name: "GitHub",
      location: "Pull request #42 · Discussion",
      sender: "Stephen Toub",
      prompts: [
        {
          label: "Ask for a code review",
          request: "@goblin, could you review the empty states in this PR?",
          reply:
            "I’ll check first use, no results, and failed loading. I’ll bring any findings back as review notes.",
          receipt: "Review starts with a focused question.",
        },
        {
          label: "Think through edge cases",
          request: "What should we consider before merging this search change?",
          reply:
            "I’d check blank queries, slow responses, and keyboard navigation. Let’s review those before merging.",
          receipt: "A second set of eyes on the details.",
        },
        {
          label: "Draft a PR summary",
          request: "Help me write a clear description for this small login fix.",
          reply:
            "Start with the symptom, explain the fix, then list how it was checked. I can help turn your notes into a concise draft.",
          receipt: "Clear context for the next reviewer.",
        },
      ],
    },
    {
      id: "discord",
      name: "Discord",
      location: "# build-together",
      sender: "Renko Karman",
      prompts: [
        {
          label: "Explore an idea",
          request: "Goblin, we’re thinking about adding keyboard shortcuts. Where do we start?",
          reply:
            "Start with search, navigation, and a shortcut guide. Keep defaults familiar, and let people discover them as they go.",
          receipt: "An idea with room to grow.",
        },
        {
          label: "Unblock a teammate",
          request: "Our empty screen feels confusing. Any suggestions?",
          reply:
            "Give it a short explanation and one useful next action. A small example can help people see what belongs there.",
          receipt: "A little help to keep building.",
        },
        {
          label: "Draft a community update",
          request: "Can you help us share what we’re working on?",
          reply:
            "Try: ‘We’re improving the login experience and exploring keyboard shortcuts. We’d love your feedback as we go.’",
          receipt: "A draft with a human in the loop.",
        },
      ],
    },
    {
      id: "email",
      name: "Gmail",
      location: "Re: A quick project check-in",
      sender: "Linus Torvalds",
      prompts: [
        {
          label: "Draft a project recap",
          request: "Hi Goblin, help me summarize our next steps for the team.",
          reply:
            "Here’s a starting point: review the login fix, agree on the settings scope, and gather feedback on shortcuts.",
          receipt: "A thoughtful follow-up, ready to edit.",
        },
        {
          label: "Prepare a handoff",
          request: "What should we include in a handoff note for this feature?",
          reply:
            "Include the goal, what changed, how to check it, and any open questions. Link the relevant discussion for context.",
          receipt: "The useful details, all in one place.",
        },
        {
          label: "Make an update clearer",
          request: "Can you help me make this release update easier to read?",
          reply:
            "Lead with what people can do now. Group related changes, keep the language simple, and close with the next step.",
          receipt: "A clearer message for everyone.",
        },
      ],
    },
  ];

  const byId = (id) => document.getElementById(id);
  const player = byId("player");
  const timeline = byId("timeline");
  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
  const sceneDuration = 12;
  const duration = sceneDuration * scenes.length;
  const speeds = [1, 1.5, 0.75];
  const selectedPrompts = scenes.map(() => 0);
  const channelButtons = [...document.querySelectorAll("[data-channel]")];
  const chapterButtons = [...document.querySelectorAll("[data-chapter]")];
  const paths = scenes.map((scene) => byId(`line-${scene.id}`));
  const pathLengths = paths.map((path) => path.getTotalLength());
  let position = 7;
  let playing = !reducedMotion.matches;
  let speedIndex = 0;
  let currentScene = -1;
  let currentPhase = "";
  let lastFrame = null;
  let animationFrame = null;
  let scrubResume = false;
  let dialogResume = false;
  let previewResume = false;
  let customMessage = null;

  function announce(message) {
    byId("announcer").textContent = message;
  }

  function updateCard(scene, prompt) {
    const card = channelButtons.find((button) => button.dataset.channel === scene.id);
    card.title = scene.sender + ": " + prompt.request + "\nGoblin: " + prompt.reply;
    card.setAttribute(
      "aria-label",
      "Play the " + scene.name + " conversation with " + scene.sender + ". " + prompt.request,
    );
  }

  function displayScene(index) {
    const scene = scenes[index];
    const prompt = scene.prompts[selectedPrompts[index]];
    currentScene = index;
    currentPhase = "";
    byId("stage-count").textContent = `${String(index + 1).padStart(2, "0")} / 05`;
    byId("conversation-number").textContent = String(index + 1).padStart(2, "0");
    byId("conversation-title").textContent = scene.name;
    byId("conversation-location").textContent = scene.location;
    byId("conversation-icon").src = `assets/${scene.id}.svg`;
    byId("sender-name").textContent = scene.sender;
    byId("human-avatar").textContent = scene.sender
      .split(" ")
      .map((part) => part[0])
      .join("");
    byId("request-text").textContent = customMessage?.request ?? prompt.request;
    byId("reply-text").textContent = customMessage?.reply ?? prompt.reply;
    byId("receipt-text").textContent = prompt.receipt;
    byId("step-three").textContent = "Prepare a proposed change";
    byId("scenario").replaceChildren(
      ...scene.prompts.map((item, promptIndex) => {
        const option = document.createElement("option");
        option.value = String(promptIndex);
        option.textContent = item.label;
        return option;
      }),
    );
    byId("scenario").value = String(selectedPrompts[index]);
    channelButtons.forEach((button) =>
      button.setAttribute("aria-pressed", String(button.dataset.channel === scene.id)),
    );
    chapterButtons.forEach((button) => {
      if (button.dataset.chapter === scene.id) button.setAttribute("aria-current", "step");
      else button.removeAttribute("aria-current");
    });
    paths.forEach((path, pathIndex) => path.classList.toggle("is-active", pathIndex === index));
    updateCard(scene, prompt);
    renderDetails(scene, prompt);
  }

  function setPhase(phase) {
    if (currentPhase === phase) return;
    currentPhase = phase;
    const prompt = scenes[currentScene].prompts[selectedPrompts[currentScene]];
    const waiting = phase === "ready" || phase === "asking";
    byId("typing-row").hidden = phase !== "thinking" && phase !== "replying";
    byId("reply-message").hidden = phase !== "received";
    byId("artifact-message").hidden = phase !== "received" || customMessage !== null;
    byId("thread-placeholder").hidden = !waiting;
    byId("goblin-hub").classList.toggle("is-thinking", phase === "thinking");
    const status = {
      ready: "Here for your team",
      asking: "A new conversation",
      thinking: "Thinking it through…",
      replying: "Bringing an answer back",
      received: "Right here, in the loop",
    };
    byId("hub-status").textContent = status[phase];
    if (phase === "received")
      announce(`Goblin replies in ${scenes[currentScene].name}: ${prompt.reply}`);
  }

  function render() {
    const index = Math.min(scenes.length - 1, Math.floor(position / sceneDuration));
    const localTime = position - index * sceneDuration;
    if (index !== currentScene) {
      if (currentScene >= 0 && index !== currentScene) customMessage = null;
      displayScene(index);
    }
    const phase =
      localTime < 1.2
        ? "ready"
        : localTime < 2.7
          ? "asking"
          : localTime < 5.5
            ? "thinking"
            : localTime < 6.8
              ? "replying"
              : "received";
    setPhase(phase);
    byId("incoming-message").style.opacity = "1";
    const inTransit = !reducedMotion.matches && (phase === "asking" || phase === "replying");
    byId("packet").setAttribute("visibility", inTransit ? "visible" : "hidden");
    byId("travel-label").style.opacity = inTransit ? "1" : "0";
    if (inTransit) {
      const progress = phase === "asking" ? (localTime - 1.2) / 1.5 : 1 - (localTime - 5.5) / 1.3;
      const eased = progress * progress * (3 - 2 * progress);
      const point = paths[index].getPointAtLength(eased * pathLengths[index]);
      byId("packet").setAttribute("transform", `translate(${point.x} ${point.y})`);
      // Follow the SVG's rendered coordinates, including the taller mobile stage.
      const svgRect = document.querySelector(".connections").getBoundingClientRect();
      const networkRect = byId("network").getBoundingClientRect();
      const label = byId("travel-label");
      label.style.left = `${svgRect.left - networkRect.left + (point.x / 1100) * svgRect.width}px`;
      label.style.top = `${svgRect.top - networkRect.top + (point.y / 700) * svgRect.height}px`;
      label.textContent = phase === "asking" ? "Hey, Goblin…" : "On it. ↗";
    }
    timeline.value = String(position);
    timeline.style.setProperty("--progress", `${(position / duration) * 100}%`);
    const seconds = Math.floor(position);
    byId("elapsed").textContent =
      `${String(Math.floor(seconds / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
    timeline.setAttribute(
      "aria-valuetext",
      `${Math.round(position)} seconds of ${duration} seconds. ${scenes[index].name} conversation.`,
    );
  }

  function updatePlaybackControls() {
    player.classList.toggle("is-paused", !playing);
    byId("play-button").setAttribute(
      "aria-label",
      playing ? "Pause animation" : position >= duration ? "Replay animation" : "Play animation",
    );
    byId("play-icon").toggleAttribute("hidden", playing);
    byId("pause-icon").toggleAttribute("hidden", !playing);
  }

  function tick(timestamp) {
    animationFrame = null;
    if (!playing) return;
    if (lastFrame !== null)
      position = Math.min(
        duration,
        position + Math.min((timestamp - lastFrame) / 1000, 0.1) * speeds[speedIndex],
      );
    lastFrame = timestamp;
    render();
    if (position >= duration) {
      setPlaying(false);
      announce("The film has ended. Replay it or choose another channel.");
    } else animationFrame = requestAnimationFrame(tick);
  }

  function setPlaying(value) {
    playing = value;
    if (animationFrame !== null) cancelAnimationFrame(animationFrame);
    animationFrame = null;
    lastFrame = null;
    updatePlaybackControls();
    if (playing && !document.hidden) animationFrame = requestAnimationFrame(tick);
  }

  function togglePlayback() {
    if (position >= duration) {
      customMessage = null;
      currentScene = -1;
      position = 0;
      render();
    }
    setPlaying(!playing);
  }

  function selectChannel(id) {
    const index = scenes.findIndex((scene) => scene.id === id);
    if (index < 0) return;
    customMessage = null;
    position = index * sceneDuration + (reducedMotion.matches ? 7 : 0);
    currentScene = -1;
    render();
    setPlaying(!reducedMotion.matches);
    if (!reducedMotion.matches)
      announce(
        `${scenes[index].name} conversation. ${scenes[index].prompts[selectedPrompts[index]].request}`,
      );
  }

  channelButtons.forEach((button) =>
    button.addEventListener("click", () => selectChannel(button.dataset.channel)),
  );
  chapterButtons.forEach((button) =>
    button.addEventListener("click", () => selectChannel(button.dataset.chapter)),
  );
  byId("scenario").addEventListener("change", (event) => {
    selectedPrompts[currentScene] = Number(event.target.value);
    selectChannel(scenes[currentScene].id);
  });
  byId("play-button").addEventListener("click", togglePlayback);
  byId("replay-button").addEventListener("click", () => {
    customMessage = null;
    currentScene = -1;
    position = 0;
    render();
    setPlaying(true);
    announce("Replaying from the beginning.");
  });
  byId("speed-button").addEventListener("click", () => {
    speedIndex = (speedIndex + 1) % speeds.length;
    byId("speed-button").textContent = `${speeds[speedIndex]}×`;
    byId("speed-button").setAttribute(
      "aria-label",
      `Playback speed: ${speeds[speedIndex]} times. Click to change.`,
    );
    announce(`Playback speed ${speeds[speedIndex]} times.`);
  });

  timeline.addEventListener("pointerdown", () => {
    scrubResume = playing;
    setPlaying(false);
  });
  timeline.addEventListener("input", () => {
    position = Number(timeline.value);
    render();
    if (position >= duration) setPlaying(false);
  });
  function finishScrubbing() {
    if (scrubResume && position < duration) setPlaying(true);
    scrubResume = false;
  }
  window.addEventListener("pointerup", finishScrubbing);
  window.addEventListener("pointercancel", finishScrubbing);
  timeline.addEventListener("keydown", (event) => {
    if (
      [
        "ArrowLeft",
        "ArrowRight",
        "ArrowUp",
        "ArrowDown",
        "Home",
        "End",
        "PageUp",
        "PageDown",
      ].includes(event.key)
    )
      setPlaying(false);
  });

  const fullscreenButton = byId("fullscreen-button");
  fullscreenButton.hidden = !document.fullscreenEnabled;
  fullscreenButton.addEventListener("click", async () => {
    try {
      if (document.fullscreenElement) await document.exitFullscreen();
      else await player.requestFullscreen();
    } catch {
      announce("Fullscreen is unavailable in this browser. You can continue watching here.");
    }
  });
  document.addEventListener("fullscreenchange", () => {
    fullscreenButton.setAttribute(
      "aria-label",
      document.fullscreenElement ? "Exit fullscreen" : "Enter fullscreen",
    );
    render();
  });

  const aboutDialog = byId("about-dialog");
  byId("about-button").addEventListener("click", () => {
    dialogResume = playing;
    setPlaying(false);
    aboutDialog.showModal();
  });
  byId("close-about").addEventListener("click", () => aboutDialog.close());
  aboutDialog.addEventListener("close", () => {
    if (dialogResume) setPlaying(true);
    dialogResume = false;
  });
  aboutDialog.addEventListener("click", (event) => {
    if (event.target !== aboutDialog) return;
    const rect = aboutDialog.getBoundingClientRect();
    if (
      event.clientX < rect.left ||
      event.clientX > rect.right ||
      event.clientY < rect.top ||
      event.clientY > rect.bottom
    )
      aboutDialog.close();
  });

  document.addEventListener("keydown", (event) => {
    if (
      event.code !== "Space" ||
      event.repeat ||
      event.altKey ||
      event.ctrlKey ||
      event.metaKey ||
      aboutDialog.open ||
      byId("preview-dialog").open
    )
      return;
    if (
      event.target instanceof Element &&
      event.target.closest("button, input, select, textarea, a, [contenteditable]")
    )
      return;
    event.preventDefault();
    togglePlayback();
  });
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) {
      if (animationFrame !== null) cancelAnimationFrame(animationFrame);
      animationFrame = null;
      lastFrame = null;
      player.classList.add("is-paused");
    } else setPlaying(playing);
  });
  reducedMotion.addEventListener("change", () => {
    if (reducedMotion.matches) {
      position = currentScene * sceneDuration + 7;
      setPlaying(false);
    }
    render();
  });
  window.addEventListener("resize", render);

  const artifactLabels = {
    slack: ["PR #482", "A proposed fix for the login flow", "Investigation notes"],
    teams: ["Standup draft", "A shared update for the engineering team", "Team decisions"],
    github: ["Review notes", "A focused review of pull request #42", "Review checklist"],
    discord: ["Shortcut proposal", "A starting point for building together", "Community notes"],
    email: ["Project recap", "A thoughtful follow-up for the team", "Handoff notes"],
  };
  function element(tag, text, className) {
    const node = document.createElement(tag);
    if (text) node.textContent = text;
    if (className) node.className = className;
    return node;
  }
  function renderDetails(scene, prompt) {
    const labels =
      selectedPrompts[currentScene] === 0
        ? artifactLabels[scene.id]
        : ["Proposed next steps", prompt.label, "Conversation notes"];
    const externalIcon = byId("artifact-title").querySelector("svg");
    byId("artifact-title").replaceChildren(document.createTextNode(labels[0] + " "));
    if (externalIcon) byId("artifact-title").append(externalIcon);
    byId("artifact-description").textContent = labels[1];
    byId("document-title").textContent = labels[2];
    const activity = byId("view-activity");
    activity.replaceChildren(
      element("h3", "Every step, in the loop"),
      element("p", "An example of how one conversation becomes a clear next step."),
    );
    [
      ["Request received in " + scene.name, customMessage?.request ?? prompt.request],
      ["Context brought together", "The request and its conversation stay connected."],
      ["A response prepared", customMessage?.reply ?? prompt.reply],
      ["Ready for a human review", "You decide what happens next."],
    ].forEach(([title, description]) => {
      const item = element("div", null, "activity-item");
      item.append(element("strong", title), element("p", description));
      activity.append(item);
    });
    const work = byId("view-work");
    const workCard = element("div", null, "work-card");
    workCard.append(
      element("span", "Awaiting review", "review-badge"),
      element("h4", customMessage ? "Your demo request" : prompt.label),
      element("p", customMessage?.reply ?? prompt.reply),
    );
    const list = element("ul");
    [
      "Understand the request",
      "Gather the relevant context",
      "Prepare a proposed next step",
    ].forEach((text) => list.append(element("li", text)));
    workCard.append(list);
    work.replaceChildren(
      element("h3", "From a message to work"),
      element("p", "The conversation stays attached to the work it starts."),
      workCard,
    );
    const files = byId("view-files");
    files.replaceChildren(
      element("h3", "The useful details"),
      element(
        "p",
        customMessage
          ? "Your message is a local demo. No files have been generated or uploaded."
          : "Example deliverables, ready to bring back to the conversation.",
      ),
    );
    if (!customMessage) {
      const fileCard = byId("artifact-message").querySelector(".artifact-card").cloneNode(true);
      fileCard.querySelectorAll("[id]").forEach((node) => node.removeAttribute("id"));
      files.append(fileCard);
    }
  }
  const tabs = [...document.querySelectorAll("[data-tab]")];
  function selectTab(tab) {
    tabs.forEach((button) => {
      const active = button === tab;
      button.setAttribute("aria-selected", String(active));
      button.tabIndex = active ? 0 : -1;
      byId("view-" + button.dataset.tab).hidden = !active;
    });
    if (tab.dataset.tab !== "conversation") setPlaying(false);
  }
  tabs.forEach((tab, index) => {
    tab.addEventListener("click", () => selectTab(tab));
    tab.addEventListener("keydown", (event) => {
      let next;
      if (event.key === "ArrowRight") next = (index + 1) % tabs.length;
      if (event.key === "ArrowLeft") next = (index + tabs.length - 1) % tabs.length;
      if (event.key === "Home") next = 0;
      if (event.key === "End") next = tabs.length - 1;
      if (next === undefined) return;
      event.preventDefault();
      selectTab(tabs[next]);
      tabs[next].focus();
    });
  });
  const previewDialog = byId("preview-dialog");
  function openPreview(kind) {
    previewResume = playing;
    setPlaying(false);
    const scene = scenes[currentScene];
    const prompt = scene.prompts[selectedPrompts[currentScene]];
    const body = byId("preview-body");
    byId("preview-title").textContent =
      kind === "pr"
        ? byId("artifact-title").textContent.trim()
        : byId("document-title").textContent;
    body.replaceChildren(
      element("p", "A sample deliverable from the " + scene.name + " conversation."),
      element("p", "Request: " + prompt.request),
      element("p", prompt.reply),
    );
    const list = element("ul");
    [
      "Keep the original request and context together.",
      "Review the proposed next step with your team.",
      "Decide what to change or share next.",
    ].forEach((text) => list.append(element("li", text)));
    body.append(
      list,
      element(
        "p",
        "This is an illustrative preview. No pull request or document has been created.",
      ),
    );
    previewDialog.showModal();
  }
  document.addEventListener("click", (event) => {
    const button = event.target.closest("[data-preview]");
    if (button) openPreview(button.dataset.preview);
  });
  byId("close-preview").addEventListener("click", () => previewDialog.close());
  byId("preview-done").addEventListener("click", () => previewDialog.close());
  previewDialog.addEventListener("close", () => {
    if (previewResume) setPlaying(true);
    previewResume = false;
  });
  byId("channel-info").addEventListener("click", () => byId("about-button").click());
  const messageInput = byId("message-input");
  const attachmentInput = byId("attachment-input");
  byId("attach-button").addEventListener("click", () => attachmentInput.click());
  attachmentInput.addEventListener("change", () => {
    const file = attachmentInput.files[0];
    byId("attachment-name").textContent = file ? file.name + " · demo attachment" : "";
    byId("attachment-name").hidden = !file;
    if (file) announce("File selected for the demo. It will not be uploaded.");
  });
  function insertText(text) {
    messageInput.setRangeText(text, messageInput.selectionStart, messageInput.selectionEnd, "end");
    messageInput.focus();
  }
  byId("mention-button").addEventListener("click", () => insertText("@Goblin "));
  byId("emoji-button").addEventListener("click", () => insertText("🙂"));
  messageInput.addEventListener("focus", () => setPlaying(false));
  messageInput.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      byId("message-form").requestSubmit();
    }
  });
  byId("message-form").addEventListener("submit", (event) => {
    event.preventDefault();
    const request = messageInput.value.trim();
    if (!request) {
      messageInput.focus();
      return;
    }
    setPlaying(false);
    const scene = scenes[currentScene];
    customMessage = {
      request:
        request +
        (attachmentInput.files[0]
          ? " (Demo attachment: " + attachmentInput.files[0].name + ")"
          : ""),
      reply:
        "Here’s an example next step: clarify the goal, gather the relevant context, and prepare a draft for your review. This message stays in this demo.",
    };
    position = currentScene * sceneDuration + 7;
    displayScene(currentScene);
    render();
    byId("sender-name").textContent = "You";
    byId("human-avatar").textContent = "Y";
    byId("step-three").textContent = "Outline a possible next step";
    messageInput.value = "";
    attachmentInput.value = "";
    byId("attachment-name").hidden = true;
    byId("thread").scrollTop = 0;
    announce("Your demo message in " + scene.name + ". " + customMessage.reply);
  });

  scenes.forEach((scene) => updateCard(scene, scene.prompts[0]));
  render();
  setPlaying(playing);
})();
