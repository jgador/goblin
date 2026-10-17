import type { CSSProperties, ReactNode } from "react";

type IconProps = {
    size: number;
    color?: string;
    strokeWidth?: number;
    style?: CSSProperties;
    children: ReactNode;
};

// Stroke icons copied from website/index.html.
export const Icon = ({
    size,
    color = "currentColor",
    strokeWidth = 1.65,
    style,
    children,
}: IconProps) => (
    <svg
        viewBox="0 0 24 24"
        width={size}
        height={size}
        fill="none"
        stroke={color}
        strokeWidth={strokeWidth}
        strokeLinecap="round"
        strokeLinejoin="round"
        style={{ flexShrink: 0, display: "block", ...style }}
    >
        {children}
    </svg>
);

export const paths = {
    check: <path d="m5 13 4 4L19 7" />,
    external: <path d="M14 3h7v7m0-7L10 14M10 4H4v16h16v-6" />,
    file: (
        <>
            <path d="M5 2h10l5 5v15H5V2Z" />
            <path d="M14 2v6h6M9 12h7m-7 4h7" />
        </>
    ),
    lock: (
        <>
            <rect x="5" y="10" width="14" height="11" rx="2" />
            <path d="M8 10V6a4 4 0 0 1 8 0v4m-4 4v3" />
        </>
    ),
    code: <path d="m8 7-5 5 5 5m8-10 5 5-5 5" />,
    issue: (
        <>
            <circle cx="12" cy="12" r="9" />
            <circle cx="12" cy="12" r="1.5" fill="currentColor" stroke="none" />
        </>
    ),
    pullRequest: (
        <>
            <circle cx="6" cy="5" r="2" />
            <circle cx="6" cy="19" r="2" />
            <circle cx="18" cy="19" r="2" />
            <path d="M6 7v10m12 0V9a4 4 0 0 0-4-4h-2m3-3-3 3 3 3" />
        </>
    ),
    actions: (
        <>
            <circle cx="12" cy="12" r="9" />
            <path d="m10 8 6 4-6 4Z" />
        </>
    ),
    projects: (
        <>
            <rect x="3" y="4" width="18" height="16" rx="2" />
            <path d="M9 4v16m6-16v16M5.5 8h1m5 0h1m5 0h1" />
        </>
    ),
    mail: (
        <>
            <rect x="2" y="4" width="20" height="16" rx="2" />
            <path d="m3 5 9 8 9-8" />
        </>
    ),
    reply: <path d="m9 4-6 6 6 6M3 10h10c5 0 8 3 8 8" />,
    attach: (
        <path d="m9 16 8-8a3 3 0 0 0-4-4L4 13a5 5 0 0 0 7 7L21 10M8 13l7-7" />
    ),
    emoji: (
        <>
            <circle cx="12" cy="12" r="10" />
            <path d="M7 14c2 5 8 5 10 0M8 8v1m8-1v1" />
        </>
    ),
    send: <path d="m3 3 19 9-19 9 4-9-4-9Zm4 9h15" />,
    bolt: <path d="m13 1-10 13h8l-1 9 11-14h-8l1-8Z" />,
    shield: (
        <>
            <path d="m12 2 9 4v6c0 6-9 10-9 10S3 18 3 12V6l9-4Z" />
            <path d="m8 12 3 3 5-6" />
        </>
    ),
} as const;
