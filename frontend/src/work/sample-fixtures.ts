// Design examples only. Never loaded by the application.
export const sampleWork = [
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
