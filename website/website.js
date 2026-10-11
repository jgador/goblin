(() => {
  "use strict";

  // Presentation-only examples. This website has no service connections or Work state.
  const scenes = [
    {
      id: "slack",
      name: "Slack",
      flow: { travelSeconds: 0.6, thinkingSeconds: 0.8 },
      location: "#log-pipeline",
      sender: "Josh A.",
      steps: [
        "Trace ingestion and buffering",
        "Check throughput and delivery",
        "Prepare the Kubernetes rollout",
      ],
      prompts: [
        {
          label: "Rewrite the log pipeline",
          request:
            "Hey Goblin, rewrite the log ingestion pipeline to handle traffic spikes, then deploy it to our Kubernetes staging cluster.",
          reply:
            "I’ll separate ingestion from processing, add bounded buffering and backpressure, then deploy the worker to staging. I’ll bring back the changes, load-test results, and rollback steps for review.",
          receipt: "From a pipeline rewrite to a staging rollout, in one thread.",
          checks: [
            "Trace the current ingestion path and measure throughput, queue lag, and memory under burst traffic.",
            "Keep event IDs stable, acknowledge only after durable enqueue, and verify downstream deduplication when events are replayed.",
            "Deploy the rewritten worker to Kubernetes staging and compare delivery counts, latency, and resource use with the baseline.",
          ],
        },
        {
          label: "Deploy to Kubernetes",
          request:
            "Make the new ingestion worker ready for Kubernetes. Include health probes, resource limits, and a graceful shutdown so pod rollouts don’t lose accepted logs.",
          reply:
            "I’ll add the manifests, drain in-flight batches on shutdown, and size the termination grace period. I’ll test a staging rollout under load and check that accepted events reach storage.",
          receipt: "Deployment details, with delivery checks built in.",
          artifacts: [
            "Kubernetes deployment changes",
            "Health probes, resource limits, and graceful shutdown",
            "Staging rollout checklist",
          ],
          checks: [
            "Pin the container image and define resource requests, limits, and startup, readiness, and liveness probes.",
            "Stop taking new work on termination, finish in-flight batches, and leave unacknowledged events available for redelivery.",
            "Exercise pod replacement under load in staging; check accepted event IDs at the sink for missing or duplicate records.",
          ],
        },
        {
          label: "Review the rollout",
          request:
            "Give me a rollout summary for the ingestion rewrite: throughput, queue lag, rejected events, and how we roll back before moving beyond staging.",
          reply:
            "I’ll compare the staging results with the old pipeline, flag delivery gaps or rising queue lag, and document how to restore the previous worker without discarding queued logs.",
          receipt: "The rollout evidence and rollback plan, back in Slack.",
          artifacts: [
            "Ingestion rollout review",
            "Throughput, queue lag, rejected events, and delivery counts",
            "Rollback plan",
          ],
          checks: [
            "Compare old and new pipeline throughput, p95 ingestion latency, queue lag, and rejected-event counts using the same workload.",
            "Report missing or duplicate event IDs and separate measured results from checks that have not run.",
            "Verify queue-format compatibility with the previous worker, rehearse rollback without deleting queued logs, and record the release decision for review.",
          ],
        },
      ],
    },
    {
      id: "teams",
      name: "Microsoft Teams",
      flow: { travelSeconds: 1, thinkingSeconds: 1.4 },
      location: "TypeScript · Compiler discussion",
      sender: "Anders H.",
      source: {
        label: "microsoft/TypeScript#62606",
        url: "https://github.com/microsoft/TypeScript/issues/62606",
        summary:
          "Anders H.’s closed issue reports a generic assignment error that disappears when two property declarations are reversed. This conversation imagines reviewing regression coverage.",
      },
      steps: [
        "Compare property declaration orders",
        "Check generic assignability",
        "Prepare regression coverage",
      ],
      prompts: [
        {
          label: "Check property-order behavior",
          request:
            "Goblin, use microsoft/TypeScript#62606 to check for regressions. Swapping schema and readonlySchema should not turn a valid assignment into an error.",
          reply:
            "I’ll compare both declaration orders in the original DerivedTable example, then run the focused compiler tests. I’ll bring the diagnostics and a proposed regression test back here.",
          receipt: "A subtle compiler bug, with a clear regression check.",
          checks: [
            "Use the original DerivedTable example from the closed issue as the regression case.",
            "Check that the valid assignment is accepted with either property declaration order.",
            "Run focused compiler tests and return the diagnostics with the proposed regression coverage.",
          ],
        },
        {
          label: "Preserve type safety",
          request:
            "Include the Readonly mapped type and indexed access intersections. Invalid assignments still need to fail; accepting both property orders isn’t enough.",
          reply:
            "I’ll pair the original case with assignments that omit a required property or use the wrong property type. I’ll check both declaration orders and keep the expected errors in the test baselines.",
          receipt: "Consistent checking, with type safety preserved.",
          artifacts: [
            "Assignability test plan",
            "Property order, Readonly, and indexed access intersections",
            "Expected diagnostics",
          ],
          checks: [
            "Retain the original Readonly mapped type and intersections of indexed access types.",
            "Add invalid assignments with missing required properties or incompatible property types.",
            "Check that valid assignments pass and invalid assignments fail under both declaration orders.",
          ],
        },
        {
          label: "Prepare the team update",
          request:
            "Give the team a short update on #62606: the original symptom, the regression cases we need, and what still needs review.",
          reply:
            "I’ll link the closed issue, explain how swapping two declarations changed an error, and outline the valid and invalid assignment cases. I’ll distinguish proposed coverage from verified test results.",
          receipt: "Compiler context, ready for the team to review.",
          artifacts: [
            "Compiler team update",
            "TypeScript #62606 · Symptom, coverage, and review",
            "Regression review notes",
          ],
          checks: [
            "Link the closed issue and explain the original property-order inconsistency.",
            "Summarize coverage for both declaration orders, valid assignments, and expected errors.",
            "Separate proposed tests from verified results and identify remaining review questions.",
          ],
        },
      ],
    },
    {
      id: "github",
      name: "GitHub",
      flow: { travelSeconds: 2.4, thinkingSeconds: 2.8 },
      location: "dotnet/runtime · Issue #96277",
      sender: "Stephen T.",
      source: {
        label: "dotnet/runtime#96277",
        url: "https://github.com/dotnet/runtime/issues/96277",
        summary:
          "The public issue explores using a PriorityQueue for OrderBy().Take(), with memory, throughput, and stable ordering to consider.",
      },
      steps: [
        "Trace OrderBy().Take()",
        "Compare time and allocations",
        "Prepare findings for review",
      ],
      prompts: [
        {
          label: "Investigate LINQ allocations",
          request:
            "@goblin, take a look at dotnet/runtime#96277. Can we reduce the memory used by OrderBy(...).Take(10) without slowing it down?",
          reply:
            "I’ll compare the current LINQ path with a bounded PriorityQueue. The thread reports lower allocations but slower runtime results, so I’ll measure both before recommending a change.",
          receipt: "A real performance question, ready for investigation.",
          checks: [
            "Compare the existing OrderBy().Take() path with a bounded priority queue inside System.Linq.",
            "Measure execution time and allocated bytes; lower allocation alone is not a speedup.",
            "Preserve stable ordering and bring the findings back for review.",
          ],
        },
        {
          label: "Preserve ordering",
          request:
            "Keep OrderBy stable. Equal keys must stay in source order, and Take must return the smallest elements for ascending order.",
          reply:
            "I’ll use the source index to break ties, reverse the heap comparison to retain the smallest values, and restore ascending output. I’ll cover duplicate keys, descending order, and Skip().Take().",
          receipt: "Lower memory, with the same ordering guarantees.",
          artifacts: [
            "Ordering checklist",
            "Stable ties, heap direction, and Skip/Take boundaries",
            "Correctness test plan",
          ],
          checks: [
            "Use the source index as a tie-breaker so equal keys retain their original order.",
            "Keep the smallest values for ascending order, then emit them in the correct order.",
            "Cover duplicate keys, custom comparers, descending order, empty input, and Skip().Take() boundaries.",
          ],
        },
        {
          label: "Find the performance threshold",
          request:
            "Where does the queue actually win? Compare small and large sources, vary Take and Skip, and include ToArray and ToList.",
          reply:
            "I’ll benchmark inside System.Linq across source sizes and take/skip counts. I’ll report time and allocated bytes, including cases where the existing path wins, before proposing a threshold.",
          receipt: "A measured tradeoff, ready for your review.",
          artifacts: [
            "Benchmark plan",
            "Throughput and allocations across source and result sizes",
            "Threshold review notes",
          ],
          checks: [
            "Vary source length, Take count, Skip count, and value/reference types.",
            "Include ToArray and ToList, and test when the requested slice approaches the source size.",
            "Compare the real System.Linq implementation and report regressions as well as improvements before choosing a threshold.",
          ],
        },
      ],
    },
    {
      id: "discord",
      name: "Discord",
      flow: { travelSeconds: 1, thinkingSeconds: 1.4 },
      location: "# graph-tooling",
      sender: "Renko K.",
      source: {
        label: "Circles Arrows · About us",
        url: "https://www.circlesarrows.com/about-us",
        summary:
          "Circles Arrows describes work in graph database tooling, business applications, and generative AI. This fictional example explores a C# graph model, data comparison, and answers grounded in sample data.",
        attribution: "Fictional dialogue inspired by public company and product descriptions. ",
        related: [
          {
            label: "Blueprint41",
            url: "https://www.circlesarrows.com/blueprint41",
          },
          {
            label: "Compare41",
            url: "https://www.circlesarrows.com/compare41",
          },
        ],
      },
      steps: [
        "Understand the graph model",
        "Trace data and relationships",
        "Prepare an example for review",
      ],
      prompts: [
        {
          label: "Model a graph in C#",
          request:
            "Goblin, sketch a Blueprint41 sample for customers, orders, and products. Keep the model easy for a C# developer to follow.",
          reply:
            "I’ll draft the entities and relationships, add a small fictional dataset, and show how to query a customer’s orders. I’ll include a schema change example for the team to review.",
          receipt: "A graph model the team can build on.",
          checks: [
            "Define Customer, Order, and Product entities and their relationships using fictional sample records.",
            "Outline a Blueprint41 example using generated C# data objects to query a customer’s orders.",
            "Include a versioned schema change and checks that the sample relationships remain intact.",
          ],
        },
        {
          label: "Review graph differences",
          request:
            "Before we sync the sample graphs, help me review the Compare41 differences. Which nodes, relationships, and properties would change?",
          reply:
            "I’ll turn the comparison into a scoped change list, flag deletions and relationship changes, and draft a validation checklist for the selected subgraph.",
          receipt: "See what changes before you sync.",
          artifacts: [
            "Graph comparison review",
            "Compare41 · Nodes, relationships, and sync scope",
            "Validation checklist",
          ],
          checks: [
            "Review model and data differences between the two sample graphs, including the entity mapping.",
            "List proposed additions, updates, deletions, and relationship changes within the selected subgraph.",
            "Define before-and-after query checks and record counts for the proposed sync scope.",
          ],
        },
        {
          label: "Plan an AI prototype with graph data",
          request:
            "Can we turn this sample graph into a useful AI prototype? Let people ask which products a customer ordered and see what supports the answer.",
          reply:
            "I’ll plan one question-to-answer flow, ground the answer in the sample graph, and show the records behind it. I’ll include missing-data cases and keep the prototype small enough to review.",
          receipt: "A useful answer, connected to its evidence.",
          artifacts: [
            "Graph AI prototype plan",
            "A customer question, a graph query, and supporting records",
            "Example questions and checks",
          ],
          checks: [
            "Use the fictional customer-order-product graph for one clearly defined question-to-answer flow.",
            "Show the supporting records alongside each answer so reviewers can trace it to the graph.",
            "Cover unknown customers and missing relationships without inventing facts; keep additional use cases outside the first prototype.",
          ],
        },
      ],
    },
    {
      id: "email",
      name: "Gmail",
      flow: { travelSeconds: 1.6, thinkingSeconds: 2 },
      location: "Re: [PATCH] AtomISP min/max expansion",
      sender: "Linus T.",
      source: {
        label: "Linus’s reply · 11 Sep 2024",
        url: "https://lists.openwall.net/netdev/2024/09/11/354",
        summary:
          "In his September 2024 reply, Linus accepted the focused AtomISP patch for a reported build problem and left two other patches for their maintainers. This scenario revisits that historical review.",
        attribution: "Fictional dialogue inspired by a public patch discussion. ",
        related: [
          {
            label: "torvalds/linux · 7c6a3a65ace7",
            url: "https://github.com/torvalds/linux/commit/7c6a3a65ace70f12b27b1a27c9a69cb791dc6e91",
          },
        ],
      },
      steps: [
        "Reproduce the AtomISP build failure",
        "Review the focused backport",
        "Prepare findings for the thread",
      ],
      prompts: [
        {
          label: "Revisit a kernel build failure",
          request:
            "Goblin, revisit the AtomISP build failure behind 7c6a3a65ace7. Check the focused fix against the reported Slackware 15.0 32-bit setup.",
          reply:
            "I’ll compare the affected 5.15 build with and without the patch, inspect the nested min/max expansion, and record compiler memory and build results for review.",
          receipt: "A real build problem, with evidence to review.",
          checks: [
            "Reconstruct the reported 32-bit build environment and record the kernel revision, compiler, and configuration.",
            "Compare the affected AtomISP build before and after the focused patch, including preprocessed output and compiler memory use.",
            "Return build logs and measured results; keep any unverified reproduction details explicit.",
          ],
        },
        {
          label: "Review the focused backport",
          request:
            "Keep the backport to the AtomISP fix. Check the inline helpers and clamp_t preserve the results without pulling in the wider min/max rewrite.",
          reply:
            "I’ll compare the signed and unsigned helper results at the shift and clamp boundaries, check the affected callers, and review the patch against the target tree.",
          receipt: "A small patch, with its behavior checked.",
          artifacts: [
            "AtomISP backport review",
            "Inline helpers, clamp limits, and affected callers",
            "Boundary test plan",
          ],
          checks: [
            "Review the change from nested macros to inline helpers and clamp_t in sh_css_frac.h.",
            "Compare signed and unsigned results for valid inputs around shift and clamp boundaries, including affected call sites.",
            "Check applicability to the target tree and document dependencies without importing the wider min/max rewrite.",
          ],
        },
        {
          label: "Summarize the patch decision",
          request:
            "Draft a short recap of the thread: why the AtomISP patch was taken directly, and why the other two patches were left for their maintainers.",
          reply:
            "I’ll explain the reported build problem, the more readable inline helpers, and the backport rationale. I’ll link your reply and the accepted commit, and keep the other two changes separate.",
          receipt: "The patch and its rationale, kept in the thread.",
          artifacts: [
            "Patch discussion recap",
            "AtomISP · Build fix, backport rationale, and maintainer review",
            "Mailing-list reply draft",
          ],
          checks: [
            "Link Linus’s actual reply and the accepted commit, credited to patch author Lorenzo S.",
            "Explain the reported build problem and why a focused change was useful for backporting.",
            "Describe the other two patches as left for their maintainers at the time; distinguish historical facts from proposed new checks.",
          ],
        },
      ],
    },
  ];

  const byId = (id) => document.getElementById(id);
  const player = byId("player");
  const timeline = byId("timeline");
  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
  // Illustrative pacing: chat is quick; email and repository work take longer.
  const sceneTimings = scenes.reduce((timings, { flow }) => {
    const asking = 0.5;
    const thinking = asking + flow.travelSeconds;
    const replying = thinking + flow.thinkingSeconds;
    const received = replying + flow.travelSeconds;
    const start = timings.at(-1)?.end ?? 0;
    // Hold the completed reply for 2–3 seconds, without padding fast channels.
    const end = start + Math.ceil(received + 2);
    timings.push({ start, end, asking, thinking, replying, received });
    return timings;
  }, []);
  const duration = sceneTimings.at(-1).end;
  timeline.max = String(duration);
  byId("total-duration").textContent = formatTime(duration);
  const speeds = [1, 1.5, 0.75];
  const channelButtons = [...document.querySelectorAll("[data-channel]")];
  const chapterButtons = [...document.querySelectorAll("[data-chapter]")];
  const connections = document.querySelector(".connections");
  const paths = scenes.map((scene) => byId(`line-${scene.id}`));
  const pathLengths = paths.map((path) => path.getTotalLength());
  const connectorPorts = scenes.map((scene) =>
    document.querySelector(`[data-channel="${scene.id}"] .connection-port`),
  );
  const connectorDots = scenes.map((scene) => byId(`dot-${scene.id}`));
  const packet = byId("packet");
  const packetDot = packet.querySelector(".packet-dot");
  const packetHalo = packet.querySelector(".packet-halo");
  const packetScale = { x: 1, y: 1 };
  let position = receivedPosition(0);
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

  function receivedPosition(index) {
    const timing = sceneTimings[index];
    return timing.start + timing.received + 0.2;
  }

  function formatTime(value) {
    const seconds = Math.floor(value);
    return `${String(Math.floor(seconds / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
  }

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
    const prompt = scene.prompts[0];
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
    const steps = (!customMessage && scene.steps) || [
      "Understand the request",
      "Gather the relevant context",
      "Prepare a proposed change",
    ];
    ["step-one", "step-two", "step-three"].forEach((id, stepIndex) => {
      byId(id).textContent = steps[stepIndex];
    });
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
    const prompt = scenes[currentScene].prompts[0];
    byId("typing-row").hidden = phase !== "thinking" && phase !== "replying";
    byId("reply-message").hidden = phase !== "received";
    byId("artifact-message").hidden = phase !== "received" || customMessage !== null;
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
    const index = sceneTimings.findLastIndex(({ start }) => position >= start);
    const timing = sceneTimings[index];
    const localTime = position - timing.start;
    const travelSeconds = scenes[index].flow.travelSeconds;
    if (index !== currentScene) {
      if (currentScene >= 0 && index !== currentScene) customMessage = null;
      displayScene(index);
    }
    const phase =
      localTime < timing.asking
        ? "ready"
        : localTime < timing.thinking
          ? "asking"
          : localTime < timing.replying
            ? "thinking"
            : localTime < timing.received
              ? "replying"
              : "received";
    setPhase(phase);
    byId("incoming-message").style.opacity = "1";
    const inTransit = !reducedMotion.matches && (phase === "asking" || phase === "replying");
    byId("packet").setAttribute("visibility", inTransit ? "visible" : "hidden");
    byId("travel-label").style.opacity = inTransit ? "1" : "0";
    if (inTransit) {
      const progress =
        phase === "asking"
          ? (localTime - timing.asking) / travelSeconds
          : 1 - (localTime - timing.replying) / travelSeconds;
      const eased = progress * progress * (3 - 2 * progress);
      const point = paths[index].getPointAtLength(eased * pathLengths[index]);
      packet.setAttribute(
        "transform",
        `translate(${point.x} ${point.y}) scale(${packetScale.x} ${packetScale.y})`,
      );
      const screenPoint = point.matrixTransform(connections.getScreenCTM());
      const networkRect = byId("network").getBoundingClientRect();
      const label = byId("travel-label");
      label.style.left = `${screenPoint.x - networkRect.left}px`;
      label.style.top = `${screenPoint.y - networkRect.top}px`;
      label.textContent = phase === "asking" ? "Hey, Goblin…" : "On it. ↗";
    }
    timeline.value = String(position);
    timeline.style.setProperty("--progress", `${(position / duration) * 100}%`);
    byId("elapsed").textContent = formatTime(position);
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
      position =
        (position + Math.min((timestamp - lastFrame) / 1000, 0.1) * speeds[speedIndex]) % duration;
    lastFrame = timestamp;
    render();
    animationFrame = requestAnimationFrame(tick);
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
    position = reducedMotion.matches ? receivedPosition(index) : sceneTimings[index].start;
    currentScene = -1;
    render();
    setPlaying(!reducedMotion.matches);
    if (!reducedMotion.matches)
      announce(
        `${scenes[index].name} conversation. ${scenes[index].prompts[0].request}`,
      );
  }

  channelButtons.forEach((button) =>
    button.addEventListener("click", () => selectChannel(button.dataset.channel)),
  );
  chapterButtons.forEach((button) =>
    button.addEventListener("click", () => selectChannel(button.dataset.chapter)),
  );
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
  byId("channel-info").addEventListener("click", () => {
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
      position = receivedPosition(currentScene);
      setPlaying(false);
    }
    render();
  });
  const hub = byId("goblin-hub");
  const network = byId("network");
  const discordEndpoint = byId("dot-discord");
  const connectorRoutes = {
    slack: ({ x, y }) => {
      const bend = Math.max(365, x + 25);
      return `M${x} ${y}H${bend - 25}Q${bend} ${y} ${bend} 96V101Q${bend} 126 ${bend + 29} 126H464Q535 126 535 200`;
    },
    teams: ({ x, y }) => {
      const bend = Math.min(765, x - 25);
      return `M${x} ${y}H${bend + 25}Q${bend} ${y} ${bend} 96V101Q${bend} 126 ${bend - 29} 126H691Q620 126 620 200`;
    },
    github: ({ x, y }) => `M${x} ${y}H382Q420 ${y} 420 325V302Q420 272 450 272H500`,
    email: ({ x, y }) => `M${x} ${y}H750Q716 ${y} 716 325V302Q716 272 686 272H653`,
    discord: ({ x, y }) => `M${x} ${y}L575 420`,
  };
  function updateConnectorGeometry() {
    const matrix = connections.getScreenCTM();
    if (!matrix) return;
    const toSvg = matrix.inverse();
    // Cancel the SVG's unequal axis scaling so circles keep their pixel shape and size.
    packetScale.x = 1 / Math.hypot(matrix.a, matrix.b);
    packetScale.y = 1 / Math.hypot(matrix.c, matrix.d);
    scenes.forEach((scene, index) => {
      const port = connectorPorts[index].getBoundingClientRect();
      const start = new DOMPoint(port.x + port.width / 2, port.y + port.height / 2)
        .matrixTransform(toSvg);
      const path = paths[index];
      path.setAttribute("d", connectorRoutes[scene.id](start));
      pathLengths[index] = path.getTotalLength();
      const end = path.getPointAtLength(pathLengths[index]);
      const dot = connectorDots[index];
      dot.setAttribute("cx", String(end.x));
      dot.setAttribute("cy", String(end.y));
      dot.setAttribute("r", String(port.width / 2));
      dot.setAttribute(
        "transform",
        `translate(${end.x} ${end.y}) scale(${packetScale.x} ${packetScale.y}) translate(${-end.x} ${-end.y})`,
      );
    });
    const radius = connectorPorts[0].getBoundingClientRect().width / 2;
    packetDot.setAttribute("r", String(radius));
    packetHalo.setAttribute("r", String(radius * 3));
  }
  function fitHubAboveConnector() {
    // The SVG scales with the diagram while the logo and wordmark have pixel sizes.
    // Measure the unscaled group so resizing cannot compound its scale factor.
    const hubTop = network.getBoundingClientRect().top + hub.offsetTop;
    const availableHeight = discordEndpoint.getBoundingClientRect().top - hubTop - 18;
    const scale = Math.min(1, Math.max(0, availableHeight) / hub.offsetHeight);
    hub.style.setProperty("--hub-scale", String(scale));
  }
  const networkLayoutObserver = new ResizeObserver(() => {
    updateConnectorGeometry();
    fitHubAboveConnector();
    render();
  });
  networkLayoutObserver.observe(network);
  networkLayoutObserver.observe(hub);
  channelButtons.forEach((button) => networkLayoutObserver.observe(button));
  updateConnectorGeometry();
  fitHubAboveConnector();

  const artifactLabels = {
    slack: [
      "Log ingestion rewrite",
      "Bounded buffering, backpressure, and Kubernetes staging",
      "Load-test and rollout plan",
    ],
    teams: [
      "Compiler regression plan",
      "Property declaration order · TypeScript #62606",
      "Reproduction notes",
    ],
    github: ["LINQ investigation", "OrderBy().Take() · dotnet/runtime#96277", "Benchmark plan"],
    discord: [
      "Blueprint41 sample plan",
      "C# · Customers, orders, products, and relationships",
      "Graph model notes",
    ],
    email: [
      "AtomISP build investigation",
      "Linux 7c6a3a65ace7 · Nested min/max expansion",
      "Reproduction plan",
    ],
  };
  function element(tag, text, className) {
    const node = document.createElement(tag);
    if (text) node.textContent = text;
    if (className) node.className = className;
    return node;
  }
  function renderDetails(scene, prompt) {
    const labels = prompt.artifacts ?? artifactLabels[scene.id];
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
      [
        "Context brought together",
        (!customMessage && scene.source?.summary) || "The request and its conversation stay connected.",
      ],
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
      element("h4", customMessage ? "Your request" : prompt.label),
      element("p", customMessage?.reply ?? prompt.reply),
    );
    const list = element("ul");
    const checks = (!customMessage && prompt.checks) || [
      "Understand the request",
      "Gather the relevant context",
      "Prepare a proposed next step",
    ];
    checks.forEach((text) => list.append(element("li", text)));
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
          ? "Your message stays in this browser. No files have been generated or uploaded."
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
    const prompt = scene.prompts[0];
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
    const checks = prompt.checks ?? [
      "Keep the original request and context together.",
      "Review the proposed next step with your team.",
      "Decide what to change or share next.",
    ];
    checks.forEach((text) => list.append(element("li", text)));
    body.append(
      list,
      element(
        "p",
        "This is an illustrative preview. No pull request or document has been created.",
      ),
    );
    if (scene.source) {
      const attribution = element(
        "p",
        scene.source.attribution ?? "Fictional dialogue inspired by the public issue. ",
      );
      [scene.source, ...(scene.source.related ?? [])].forEach((reference, index) => {
        if (index > 0) attribution.append(" · ");
        const source = element("a", "Source: " + reference.label);
        source.href = reference.url;
        source.target = "_blank";
        source.rel = "noopener noreferrer";
        attribution.append(source);
      });
      body.append(attribution);
    }
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
  const messageInput = byId("message-input");
  const attachmentInput = byId("attachment-input");
  byId("attach-button").addEventListener("click", () => attachmentInput.click());
  attachmentInput.addEventListener("change", () => {
    const file = attachmentInput.files[0];
    byId("attachment-name").textContent = file ? file.name + " · local attachment" : "";
    byId("attachment-name").hidden = !file;
    if (file) announce("File selected. It will not be uploaded.");
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
          ? " (Selected file: " + attachmentInput.files[0].name + ")"
          : ""),
      reply:
        "Here’s an example next step: clarify the goal, gather the relevant context, and prepare a draft for your review. This message stays in your browser.",
    };
    position = receivedPosition(currentScene);
    displayScene(currentScene);
    render();
    byId("sender-name").textContent = "You";
    byId("human-avatar").textContent = "Y";
    byId("step-three").textContent = "Outline a possible next step";
    messageInput.value = "";
    attachmentInput.value = "";
    byId("attachment-name").hidden = true;
    byId("thread").scrollTop = 0;
    announce("Your message in " + scene.name + ". " + customMessage.reply);
  });

  scenes.forEach((scene) => updateCard(scene, scene.prompts[0]));
  render();
  setPlaying(playing);
})();
