// Copy mirrors website/index.html and the Slack scene in website/website.js.
// Update both when the website wording changes.

export const intro = {
    kicker: "Your self-hosted AI coworker.",
    headline: ["Turn team conversations", "into results."],
    subheadline:
        "Assign tasks, follow progress, and review the work. Goblin keeps conversations and results connected, so your team spends less time coordinating.",
} as const;

export const notice = {
    title: "Illustrative example",
    detail: "Teams, Discord, and email show planned integrations.",
} as const;

export type ChannelId = "slack" | "teams" | "github" | "email" | "discord";

export type Channel = {
    id: ChannelId;
    name: string;
    initials: string;
};

export const channels: readonly Channel[] = [
    { id: "slack", name: "Slack", initials: "JA" },
    {
        id: "teams",
        name: "Microsoft Teams",
        initials: "AH",
    },
    { id: "github", name: "GitHub", initials: "ST" },
    { id: "email", name: "Gmail", initials: "LT" },
    { id: "discord", name: "Discord", initials: "RK" },
];

// The video follows the Slack conversation; the other channels appear in a montage.
export const featuredScene = {
    channel: "slack",
    name: "Slack",
    location: "#log-pipeline",
    request:
        "Help us handle higher traffic without losing accepted logs. Prepare a tested change and a deployment plan for review.",
    steps: [
        "Understand the traffic problem",
        "Prepare and check the changes",
        "Plan the deployment",
        "Review the proposed result",
    ],
    reply: "I’ll investigate the problem, prepare the changes, and report the results here. I’ll ask when a decision needs your input.",
    artifactTitle: "Proposed changes",
    artifactDescription: "Verification summary and deployment plan",
    documentTitle: "Verification and deployment plan",
} as const satisfies { channel: ChannelId } & Record<string, unknown>;

export const loopSteps = [
    "Assign tasks.",
    "Follow progress.",
    "Review the work.",
] as const;

export const benefits = {
    tools: {
        title: "Spend less time switching tools",
        detail: "Follow conversations, progress, and results in one place.",
    },
    hosting: {
        label: "Self-hosted",
        title: "Control where Goblin runs",
        detail: "Host Goblin on infrastructure you control.",
    },
} as const;

export const endCard = {
    eyebrow: "From a conversation to a result.",
    website: "goblinboard.com",
    repository: "github.com/jgador/goblin",
    // Mirrors the website README: the Slack dialogue and its log pipeline are invented.
    footnote: [
        "Illustrative example. The Slack dialogue is fictional and does not describe an actual system or work.",
        "Teams, Discord, and email show planned integrations.",
    ],
} as const;
