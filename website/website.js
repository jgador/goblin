(() => {
  "use strict";

  // Presentation-only examples. This website has no service connections or Work state.
  const scenes = [
    {
      id: "slack",
      name: "Slack",
      flow: { travelSeconds: 0.6, thinkingSeconds: 0.8 },
      location: "#log-pipeline",
      steps: [
        "Understand the traffic problem",
        "Prepare and check the changes",
        "Plan the deployment",
      ],
      prompts: [
        {
          label: "Handle higher traffic",
          request:
            "Help us handle higher traffic without losing accepted logs. Prepare a tested change and a deployment plan for review.",
          reply:
            "I’ll investigate the problem, prepare the changes, and report the results here. I’ll ask when a decision needs your input.",
          receipt: "Proposed changes and a deployment plan, ready for review.",
          checks: [
            "Trace the current ingestion path and measure throughput, queue lag, and memory under burst traffic.",
            "Keep event IDs stable, acknowledge only after durable enqueue, and verify downstream deduplication when events are replayed.",
            "Test bounded buffering and backpressure in Kubernetes staging; compare delivery counts, latency, and resource use with the baseline, and prepare deployment and rollback steps for review.",
          ],
        },
        {
          label: "Protect logs during deployment",
          request:
            "Prepare the ingestion worker for Kubernetes so deployments do not lose accepted logs.",
          reply:
            "I’ll prepare the deployment changes, test shutdown and replacement under load, and report whether accepted logs reach storage.",
          receipt: "Delivery checks to support a deployment decision.",
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
          label: "Review release readiness",
          request:
            "Help us decide whether the ingestion changes are ready for a wider release. Provide a summary of the staging results and explain how to restore the previous version.",
          reply:
            "I’ll compare throughput and delivery with the previous version, identify remaining risks, and prepare a rollback plan for review.",
          receipt: "Evidence to decide whether the release is ready.",
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
      source: {
        label: "microsoft/TypeScript#62606",
        url: "https://github.com/microsoft/TypeScript/issues/62606",
        summary:
          "Anders H.’s closed issue reports a generic assignment error that disappears when two property declarations are reversed. This conversation imagines reviewing regression coverage.",
      },
      steps: [
        "Understand the reported problem",
        "Check valid and invalid cases",
        "Prepare tests for review",
      ],
      prompts: [
        {
          label: "Keep type checks consistent",
          request:
            "Check TypeScript #62606 for regressions. Valid assignments should work regardless of property declaration order.",
          reply:
            "I’ll test both declaration orders, compare the diagnostics, and prepare a regression test for review.",
          receipt: "Regression coverage the compiler team can review.",
          checks: [
            "Use the original DerivedTable example from the closed issue as the regression case.",
            "Check that the valid assignment is accepted with either property declaration order.",
            "Run focused compiler tests and return the diagnostics with the proposed regression coverage.",
          ],
        },
        {
          label: "Preserve type safety",
          request:
            "Make sure the proposed regression tests also reject invalid assignments. Consistent property order checks must preserve type safety.",
          reply:
            "I’ll add cases with missing properties and incorrect types, test both declaration orders, and include the expected errors.",
          receipt: "Valid and invalid cases, ready for review.",
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
            "Prepare a short team update on TypeScript #62606. Explain the original problem, the proposed tests, and what needs review.",
          reply:
            "I’ll link the closed issue and outline the coverage. I’ll state which tests are proposed and which results have been verified.",
          receipt: "A shared explanation of the issue and next decisions.",
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
      source: {
        label: "dotnet/runtime#96277",
        url: "https://github.com/dotnet/runtime/issues/96277",
        summary:
          "The public issue explores using a PriorityQueue for OrderBy().Take(), with memory, throughput, and stable ordering to consider.",
      },
      steps: [
        "Understand current performance",
        "Compare speed and memory use",
        "Prepare a recommendation",
      ],
      prompts: [
        {
          label: "Reduce memory use",
          request:
            "Investigate dotnet/runtime#96277. Can OrderBy(...).Take(10) use less memory without slowing down?",
          reply:
            "I’ll compare memory use and execution time, check that ordering stays correct, and report the tradeoffs for review.",
          receipt: "Evidence to guide a performance decision.",
          checks: [
            "Compare the existing OrderBy().Take() path with a bounded priority queue inside System.Linq.",
            "Measure execution time and allocated bytes; lower allocation alone is not a speedup.",
            "Preserve stable ordering and bring the findings back for review.",
          ],
        },
        {
          label: "Preserve ordering",
          request:
            "Keep the same ordering when reducing memory use. Equal keys must stay in source order, and Take must return the correct elements.",
          reply:
            "I’ll check equal keys, ascending and descending order, and Skip().Take(). I’ll include the proposed correctness tests for review.",
          receipt: "Ordering checks to support a safe change.",
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
          label: "Find when the change helps",
          request:
            "Find when the proposed change would help. Compare small and large inputs, different Take and Skip counts, and ToArray and ToList.",
          reply:
            "I’ll measure time and memory across those cases, report improvements and regressions, and recommend when to use the change.",
          receipt: "A benchmark plan to support the decision.",
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
        "Understand the team’s question",
        "Connect the sample records",
        "Prepare an example for review",
      ],
      prompts: [
        {
          label: "Make graph data easier to use",
          request:
            "Help the team understand how to use our sample graph in C#. Show a customer’s orders and products with Blueprint41.",
          reply:
            "I’ll prepare a small fictional dataset, a query example, and a schema change for review.",
          receipt: "A practical example the team can use.",
          checks: [
            "Define Customer, Order, and Product entities and their relationships using fictional sample records.",
            "Outline a Blueprint41 example using generated C# data objects to query a customer’s orders.",
            "Include a versioned schema change and checks that the sample relationships remain intact.",
          ],
        },
        {
          label: "Review changes before synchronising",
          request:
            "Help us review the Compare41 results before synchronising the sample graphs. Show which nodes, relationships, and properties would change.",
          reply:
            "I’ll list the proposed changes, highlight deletions, and prepare checks so the team can decide what to apply.",
          receipt: "A clear view of the changes before approval.",
          artifacts: [
            "Graph comparison review",
            "Compare41 · Proposed changes and comparison scope",
            "Validation checklist",
          ],
          checks: [
            "Review model and data differences between the two sample graphs, including the entity mapping.",
            "List proposed additions, updates, deletions, and relationship changes within the selected subgraph.",
            "Define before-and-after query checks and record counts for the proposed sync scope.",
          ],
        },
        {
          label: "Connect answers to evidence",
          request:
            "Plan a small AI example that answers which products a customer ordered and shows the records supporting the answer.",
          reply:
            "I’ll use the sample graph to plan the query and show the supporting records. I’ll include cases where information is missing.",
          receipt: "Answers the team can check against the source data.",
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
        "Understand the reported failure",
        "Compare the build results",
        "Prepare findings for review",
      ],
      prompts: [
        {
          label: "Review a kernel build fix",
          request:
            "Review whether the AtomISP fix addresses the reported build failure. Use the historical 32-bit setup described with commit 7c6a3a65ace7.",
          reply:
            "I’ll compare builds with and without the patch, record the results, and make any unverified details clear.",
          receipt: "Build evidence to support a review.",
          checks: [
            "Reconstruct the reported Slackware 15.0 32-bit build environment and record the kernel revision, compiler, and configuration.",
            "Compare the affected AtomISP build on 5.15 before and after the focused patch, including preprocessed output and compiler memory use.",
            "Return build logs and measured results; keep any unverified reproduction details explicit.",
          ],
        },
        {
          label: "Review the focused backport",
          request:
            "Check that the focused AtomISP backport preserves behaviour and can be applied to the target version without the wider min/max rewrite.",
          reply:
            "I’ll compare helper results, check the affected callers, and document the patch dependencies and remaining review questions.",
          receipt: "A focused patch with clear review checks.",
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
          label: "Explain the patch decision",
          request:
            "Prepare a short summary explaining why the AtomISP patch was accepted directly and the other two patches were left for their maintainers.",
          reply:
            "I’ll explain the build problem and the backport decision, link the public reply and accepted commit, and keep historical facts separate from proposed checks.",
          receipt: "The decision and its sources, ready to share.",
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
  const compactLayout = window.matchMedia("(max-width: 600px)");
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
  const mobileConnectors = Object.fromEntries(
    scenes.map((scene) => {
      const group = byId(`mobile-connector-${scene.id}`);
      return [
        scene.id,
        {
          strands: [...group.querySelectorAll("path")],
        },
      ];
    }),
  );
  const mobileFiberLights = [...document.querySelectorAll(".mobile-fiber-glow, .mobile-fiber-light")];
  const connectorPorts = scenes.map((scene) =>
    document.querySelector(`[data-channel="${scene.id}"] .connection-port`),
  );
  const connectorDots = scenes.map((scene) => byId(`dot-${scene.id}`));
  const packet = byId("packet");
  const packetDot = packet.querySelector(".packet-dot");
  const packetHalo = packet.querySelector(".packet-halo");
  const packetScale = { x: 1, y: 1 };
  const hubCharacter = createHubCharacter();
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

  function receivedPosition(index) {
    const timing = sceneTimings[index];
    return timing.start + timing.received + 0.2;
  }

  // Animates the hub's Goblin mark through each conversation. CSS plays the entrance so
  // the mark is never painted at rest first; this takes over once it has finished.
  function createHubCharacter() {
    const mark = { centerX: 324.1, bottom: 602.03, headX: 325, headBottom: 274.84, eyeY: 101.28 };
    const settle = { stiffness: 120, damping: 14, mass: 0.8 };
    const lively = { stiffness: 110, damping: 10, mass: 0.9 };
    const body = byId("hub-body");
    const head = byId("hub-head");
    const gaze = byId("hub-eyes");
    const blocks = [
      { element: byId("hub-block-left"), centerX: 160.52, phase: 0 },
      { element: byId("hub-block-right"), centerX: 487.68, phase: Math.PI },
    ];
    const eyes = ["left", "right"].map((side, index) => ({
      shape: byId(`hub-eye-${side}`),
      smile: byId(`hub-smile-${side}`),
      x: [198.98, 449.23][index],
    }));
    const moving = [body, head, gaze, ...blocks.map(({ element }) => element)];
    const looks = new Map();
    let clock = 0;
    let resting = true;

    const clamp = (value) => Math.min(1, Math.max(0, value));
    const ease = (value) => value * value * (3 - 2 * value);
    const progress = (time, start, end, curve = ease) =>
      curve(clamp((time - start) / (end - start)));
    const wave = (time, period, phase = 0) => Math.sin((2 * Math.PI * time) / period + phase);
    const backOut = (value) => 1 + 3 * (value - 1) ** 3 + 2 * (value - 1) ** 2;
    const pivot = (x, y, transform) => `translate(${x} ${y}) ${transform} translate(${-x} ${-y})`;
    const hash = (value) => {
      const noise = Math.sin(value * 12.9898) * 43758.5453;
      return noise - Math.floor(noise);
    };

    // Step response of a damped spring, matching the promo video's motion.
    function spring(time, { stiffness, damping, mass } = settle) {
      if (time <= 0) return 0;
      const omega = Math.sqrt(stiffness / mass);
      const zeta = damping / (2 * Math.sqrt(stiffness * mass));
      if (zeta >= 1) return 1 - Math.exp(-omega * time) * (1 + omega * time);
      const damped = omega * Math.sqrt(1 - zeta * zeta);
      return (
        1 -
        Math.exp(-zeta * omega * time) *
          (Math.cos(damped * time) + ((zeta * omega) / damped) * Math.sin(damped * time))
      );
    }

    // Springs towards each [time, value] target in turn, starting from zero.
    function springPath(time, keys, config) {
      let value = 0;
      let previous = 0;
      for (const [start, target] of keys) {
        value += (target - previous) * spring(time - start, config);
        previous = target;
      }
      return value;
    }

    // A damped wobble that starts at 1 on impact.
    const impact = (time, start) =>
      time < start ? 0 : Math.exp(-(time - start) / 0.17) * Math.cos((time - start) * 18);

    function blink(time) {
      const period = 3.4;
      const cycle = Math.floor(time / period);
      let amount = 0;
      for (const index of [cycle - 1, cycle]) {
        const start = index * period + hash(index + 1) * 1.6;
        const starts = hash(index + 7) > 0.65 ? [start, start + 0.32] : [start];
        for (const blinkStart of starts) {
          const elapsed = time - blinkStart;
          if (elapsed > 0 && elapsed < 0.3)
            amount = Math.max(
              amount,
              elapsed < 0.1 ? (elapsed / 0.1) ** 2 : 1 - ease(clamp((elapsed - 0.13) / 0.17)),
            );
        }
      }
      return Math.min(1, amount);
    }

    function setAttributes(element, values) {
      Object.entries(values).forEach(([name, value]) => element.setAttribute(name, value));
    }

    function rest() {
      if (resting) return;
      resting = true;
      moving.forEach((element) => element.removeAttribute("transform"));
      eyes.forEach(({ shape, smile }) => {
        shape.removeAttribute("transform");
        shape.removeAttribute("opacity");
        setAttributes(smile, { opacity: "0", transform: "" });
      });
    }

    // Records where each channel card sits relative to the mark so the eyes can find it.
    function measure() {
      const logo = byId("hub-character").getBoundingClientRect();
      const centerX = logo.left + logo.width / 2;
      const centerY = logo.top + logo.height / 2;
      channelButtons.forEach((button) => {
        const card = button.getBoundingClientRect();
        const x = card.left + card.width / 2 - centerX;
        const y = card.top + card.height / 2 - centerY;
        const length = Math.hypot(x, y) || 1;
        looks.set(button.dataset.channel, {
          x: (24 * x) / length,
          y: ((y < 0 ? 18 : 14) * y) / length,
          side: Math.abs(x / length) > 0.2 ? Math.sign(x) : -1,
        });
      });
    }

    function update(scene, time, timing) {
      if (reducedMotion.matches) {
        rest();
        return;
      }
      resting = false;
      const { asking, thinking, replying, received } = timing;
      const look = looks.get(scene.id) ?? { x: 0, y: 0, side: -1 };
      const awake = progress(clock, 1.3, 1.7);

      // Look at the requesting channel, glance up when the request arrives, squint and
      // drift while thinking, watch the response leave, and then face forward again.
      const considering =
        progress(time, thinking + 0.3, thinking + 0.5) *
        (1 - progress(time, replying - 0.15, replying + 0.05));
      const gazeX =
        springPath(time, [
          [asking - 0.1, look.x],
          [thinking, look.x * 0.4],
          [replying - 0.05, look.x],
          [received, 0],
        ]) +
        9 * wave(time - thinking, 1.3) * considering;
      const gazeY = springPath(time, [
        [asking - 0.1, look.y],
        [thinking, -14],
        [replying - 0.05, look.y],
        [received, 0],
      ]);
      const curious = springPath(
        time,
        [
          [thinking, 1],
          [thinking + 0.55, 0],
        ],
        lively,
      );
      const size = springPath(time, [
        [thinking, 0.3],
        [thinking + 0.5, -0.28],
        [replying, 0],
      ]);
      const nod = springPath(time, [
        [replying, 8],
        [replying + 0.2, 0],
      ]);

      // Hop with anticipation, stretch in the air, and squash on landing.
      const launch = received + 0.28;
      const land = received + 0.78;
      const hop = (offset) => {
        const amount = (time - offset - launch) / (land - launch);
        return amount <= 0 || amount >= 1 ? 0 : -70 * 4 * amount * (1 - amount);
      };
      const airborne = (time - launch) / (land - launch);
      const crouch =
        progress(time, received + 0.05, launch - 0.07) -
        progress(time, launch - 0.07, launch + 0.03);
      const stretch = airborne > 0 && airborne < 1 ? 0.08 * Math.abs(1 - 2 * airborne) : 0;
      const landing = impact(time, land);
      const lift = hop(0);
      const smiling =
        progress(time, launch + 0.07, launch + 0.17) - progress(time, land + 0.5, land + 0.67);
      const arc =
        progress(time, launch + 0.17, launch + 0.37, backOut) *
        (1 - progress(time, land + 0.4, land + 0.5));

      const bodyScaleX = 1 + awake * (0.05 * crouch - stretch / 2 + 0.06 * landing);
      const bodyScaleY = 1 + awake * (-0.1 * crouch + stretch - 0.12 * landing);
      body.setAttribute(
        "transform",
        pivot(
          mark.centerX,
          mark.bottom,
          `translate(0 ${awake * lift}) scale(${bodyScaleX} ${bodyScaleY})`,
        ),
      );
      blocks.forEach(({ element, centerX, phase }) => {
        const raise = 26 * considering * Math.max(0, wave(time - thinking, 0.87, phase)) ** 2;
        const breathe = 0.015 * wave(clock, 2, phase + Math.PI / 2) - 0.05 * impact(time, land);
        element.setAttribute(
          "transform",
          pivot(
            centerX,
            mark.bottom,
            `translate(0 ${-awake * raise}) scale(1 ${1 + awake * breathe})`,
          ),
        );
      });

      const headX = 0.3 * gazeX;
      const headY = -5 * wave(clock, 2) - 10 * curious + nod + 0.5 * (hop(0.07) - lift);
      const tilt = 8 * look.side * curious + 0.05 * gazeX;
      const squash = 0.06 * impact(time, land + 0.07);
      const headScale = `scale(${1 + (awake * squash) / 2} ${1 - awake * squash})`;
      head.setAttribute(
        "transform",
        `translate(${awake * headX} ${awake * headY}) ` +
          pivot(mark.headX, 200, `rotate(${awake * tilt})`) +
          " " +
          pivot(mark.headX, mark.headBottom, headScale),
      );
      gaze.setAttribute("transform", `translate(${awake * gazeX} ${awake * gazeY})`);

      const shut = awake * Math.max(blink(clock), smiling);
      eyes.forEach(({ shape, smile, x }) => {
        const grow =
          1 + awake * (size + (Math.sign(x - mark.headX) === look.side ? 0.12 * curious : 0));
        const scaleY = grow + (0.08 - grow) * shut;
        const scaleX = (1 - 0.12 * (grow - 1)) * (1 + 0.06 * shut);
        setAttributes(shape, {
          transform: pivot(x, mark.eyeY, `scale(${scaleX} ${scaleY})`),
          opacity: awake * smiling > 0.97 ? "0" : "1",
        });
        const happy = awake * arc;
        setAttributes(smile, {
          opacity: String(Math.min(1, happy * 4)),
          transform: pivot(x, mark.eyeY, `scale(1 ${Math.max(0.05, happy)})`),
        });
      });
    }

    return {
      advance: (seconds) => {
        clock += seconds;
      },
      measure,
      update,
    };
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
    card.title = scene.name + ": " + prompt.request + "\nGoblin: " + prompt.reply;
    card.setAttribute(
      "aria-label",
      "Play the " + scene.name + " conversation. " + prompt.request,
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
    byId("sender-name").textContent = scene.name;
    byId("sender-icon").src = `assets/${scene.id}.svg`;
    byId("request-text").textContent = prompt.request;
    byId("reply-text").textContent = prompt.reply;
    byId("receipt-text").textContent = prompt.receipt;
    ["step-one", "step-two", "step-three"].forEach((id, stepIndex) => {
      byId(id).textContent = scene.steps[stepIndex];
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
    byId("artifact-message").hidden = phase !== "received";
    byId("goblin-hub").classList.toggle("is-thinking", phase === "thinking");
    const status = {
      ready: "Ready for a request",
      asking: "Receiving a request",
      thinking: "Preparing a response",
      replying: "Returning a response",
      received: "Ready for your review",
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
    if (index !== currentScene) displayScene(index);
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
    hubCharacter.update(scenes[index], localTime, timing);
    byId("incoming-message").style.opacity = "1";
    const inTransit =
      !compactLayout.matches && !reducedMotion.matches && (phase === "asking" || phase === "replying");
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
      label.textContent = phase === "asking" ? "Request" : "Response";
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

  function updateMobileFiberSpeed() {
    if (!compactLayout.matches || reducedMotion.matches) return;
    mobileFiberLights.forEach(updateFiberAnimationRate);
  }

  function updateFiberAnimationRate(light) {
    light.getAnimations().forEach((animation) => {
      if (animation.playbackRate !== speeds[speedIndex]) {
        animation.updatePlaybackRate(speeds[speedIndex]);
      }
    });
  }

  connections.addEventListener("animationstart", (event) => {
    if (event.animationName === "fiber-travel" && event.target instanceof Element) {
      updateFiberAnimationRate(event.target);
    }
  });

  function tick(timestamp) {
    animationFrame = null;
    if (!playing) return;
    if (lastFrame !== null) {
      const elapsed = Math.min((timestamp - lastFrame) / 1000, 0.1) * speeds[speedIndex];
      position = (position + elapsed) % duration;
      hubCharacter.advance(elapsed);
    }
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
      currentScene = -1;
      position = 0;
      render();
    }
    setPlaying(!playing);
  }

  function selectChannel(id) {
    const index = scenes.findIndex((scene) => scene.id === id);
    if (index < 0) return;
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
    currentScene = -1;
    position = 0;
    render();
    setPlaying(true);
    announce("Replaying from the beginning.");
  });
  byId("speed-button").addEventListener("click", () => {
    speedIndex = (speedIndex + 1) % speeds.length;
    updateMobileFiberSpeed();
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
    requestAnimationFrame(updateMobileFiberSpeed);
  });
  const hub = byId("goblin-hub");
  const network = byId("network");
  const composer = byId("message-composer");
  const conversationPanel = byId("conversation-panel");
  const benefits = document.querySelector(".benefits");
  function placeSupportingContent() {
    if (compactLayout.matches) {
      conversationPanel.after(composer, benefits);
    } else {
      byId("view-conversation").append(composer);
      network.parentElement.append(benefits);
    }
  }
  compactLayout.addEventListener("change", placeSupportingContent);
  placeSupportingContent();
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
  function mobileCurve(start, end, vertical) {
    const dx = end.x - start.x;
    const dy = end.y - start.y;
    const first = vertical
      ? { x: start.x, y: start.y + dy * 0.38 }
      : { x: start.x + dx * 0.42, y: start.y };
    const second = vertical
      ? { x: end.x, y: end.y - dy * 0.38 }
      : { x: end.x - dx * 0.42, y: end.y };
    return [start, first, second, end];
  }
  function svgPoint(point, toSvg) {
    const converted = new DOMPoint(point.x, point.y).matrixTransform(toSvg);
    return `${converted.x.toFixed(2)} ${converted.y.toFixed(2)}`;
  }
  function mobileTrackPath(points, toSvg) {
    return `M${svgPoint(points[0], toSvg)}C${points
      .slice(1)
      .map((point) => svgPoint(point, toSvg))
      .join(" ")}`;
  }
  function updateMobileConnectors(toSvg) {
    const cards = Object.fromEntries(
      channelButtons.map((button) => [button.dataset.channel, button.getBoundingClientRect()]),
    );
    const logo = byId("hub-character").getBoundingClientRect();
    const endpoints = {
      slack: {
        start: { x: cards.slack.right - 1, y: cards.slack.bottom - 9 },
        end: { x: logo.left + 1, y: logo.top + 9 },
      },
      teams: {
        start: { x: cards.teams.left + 1, y: cards.teams.bottom - 9 },
        end: { x: logo.right - 1, y: logo.top + 9 },
      },
      github: {
        start: { x: cards.github.right - 1, y: cards.github.top + 9 },
        end: { x: logo.left + 1, y: logo.bottom - 9 },
      },
      email: {
        start: { x: cards.email.left + 1, y: cards.email.top + 9 },
        end: { x: logo.right - 1, y: logo.bottom - 9 },
      },
      discord: {
        start: { x: cards.discord.left + cards.discord.width / 2, y: cards.discord.bottom - 1 },
        end: { x: logo.left + logo.width / 2, y: logo.top + 1 },
      },
    };
    scenes.forEach((scene) => {
      const { start, end } = endpoints[scene.id];
      const curve = mobileCurve(start, end, scene.id === "discord");
      const path = mobileTrackPath(curve, toSvg);
      mobileConnectors[scene.id].strands.forEach((strand) => strand.setAttribute("d", path));
    });
    updateMobileFiberSpeed();
  }
  function updateConnectorGeometry() {
    const matrix = connections.getScreenCTM();
    if (!matrix) return;
    const toSvg = matrix.inverse();
    if (compactLayout.matches) {
      updateMobileConnectors(toSvg);
      return;
    }
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
    if (compactLayout.matches) {
      hub.style.setProperty("--hub-scale", "1");
      return;
    }
    // The SVG scales with the diagram while the logo and wordmark have pixel sizes.
    // Measure the unscaled group so resizing cannot compound its scale factor.
    const hubTop = network.getBoundingClientRect().top + hub.offsetTop;
    const availableHeight = discordEndpoint.getBoundingClientRect().top - hubTop - 18;
    const scale = Math.min(1, Math.max(0, availableHeight) / hub.offsetHeight);
    hub.style.setProperty("--hub-scale", String(scale));
  }
  const networkLayoutObserver = new ResizeObserver(() => {
    if (compactLayout.matches) fitHubAboveConnector();
    updateConnectorGeometry();
    if (!compactLayout.matches) fitHubAboveConnector();
    hubCharacter.measure();
    render();
  });
  networkLayoutObserver.observe(network);
  networkLayoutObserver.observe(hub);
  channelButtons.forEach((button) => networkLayoutObserver.observe(button));
  if (compactLayout.matches) fitHubAboveConnector();
  updateConnectorGeometry();
  if (!compactLayout.matches) fitHubAboveConnector();
  hubCharacter.measure();

  const artifactLabels = {
    slack: [
      "Proposed changes",
      "Verification summary and deployment plan",
      "Verification and deployment plan",
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
      element("h3", "Follow the progress"),
      element("p", "See the request, its context, and the proposed result together."),
    );
    [
      ["Request received in " + scene.name, prompt.request],
      [
        "Related information collected",
        scene.source?.summary || "The request and its conversation stay connected.",
      ],
      ["A response prepared", prompt.reply],
      ["Ready for review", "Review the proposed result and decide what happens next."],
    ].forEach(([title, description]) => {
      const item = element("div", null, "activity-item");
      item.append(element("strong", title), element("p", description));
      activity.append(item);
    });
    const work = byId("view-work");
    const workCard = element("div", null, "work-card");
    workCard.append(
      element("span", "Awaiting review", "review-badge"),
      element("h4", prompt.label),
      element("p", prompt.reply),
    );
    const list = element("ul");
    const checks = prompt.checks || [
      "Understand the request",
      "Gather the relevant context",
      "Prepare a proposed next step",
    ];
    checks.forEach((text) => list.append(element("li", text)));
    workCard.append(list);
    work.replaceChildren(
      element("h3", "Review the proposed work"),
      element("p", "Check the planned steps and the conversation that explains them."),
      workCard,
    );
    const files = byId("view-files");
    files.replaceChildren(
      element("h3", "Explore the proposed result"),
      element("p", "Open an example deliverable to see what your team could review."),
    );
    const fileCard = byId("artifact-message").querySelector(".artifact-card").cloneNode(true);
    fileCard.querySelectorAll("[id]").forEach((node) => node.removeAttribute("id"));
    files.append(fileCard);
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

  scenes.forEach((scene) => updateCard(scene, scene.prompts[0]));
  render();
  setPlaying(playing);
})();
