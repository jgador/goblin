// Interactive experience preview. State and agent replies are simulated and reset on refresh.
export {};

type WorkStatus = "waiting" | "review" | "working" | "done" | "paused";
type DetailTab = "conversation" | "activity" | "outputs";
type WorkFilter = "all" | "attention" | "done";
type Message = { author: "You" | "Goblin"; text: string };
type WorkEvent = [title: string, description: string, time: string];
interface WorkItem {
    id: string;
    title: string;
    objective: string;
    status: WorkStatus;
    updated: string;
    created: string;
    request: string;
    summary: string;
    next: string;
    kind: string;
    outputTitle: string;
    outputType: string;
    outcome: string;
    steps: string[];
    events: WorkEvent[];
    messages?: Message[];
}
interface Conversation {
    id: string;
    title: string;
    messages: Message[];
    workId?: string;
}

function $<T extends HTMLElement = HTMLElement>(selector: string): T {
    const element = document.querySelector<T>(selector);
    if (!element) throw new Error("Missing preview element: " + selector);
    return element;
}
const icons: Record<string, string> = {
    plus: '<path d="M12 5v14M5 12h14"/>',
    chat: '<path d="M21 11.5a8.5 8.5 0 0 1-8.5 8.5H4l-3 2V11.5A8.5 8.5 0 0 1 9.5 3H13a8 8 0 0 1 8 8.5Z"/><path d="M7 9h9M7 13h6"/>',
    work: '<rect x="4" y="5" width="16" height="16" rx="3"/><path d="M9 5V3h6v2M8 11l1 1 2-2M13 11h3M8 16h8"/>',
    arrow: '<path d="M5 12h14m-5-5 5 5-5 5"/>',
    up: '<path d="M12 19V5m-6 6 6-6 6 6"/>',
    check: '<path d="m5 12 4 4L19 6"/>',
    circleCheck: '<circle cx="12" cy="12" r="9"/><path d="m8 12 3 3 5-6"/>',
    wait: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5m0 4v.1"/>',
    clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    activity: '<path d="M3 12h4l3-7 4 14 3-7h4"/>',
    file: '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z"/><path d="M14 2v6h6M8 13h8M8 17h6"/>',
    refresh:
        '<path d="M20 7v5h-5M4 17v-5h5"/><path d="M6.1 7a7 7 0 0 1 11.5-2L20 8M4 16l2.4 3A7 7 0 0 0 18 17"/>',
    chevron: '<path d="m9 5 7 7-7 7"/>',
    back: '<path d="m14 5-7 7 7 7"/>',
    link: '<path d="m10 13 4-4M8 16l-1 1a4 4 0 0 1-6-6l5-5a4 4 0 0 1 6 0m4 2 1-1a4 4 0 0 1 6 6l-5 5a4 4 0 0 1-6 0" transform="translate(1 -1) scale(.9)"/>',
    pause: '<path d="M8 5v14M16 5v14"/>',
    spark: '<path d="m12 3 2.5 6.5L21 12l-6.5 2.5L12 21l-2.5-6.5L3 12l6.5-2.5Z"/>',
    branch: '<circle cx="6" cy="5" r="2"/><circle cx="18" cy="5" r="2"/><circle cx="6" cy="19" r="2"/><path d="M6 7v10M18 7v3a4 4 0 0 1-4 4H6"/>',
    book: '<path d="M12 5v16M12 5C9 2 4 3 2 4v15c3-1 7-1 10 2 3-3 7-3 10-2V4c-2-1-7-2-10 1Z"/>',
    inbox: '<path d="M4 3h16l2 12v6H2v-6Z"/><path d="M2 15h6l2 3h4l2-3h6"/>',
};
function icon(name: string, cls = "") {
    return (
        '<svg class="' +
        cls +
        '" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
        (icons[name] || icons.work) +
        "</svg>"
    );
}
function escapeHtml(s: unknown) {
    return String(s).replace(
        /[&<>"']/g,
        (c) =>
            (
                ({
                    "&": "&amp;",
                    "<": "&lt;",
                    ">": "&gt;",
                    '"': "&quot;",
                    "'": "&#39;",
                }) as Record<string, string>
            )[c],
    );
}
const statusMeta: Record<
    WorkStatus,
    { label: string; short: string; icon: string }
> = {
    waiting: { label: "Needs your input", short: "Needs you", icon: "wait" },
    review: {
        label: "Ready for review",
        short: "Ready for review",
        icon: "circleCheck",
    },
    working: { label: "In progress", short: "In progress", icon: "activity" },
    done: { label: "Completed", short: "Completed", icon: "check" },
    paused: { label: "Paused", short: "Paused", icon: "pause" },
};
const seeds: WorkItem[] = [
    {
        id: "GB-024",
        title: "Make Goblin easier to set up",
        objective:
            "A first-time user should be able to run Goblin without reading the source.",
        status: "waiting",
        updated: "3m",
        created: "Yesterday",
        request:
            "Can you make Goblin easier to set up? There are quite a few manual steps right now. Please track this so we can pick it up later.",
        summary:
            "I’ve mapped the setup steps. The main friction is configuring providers and environment variables by hand.",
        next: "One decision before I continue.",
        kind: "setup",
        outputTitle: "A simpler first-run experience",
        outputType: "Implementation brief",
        outcome:
            "A guided setup that gets from a fresh checkout to a working Goblin in a few short steps.",
        steps: [
            "Check the local environment",
            "Connect the first agent provider",
            "Confirm your workspace and start Goblin",
        ],
        events: [
            [
                "Work created",
                "Your conversation became GB-024. The original request stays attached.",
                "Yesterday, 2:20 PM",
            ],
            [
                "Goblin picked up the work",
                "Reviewed the current setup and mapped the manual steps.",
                "Today, 9:12 AM",
            ],
            [
                "Waiting for your input",
                "Choose how the first-run setup should work.",
                "3 minutes ago",
            ],
        ],
    },
    {
        id: "GB-023",
        title: "Put together a weekly work digest",
        objective:
            "A short, useful summary of what moved forward, what is blocked, and what needs me.",
        status: "review",
        updated: "18m",
        created: "Yesterday",
        request:
            "I’d like a weekly digest of what you worked on. Keep it short and make anything that needs me obvious.",
        summary:
            "The first digest format is ready. I kept it to completed work, what is moving, and decisions for you.",
        next: "The first draft is ready to look at.",
        kind: "digest",
        outputTitle: "Your week with Goblin",
        outputType: "Draft · Weekly digest",
        outcome:
            "Three things moved forward this week. One decision needs you.",
        steps: [
            "Done: release checklist organized and ready to reuse.",
            "Moving: setup improvements and repository connection.",
            "Needs you: choose the first-run setup experience.",
        ],
        events: [
            [
                "Work created",
                "Captured the weekly digest request.",
                "Yesterday, 3:40 PM",
            ],
            [
                "Draft prepared",
                "Grouped progress, completed work, and decisions.",
                "Today, 8:48 AM",
            ],
            [
                "Ready for review",
                "The first digest is waiting for your feedback.",
                "18 minutes ago",
            ],
        ],
    },
    {
        id: "GB-021",
        title: "Find what is slowing down CI",
        objective:
            "Understand the slowest build steps and suggest a small, measurable improvement.",
        status: "working",
        updated: "27m",
        created: "2 days ago",
        request:
            "The CI builds feel slow. Can you investigate where the time goes and propose an improvement?",
        summary:
            "I’m comparing the build steps and checking which dependencies can be cached. I’ll bring back a focused recommendation.",
        next: "Comparing build steps and cache behavior.",
        kind: "ci",
        outputTitle: "CI performance findings",
        outputType: "Investigation",
        outcome:
            "Start with dependency caching and measure the next ten builds before making broader changes.",
        steps: [
            "Add a cache keyed to the dependency lockfile.",
            "Keep restore and build timings visible in CI.",
            "Compare median build duration after ten runs.",
        ],
        events: [
            ["Work created", "Captured the CI investigation.", "2 days ago"],
            [
                "Investigation started",
                "Mapped restore, build, and test steps.",
                "Today, 8:55 AM",
            ],
            [
                "Comparing options",
                "Checking dependency cache behavior.",
                "27 minutes ago",
            ],
        ],
    },
    {
        id: "GB-020",
        title: "Connect a repository to Goblin",
        objective:
            "Let me give Goblin a repository once, so I do not need to paste its context every time.",
        status: "working",
        updated: "1h",
        created: "2 days ago",
        request:
            "Help me design the experience for connecting a repository. Start with GitHub, but keep the user experience simple.",
        summary:
            "I’ve outlined the connection flow. I’m checking how repository context should follow a work item.",
        next: "Working through the repository connection flow.",
        kind: "repo",
        outputTitle: "Repository connection flow",
        outputType: "Experience outline",
        outcome:
            "Connect GitHub, choose a repository, and let its context follow related work.",
        steps: [
            "Choose a repository from connected GitHub access.",
            "Confirm the workspace and default branch.",
            "Attach repository context to new work when it is relevant.",
        ],
        events: [
            [
                "Work created",
                "Captured the repository connection request.",
                "2 days ago",
            ],
            [
                "Flow outlined",
                "Separated account connection from repository selection.",
                "Today, 8:30 AM",
            ],
        ],
    },
    {
        id: "GB-018",
        title: "Organize the release checklist",
        objective: "A repeatable checklist for a small Goblin release.",
        status: "done",
        updated: "Yesterday",
        created: "3 days ago",
        request:
            "Put together a release checklist I can reuse. Include verification, notes, and a final review.",
        summary:
            "The release checklist is complete and approved. The final version is attached to this work.",
        next: "Release checklist approved and kept here.",
        kind: "release",
        outputTitle: "Goblin release checklist",
        outputType: "Final · Checklist",
        outcome:
            "A lightweight checklist to take a change from verified to released.",
        steps: [
            "Confirm the build and test results.",
            "Review changes and write concise release notes.",
            "Check configuration and migration requirements.",
            "Approve, tag the release, and verify startup.",
        ],
        events: [
            [
                "Work created",
                "Captured the release checklist request.",
                "3 days ago",
            ],
            [
                "Checklist prepared",
                "Covered verification, configuration, and release notes.",
                "2 days ago",
            ],
            [
                "You approved the result",
                "Work completed. The final checklist remains attached.",
                "Yesterday, 4:15 PM",
            ],
        ],
    },
];
let work: WorkItem[];
let selectedId: string;
let view: "work" | "chat";
let tab: DetailTab;
let filter: WorkFilter;
let choice: "guided" | "terminal";
let detailOpen: boolean;
let chats: Conversation[];
let activeChatId: string | null;
let requestChangesFor: string | null;
let sequence: number;
let toastTimer: ReturnType<typeof setTimeout> | undefined;
function initialize() {
    work = structuredClone(seeds);
    selectedId = "GB-024";
    view = "work";
    tab = "conversation";
    filter = "all";
    choice = "guided";
    detailOpen = false;
    chats = [];
    activeChatId = null;
    requestChangesFor = null;
    sequence = 25;
}
initialize();
function current(): WorkItem {
    const item = work.find((w) => w.id === selectedId);
    if (!item) throw new Error("The selected work is missing.");
    return item;
}
function currentChat() {
    return chats.find((c) => c.id === activeChatId);
}
function label(w: WorkItem, pill = false) {
    const s = statusMeta[w.status];
    return (
        '<span class="' +
        (pill ? "status-pill" : "status-line") +
        " " +
        w.status +
        '">' +
        icon(s.icon) +
        (pill ? s.label : s.short) +
        "</span>"
    );
}
function botAvatar() {
    return '<div class="bot-avatar"><img src="/assets/branding/icon.svg" alt=""></div>';
}
function message(
    author: Message["author"],
    content: string,
    time = "Just now",
) {
    return (
        '<div class="message">' +
        (author === "Goblin" ? botAvatar() : '<div class="avatar">JG</div>') +
        '<div><div class="message-name">' +
        author +
        "<time>" +
        time +
        '</time></div><div class="message-text">' +
        content +
        "</div></div></div>"
    );
}
function toast(text: string) {
    const e = $("#toast");
    e.textContent = text;
    e.classList.add("show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => e.classList.remove("show"), 3500);
}
function render() {
    const needs = work.filter((w) =>
        ["waiting", "review"].includes(w.status),
    ).length;
    $("#app").innerHTML =
        '<div class="app-shell"><aside class="sidebar"><div class="brand"><img src="/assets/branding/icon.svg" alt="Goblin"><span>goblin</span></div><button class="new-chat" data-action="new-chat">' +
        icon("plus") +
        '<span>New conversation</span></button><nav class="nav" aria-label="Main navigation"><button class="nav-button ' +
        (view === "work" ? "active" : "") +
        '" data-action="view-work" ' +
        (view === "work" ? 'aria-current="page"' : "") +
        ">" +
        icon("work") +
        '<span>Work</span><span class="nav-count">' +
        needs +
        '</span></button><button class="nav-button ' +
        (view === "chat" ? "active" : "") +
        '" data-action="view-chat" ' +
        (view === "chat" ? 'aria-current="page"' : "") +
        ">" +
        icon("chat") +
        '<span>Conversations</span></button></nav><div class="sidebar-note"><div class="section-label">Your workspace</div><p>A place for the things<br>we’re working on together.</p></div><div class="sidebar-bottom"><div class="availability"><i class="live-dot"></i><span>Goblin is available</span></div><div class="user"><div class="avatar">JG</div><div>Jesse Gador<small>Personal workspace</small></div></div></aside><main class="main-shell"><div class="preview-bar"><div class="preview-left"><button class="mobile-menu" data-action="mobile-nav" aria-label="' +
        (view === "chat" ? "Open work" : "New conversation") +
        '"><img src="/assets/branding/icon.svg" alt="">goblin</button><span class="preview-label"><span></span>Experience preview</span></div><div class="preview-right"><a class="connection-link" href="/">Connection settings</a><span>Example work · resets on refresh</span><button class="reset" data-action="reset" aria-label="Reset experience preview">' +
        icon("refresh") +
        "Reset preview</button></div></div>" +
        (view === "work" ? renderWorkspace() : renderChat()) +
        "</main></div>";
    document.title = (view === "work" ? "Work" : "Conversation") + " · Goblin";
}
function renderWorkspace() {
    return (
        '<div class="workspace ' +
        (detailOpen ? "detail-open" : "") +
        '">' +
        renderList() +
        '<section class="detail" aria-label="Work details">' +
        renderDetail() +
        "</section></div>"
    );
}
function renderList() {
    const needs = work.filter((w) =>
        ["waiting", "review"].includes(w.status),
    ).length;
    const sections: [string, WorkItem[]][] =
        filter === "attention"
            ? [
                  [
                      "Needs your attention",
                      work.filter((w) =>
                          ["waiting", "review"].includes(w.status),
                      ),
                  ],
              ]
            : filter === "done"
              ? [["Completed", work.filter((w) => w.status === "done")]]
              : [
                    [
                        "Needs your attention",
                        work.filter((w) =>
                            ["waiting", "review"].includes(w.status),
                        ),
                    ],
                    [
                        "Moving forward",
                        work.filter((w) =>
                            ["working", "paused"].includes(w.status),
                        ),
                    ],
                    [
                        "Recently completed",
                        work.filter((w) => w.status === "done"),
                    ],
                ];
    return (
        '<section class="work-list" aria-label="Tracked work"><div class="list-heading"><div><h1>Work</h1><p>' +
        work.filter((w) => w.status !== "done").length +
        " open · " +
        needs +
        ' need you</p></div><button class="icon-button" data-action="new-chat" aria-label="Give Goblin new work">' +
        icon("plus") +
        '</button></div><div class="filter-row" role="group" aria-label="Filter work">' +
        [
            ["all", "All work", work.length],
            ["attention", "Needs you", needs],
            ["done", "Done", work.filter((w) => w.status === "done").length],
        ]
            .map(
                ([key, name, count]) =>
                    '<button class="filter ' +
                    (filter === key ? "active" : "") +
                    '" aria-pressed="' +
                    (filter === key) +
                    '" data-action="filter" data-value="' +
                    key +
                    '">' +
                    name +
                    "<span>" +
                    count +
                    "</span></button>",
            )
            .join("") +
        '</div><div class="list-scroll">' +
        sections
            .map(([title, items]) =>
                items.length
                    ? '<div class="list-section">' +
                      title +
                      "</div>" +
                      items
                          .map(
                              (w) =>
                                  '<button class="work-card ' +
                                  (selectedId === w.id ? "selected" : "") +
                                  '" data-action="select-work" data-id="' +
                                  w.id +
                                  '" aria-pressed="' +
                                  (selectedId === w.id) +
                                  '"><div class="card-top">' +
                                  label(w) +
                                  "<time>" +
                                  w.updated +
                                  "</time></div><h3>" +
                                  escapeHtml(w.title) +
                                  "</h3><p>" +
                                  escapeHtml(w.next) +
                                  '</p><div class="work-card-foot"><img src="/assets/branding/icon.svg" alt="">Goblin<span class="card-work-id">' +
                                  w.id +
                                  "</span></div></button>",
                          )
                          .join("")
                    : "",
            )
            .join("") +
        (sections.every((s) => !s[1].length)
            ? '<div class="empty-state">' +
              icon("circleCheck") +
              "<h3>All clear</h3><p>No work in this view right now.</p></div>"
            : "") +
        '</div><div class="list-bottom">' +
        icon("work") +
        "Conversations can become work. Just ask.</div></section>"
    );
}
function renderDetail() {
    const w = current();
    if (!w) return "";
    return (
        '<header class="detail-heading"><div class="detail-meta"><div class="detail-meta-left"><button class="mobile-back" data-action="back">' +
        icon("back") +
        'Work</button><span class="desktop-crumb">Work</span>' +
        icon("chevron") +
        "<span>" +
        w.id +
        '</span></div><span class="saved">' +
        icon("check") +
        'All caught up</span></div><div class="title-row"><h2>' +
        escapeHtml(w.title) +
        "</h2>" +
        label(w, true) +
        '</div><p class="work-objective">' +
        escapeHtml(w.objective) +
        '</p><div class="detail-properties"><span class="assignee"><img src="/assets/branding/icon.svg" alt="">Assigned to Goblin</span><span class="dot-divider">·</span><span>Tracked ' +
        w.created.toLowerCase() +
        '</span></div><div class="tabs" role="tablist" aria-label="Work detail views">' +
        [
            ["conversation", "chat", "Conversation"],
            ["activity", "activity", "Activity"],
            ["outputs", "file", "Outputs"],
        ]
            .map(
                ([key, ico, name]) =>
                    '<button id="tab-' +
                    key +
                    '" class="tab ' +
                    (tab === key ? "active" : "") +
                    '" role="tab" aria-selected="' +
                    (tab === key) +
                    '" aria-controls="detail-content" tabindex="' +
                    (tab === key ? "0" : "-1") +
                    '" data-action="tab" data-value="' +
                    key +
                    '">' +
                    icon(ico) +
                    name +
                    (key === "outputs"
                        ? "<span>" +
                          (["review", "done"].includes(w.status) ? 1 : 0) +
                          "</span>"
                        : "") +
                    "</button>",
            )
            .join("") +
        '</div></header><div class="detail-body" id="detail-content" role="tabpanel" aria-labelledby="tab-' +
        tab +
        '">' +
        (tab === "conversation"
            ? renderConversation(w)
            : tab === "activity"
              ? renderActivity(w)
              : renderOutputs(w)) +
        "</div>" +
        (tab === "conversation"
            ? composer(
                  "work",
                  requestChangesFor === w.id
                      ? "What would you like Goblin to change?"
                      : "Reply to Goblin or add more context…",
              )
            : "")
    );
}
function renderConversation(w: WorkItem) {
    let html =
        '<div class="day-divider">' +
        w.created +
        "</div>" +
        message(
            "You",
            "<p>" + escapeHtml(w.request) + "</p>",
            w.created === "Just now" ? "Just now" : "2:20 PM",
        ) +
        '<div class="system-event">' +
        icon("work") +
        "Goblin picked up this work</div>";
    html += message(
        "Goblin",
        "<p>" +
            escapeHtml(w.summary) +
            "</p>" +
            (w.status === "waiting"
                ? decision()
                : w.status === "working"
                  ? progress()
                  : ""),
        "Today",
    );
    if (w.messages)
        html += w.messages
            .map((m) => message(m.author, "<p>" + escapeHtml(m.text) + "</p>"))
            .join("");
    if (w.status === "review")
        html += message(
            "Goblin",
            "<p>" +
                (w.kind === "setup"
                    ? "The setup proposal is ready. It keeps the first run short and lets users change providers later."
                    : "Here’s the result. Take a look and tell me if anything needs adjusting.") +
                "</p>" +
                outputCard(w, false) +
                '<div class="review-actions"><button class="primary" data-action="approve">' +
                icon("check") +
                'Approve & complete</button><button class="secondary" data-action="changes">' +
                icon("chat") +
                "Ask for changes</button></div>",
            "Just now",
        );
    if (w.status === "done")
        html +=
            '<div class="completed-banner">' +
            icon("circleCheck") +
            '<div><strong class="completed-label">Work completed.</strong> The result and our conversation stay together.</div></div><button class="tracked-link" data-action="tab" data-value="outputs">' +
            icon("file") +
            "View the final result" +
            icon("arrow") +
            "</button>";
    return html;
}
function decision() {
    return (
        '<div class="decision"><div class="decision-eyebrow">' +
        icon("wait") +
        'A quick decision</div><h3>How should the first-run setup work?</h3><p>I’d suggest a guided setup. It keeps the first run approachable.</p><div class="options" role="group" aria-label="First-run setup approach"><button class="option ' +
        (choice === "guided" ? "selected" : "") +
        '" data-action="choice" data-value="guided" aria-pressed="' +
        (choice === "guided") +
        '"><span class="radio"></span><span><strong>Guided setup <em class="recommended">Suggested</em></strong><small>A few prompts, sensible defaults.</small></span></button><button class="option ' +
        (choice === "terminal" ? "selected" : "") +
        '" data-action="choice" data-value="terminal" aria-pressed="' +
        (choice === "terminal") +
        '"><span class="radio"></span><span><strong>Keep it in the terminal</strong><small>One command and a clear guide.</small></span></button></div><div class="decision-footer"><span>Your answer stays with this work.</span><button class="primary" data-action="continue">Continue with this' +
        icon("arrow") +
        "</button></div></div>"
    );
}
function progress() {
    return (
        '<div class="progress-panel"><div class="progress-title"><span>Goblin is working on it</span>' +
        icon("activity", "pulse") +
        '</div><div class="progress-steps"><div class="progress-step">' +
        icon("circleCheck") +
        'Understand the request and context</div><div class="progress-step current">' +
        icon("activity", "pulse") +
        'Prepare the result</div><div class="progress-step">' +
        icon("clock") +
        'Bring it back for your review</div></div><div class="progress-bar"><span></span></div><div class="outline-note">' +
        icon("spark") +
        '<button data-action="preview-result" class="preview-result-button">Preview a finished result →</button></div></div>'
    );
}
function outputCard(w: WorkItem, expanded: boolean) {
    return (
        '<article class="output-card"><div class="output-card-header"><div class="file-icon">' +
        icon("file") +
        "</div><div><h4>" +
        escapeHtml(w.outputTitle) +
        "</h4><small>" +
        escapeHtml(w.outputType) +
        " · Prepared by Goblin</small></div></div>" +
        (expanded
            ? '<div class="output-content"><h5>' +
              escapeHtml(w.outcome) +
              "</h5><ul>" +
              w.steps.map((s) => "<li>" + escapeHtml(s) + "</li>").join("") +
              '</ul><p class="output-success">Success looks like: ' +
              escapeHtml(w.objective) +
              "</p></div>"
            : '<div class="output-card-actions"><button class="secondary" data-action="tab" data-value="outputs">' +
              icon("file") +
              "Read the result" +
              icon("arrow") +
              "</button></div>") +
        "</article>"
    );
}
function renderActivity(w: WorkItem) {
    return (
        '<h3 class="activity-title">The story of this work</h3><p class="activity-caption">Progress, decisions, and handoffs, all in one place.</p><div class="timeline">' +
        w.events
            .map(
                (e, i) =>
                    '<div class="timeline-item"><div class="timeline-icon">' +
                    icon(
                        i === 0
                            ? "plus"
                            : i === w.events.length - 1
                              ? statusMeta[w.status].icon
                              : "check",
                    ) +
                    "</div><h4>" +
                    escapeHtml(e[0]) +
                    "</h4><p>" +
                    escapeHtml(e[1]) +
                    "</p><time>" +
                    escapeHtml(e[2]) +
                    "</time></div>",
            )
            .join("") +
        "</div>"
    );
}
function renderOutputs(w: WorkItem) {
    if (!["review", "done"].includes(w.status))
        return (
            '<div class="empty-state">' +
            icon("file") +
            '<h3>The result will land here</h3><p>Goblin will bring back something you can read, review, and keep with this work.</p><button class="secondary" data-action="tab" data-value="conversation">Back to the conversation</button></div>'
        );
    return (
        '<h3 class="output-title">' +
        (w.status === "done" ? "The final result" : "Ready for your review") +
        '</h3><p class="activity-caption">' +
        (w.status === "done"
            ? "Approved by you. Kept with the work."
            : "Take a look. You have the final say.") +
        "</p>" +
        outputCard(w, true) +
        (w.status === "review"
            ? '<div class="review-actions"><button class="primary" data-action="approve">' +
              icon("check") +
              'Approve & complete</button><button class="secondary" data-action="changes">' +
              icon("chat") +
              "Ask for changes</button></div>"
            : '<div class="completed-banner">' +
              icon("circleCheck") +
              "Approved and completed</div>")
    );
}
function composer(type: "work" | "chat", placeholder: string) {
    return (
        '<div class="' +
        (type === "chat" ? "chat-composer" : "composer-wrap") +
        '"><div><form class="composer" data-form="' +
        type +
        '"><label for="reply" class="sr-only">' +
        placeholder +
        '</label><textarea id="reply" name="reply" rows="2" placeholder="' +
        placeholder +
        '" maxlength="2000" required></textarea><div class="composer-footer"><span class="composer-note">' +
        icon(type === "work" ? "link" : "chat") +
        (type === "work"
            ? "Replies stay with this work"
            : "Just a conversation, until you want to track it") +
        '</span><button type="submit" class="send" aria-label="Send message">' +
        icon("up") +
        '</button></div></form><div class="composer-hint">' +
        icon("spark") +
        (type === "work"
            ? "You can leave and pick up right where you stopped."
            : "Ask a question. Think out loud. Or give Goblin something to do.") +
        "</div></div></div>"
    );
}
function renderChat() {
    const chat = currentChat();
    return (
        '<div class="workspace"><section class="work-list conversation-rail" aria-label="Conversations"><div class="list-heading"><div><h1>Conversations</h1><p>A little room to think</p></div><button class="icon-button" data-action="new-chat" aria-label="New conversation">' +
        icon("plus") +
        '</button></div><div class="list-scroll">' +
        (chats.length
            ? chats
                  .map(
                      (c) =>
                          '<button class="conversation-list-button ' +
                          (c.id === activeChatId ? "selected" : "") +
                          '" data-action="select-chat" data-id="' +
                          c.id +
                          '">' +
                          escapeHtml(c.title) +
                          "<small>" +
                          (c.workId ? "Tracked as " + c.workId : "Just now") +
                          "</small></button>",
                  )
                  .join("")
            : '<div class="empty-state">' +
              icon("chat") +
              "<p>Your conversations will appear here.</p></div>") +
        '</div></section><section class="chat-workspace" aria-label="Conversation"><header class="chat-top"><div class="chat-top-title">' +
        icon("chat") +
        (chat ? "Conversation" : "New conversation") +
        '</div><span>With Goblin</span></header><div class="chat-scroll"><div class="chat-inner">' +
        (!chat
            ? '<div class="chat-empty"><div class="goblin-portrait"><img src="/assets/branding/icon.svg" alt="Goblin"></div><h1>What’s on your mind,<br>Jesse?</h1><p>Start with a conversation. We can take it from there.</p><div class="suggestions"><button class="suggestion" data-action="suggest" data-value="Help me plan the next Goblin release.">' +
              icon("branch") +
              'Plan the next release</button><button class="suggestion" data-action="suggest" data-value="Help me write a getting-started guide for Goblin.">' +
              icon("book") +
              'Write a getting-started guide</button><button class="suggestion" data-action="suggest" data-value="I have an idea for Goblin that I want to think through.">' +
              icon("spark") +
              "Think through an idea</button></div></div>"
            : chat.messages
                  .map((m) =>
                      message(m.author, "<p>" + escapeHtml(m.text) + "</p>"),
                  )
                  .join("") +
              (chat.workId
                  ? '<button class="tracked-link" data-action="select-work" data-id="' +
                    chat.workId +
                    '">' +
                    icon("work") +
                    "Tracked as " +
                    chat.workId +
                    " · Open the work" +
                    icon("arrow") +
                    "</button>"
                  : '<div class="track-offer"><h3>Want me to keep track of this?</h3><p>I’ll give it a place in Work, keep the context, and bring back progress and anything that needs you.</p><button class="primary" data-action="track">' +
                    icon("work") +
                    "Track this work</button></div>")) +
        "</div></div>" +
        composer("chat", "Tell Goblin what you have in mind…") +
        "</section></div>"
    );
}
function openWork(id: string) {
    if (!work.some((w) => w.id === id)) return false;
    selectedId = id;
    view = "work";
    tab = "conversation";
    detailOpen = true;
    render();
    return true;
}
function addEvent(w: WorkItem, title: string, body: string) {
    w.events.push([title, body, "Just now"]);
    w.updated = "Now";
}
function startWork(w: WorkItem, reply: string) {
    w.status = "working";
    w.next = "Preparing the result for your review.";
    w.messages ??= [];
    w.messages.push({ author: "You", text: reply });
    w.messages.push({
        author: "Goblin",
        text: "Got it. I’ve kept your decision with this work. I’ll use it to prepare the next result.",
    });
    addEvent(w, "You gave Goblin direction", reply);
    requestChangesFor = null;
    tab = "conversation";
    render();
    toast("Preview: Goblin continues with your input.");
}
function finishPreview(w: WorkItem) {
    if (w.status !== "working") return false;
    w.status = "review";
    w.next = "The result is ready for your review.";
    addEvent(
        w,
        "Ready for review",
        "Goblin brought the result back for your review.",
    );
    tab = "conversation";
    render();
    toast("Preview: the result is ready to review.");
    return true;
}
function approveWork(w: WorkItem) {
    if (w.status !== "review") return false;
    w.status = "done";
    w.next = "Approved. The result stays with this work.";
    addEvent(
        w,
        "You approved the result",
        "Work completed. The result and conversation remain together.",
    );
    requestChangesFor = null;
    render();
    toast("Work completed. Everything stays together.");
    return true;
}
function trackChat() {
    const c = currentChat();
    if (!c || c.workId) return null;
    const id = "GB-" + String(sequence++).padStart(3, "0");
    const first = c.messages.find((m) => m.author === "You")?.text;
    if (!first) return null;
    const title = first
        .replace(/[.!?]$/, "")
        .replace(/^(please |can you |help me )/i, "");
    const displayTitle = title.charAt(0).toUpperCase() + title.slice(1);
    const w: WorkItem = {
        id,
        title:
            displayTitle.length > 68
                ? displayTitle.slice(0, 65) + "…"
                : displayTitle,
        objective: first,
        status: "working",
        updated: "Now",
        created: "Just now",
        request: first,
        summary:
            "I’ve picked this up. I’ll keep the context here and bring back a first result for you to review.",
        next: "Understanding the request and preparing a first result.",
        kind: "custom",
        outputTitle: "A first plan for your request",
        outputType: "Working proposal",
        outcome: "Start with a small, reviewable first version.",
        steps: [
            "Confirm the intended outcome: " + first,
            "Gather the relevant context and note any assumptions.",
            "Prepare a first version and bring open decisions back to you.",
        ],
        events: [
            [
                "Work created",
                "You chose to track this conversation.",
                "Just now",
            ],
            [
                "Goblin picked up the work",
                "The request and conversation are attached.",
                "Just now",
            ],
        ],
        messages: c.messages.slice(1),
    };
    work.unshift(w);
    c.workId = id;
    openWork(id);
    toast("Tracked as " + id + ". Goblin has picked it up.");
    return id;
}
function sendChat(text: string) {
    let c = currentChat();
    if (!c) {
        c = {
            id: "chat-" + Date.now(),
            title: text.length > 40 ? text.slice(0, 37) + "…" : text,
            messages: [],
        };
        chats.unshift(c);
        activeChatId = c.id;
    }
    c.messages.push({ author: "You", text });
    c.messages.push({
        author: "Goblin",
        text: c.workId
            ? "I’ve added that context to our conversation and the tracked work."
            : "Let’s start by getting the outcome clear, then work through the smallest useful next step. We can keep thinking here, or track this if you want me to carry it forward.",
    });
    if (c.workId) {
        const w = work.find((w) => w.id === c.workId);
        if (!w) throw new Error("The tracked work is missing.");
        w.messages ??= [];
        w.messages.push({ author: "You", text });
        addEvent(w, "More context added", text);
    }
    render();
    $(".chat-scroll").scrollTop = $(".chat-scroll").scrollHeight;
}
document.addEventListener("click", (e) => {
    if (!(e.target instanceof Element)) return;
    const b = e.target.closest<HTMLElement>("[data-action]");
    if (!b) return;
    const action = b.dataset.action;
    const w = current();
    if (action === "reset") {
        initialize();
        render();
        toast("The example workspace is reset.");
    }
    if (action === "new-chat") {
        view = "chat";
        activeChatId = null;
        render();
        $("#reply")?.focus();
    }
    if (action === "view-chat") {
        view = "chat";
        render();
    }
    if (action === "view-work") {
        view = "work";
        detailOpen = false;
        render();
    }
    if (action === "mobile-nav") {
        if (view === "chat") {
            view = "work";
            detailOpen = false;
        } else {
            view = "chat";
            activeChatId = null;
        }
        render();
    }
    if (action === "back") {
        detailOpen = false;
        render();
    }
    if (action === "select-work") openWork(b.dataset.id ?? "");
    if (action === "select-chat") {
        activeChatId = b.dataset.id ?? null;
        view = "chat";
        render();
    }
    if (action === "filter") {
        filter = b.dataset.value as WorkFilter;
        render();
    }
    if (action === "tab") {
        tab = b.dataset.value as DetailTab;
        render();
    }
    if (action === "choice") {
        choice = b.dataset.value as typeof choice;
        render();
    }
    if (action === "continue") {
        const answer =
            choice === "guided"
                ? "Let’s use a guided setup. A few prompts and sensible defaults."
                : "Let’s keep it in the terminal. One command and a clear guide.";
        if (choice === "terminal") {
            w.outputTitle = "A simpler terminal setup";
            w.outcome =
                "One setup command, clear prompts, and a short getting-started guide.";
            w.steps = [
                "Run a single setup command from the checkout.",
                "Enter the provider and workspace settings in the terminal.",
                "Use the generated configuration to start Goblin.",
            ];
        }
        startWork(w, answer);
    }
    if (action === "preview-result") finishPreview(w);
    if (action === "approve") approveWork(w);
    if (action === "changes") {
        requestChangesFor = w.id;
        tab = "conversation";
        render();
        $("#reply").focus();
        toast("Tell Goblin what you’d like to change.");
    }
    if (action === "suggest") {
        $<HTMLTextAreaElement>("#reply").value = b.dataset.value ?? "";
        $("#reply").focus();
    }
    if (action === "track") trackChat();
});
document.addEventListener("submit", (e) => {
    if (!(e.target instanceof HTMLFormElement) || !e.target.dataset.form)
        return;
    const form = e.target;
    e.preventDefault();
    const input = form.elements.namedItem("reply");
    if (!(input instanceof HTMLTextAreaElement)) return;
    const value = input.value.trim();
    if (!value) return;
    if (form.dataset.form === "chat") {
        sendChat(value);
        return;
    }
    const w = current();
    if (w.status === "waiting" || requestChangesFor === w.id) {
        startWork(w, value);
        return;
    }
    w.messages ??= [];
    w.messages.push({ author: "You", text: value });
    w.messages.push({
        author: "Goblin",
        text:
            w.status === "done"
                ? "Added to the conversation. The completed result is still here if you need it."
                : "I’ve added that to the work’s context. You can find it here when you come back.",
    });
    addEvent(w, "Context added", value);
    render();
    const body = $(".detail-body");
    body.scrollTop = body.scrollHeight;
});
document.addEventListener("keydown", (e) => {
    if (
        e.target instanceof HTMLTextAreaElement &&
        e.key === "Enter" &&
        !e.shiftKey &&
        !e.isComposing
    ) {
        e.preventDefault();
        e.target.form?.requestSubmit();
    }
    if (
        e.target instanceof HTMLElement &&
        e.target.matches('[role="tab"]') &&
        ["ArrowLeft", "ArrowRight", "Home", "End"].includes(e.key)
    ) {
        e.preventDefault();
        const list: DetailTab[] = ["conversation", "activity", "outputs"];
        let index = list.indexOf(tab);
        index =
            e.key === "Home"
                ? 0
                : e.key === "End"
                  ? 2
                  : (index + (e.key === "ArrowRight" ? 1 : 2)) % 3;
        tab = list[index];
        render();
        $("#tab-" + tab).focus();
    }
});
render();
