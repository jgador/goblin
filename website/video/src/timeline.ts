export const fps = 30;
export const durationInFrames = 900;
export const width = 1920;
export const height = 1080;

// The stage reproduces the website's desktop layout in a 1600 × 900 design space.
export const stage = { width: 1600, height: 900, scale: width / 1600 } as const;

export const at = {
    kicker: 6,
    headline: 14,
    subheadline: 42,
    pullback: [60, 114],
    hub: 78,
    cards: 88,
    lines: [96, 140],
    panel: [110, 142],
    request: 150,
    requestTravel: [162, 192],
    thinking: [192, 236],
    responseTravel: [236, 260],
    zoomIn: [244, 292],
    reply: 262,
    checks: [298, 320, 342],
    summary: 360,
    loopSteps: 272,
    scroll: [386, 420],
    artifact: 398,
    review: 402,
    highlight: 432,
    zoomOut: [468, 516],
    montage: 520,
    montageSpacing: 30,
    toolsCaption: [524, 640],
    hosting: 644,
    hostingCaption: [652, 768],
    endCard: [764, 796],
} as const;
