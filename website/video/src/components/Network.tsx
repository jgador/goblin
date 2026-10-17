import type { CSSProperties, ReactNode } from "react";
import { Img, staticFile, useCurrentFrame } from "remotion";
import { evolvePath, getLength, getPointAtLength } from "@remotion/paths";
import { mix, pop, progress, scaleAbsolutePath } from "../anim";
import {
    channels,
    featuredScene,
    type Channel,
    type ChannelId,
} from "../content";
import { fontFamily } from "../fonts";
import { at } from "../timeline";
import { Icon, paths } from "./Icons";

// The website's network box in stage coordinates.
export const network = { x: 54, y: 300, width: 1046, height: 556 } as const;

const viewBox = { width: 1100, height: 700 } as const;
const scaleX = network.width / viewBox.width;
const scaleY = network.height / viewBox.height;

const route = (path: string) => scaleAbsolutePath(path, scaleX, scaleY);
const point = (x: number, y: number) => ({ x: x * scaleX, y: y * scaleY });

// Connector paths from website/index.html, scaled from the 1100 × 700 view box.
const lines: Record<ChannelId, string> = {
    slack: route(
        "M330 68H340Q365 68 365 96V101Q365 126 394 126H464Q535 126 535 200",
    ),
    teams: route(
        "M800 68H790Q765 68 765 96V101Q765 126 736 126H691Q620 126 620 200",
    ),
    github: route("M320 355H382Q420 355 420 325V302Q420 272 450 272H500"),
    email: route("M815 355H750Q716 355 716 325V302Q716 272 686 272H653"),
    discord: route("M575 504V420"),
};

const returnLines = [
    { d: route("M515 206V174Q515 151 487 151H389"), color: "#b9dabe" },
    { d: route("M640 206V174Q640 151 668 151H743"), color: "#b9dabe" },
    {
        d: route("M500 288H464Q438 288 438 318V351Q438 386 401 386H334"),
        color: "#cba9dd",
    },
    {
        d: route("M653 288H675Q704 288 704 318V351Q704 386 741 386H804"),
        color: "#cba9dd",
    },
    { d: route("M543 438V490"), color: "#b9dabe" },
    { d: route("M607 438V490"), color: "#b9dabe" },
];

const dots: Record<ChannelId, { x: number; y: number; color: string }> = {
    slack: { ...point(535, 200), color: "#89c298" },
    teams: { ...point(620, 200), color: "#89c298" },
    github: { ...point(500, 272), color: "#82aef4" },
    email: { ...point(653, 272), color: "#82aef4" },
    discord: { ...point(575, 420), color: "#75b943" },
};

const lineColor: Record<ChannelId, string> = {
    slack: "#9fcaa0",
    teams: "#9fcaa0",
    github: "#93baff",
    email: "#93baff",
    discord: "#9fcaa0",
};

const hubCenterX = network.width * 0.5227;
const hubTop = network.height * 0.308;
const markSize = 130;
const markHeight = markSize * 1.03;
const hubNameHeight = 39;
// Like the website, shrink the hub so its wordmark stays 18px above the Discord connector.
const hubScale = Math.min(
    1,
    (dots.discord.y - 5 - hubTop - 18) / (markHeight + 12 + hubNameHeight),
);
const markCenterY = hubTop + (markHeight / 2) * hubScale;

type Box = { left: number; top: number; width: number; height: number };

const percentBox = (
    left: number,
    top: number,
    width: number,
    height: number,
): Box => ({
    left: (left / 100) * network.width,
    top: (top / 100) * network.height,
    width: (width / 100) * network.width,
    height: (height / 100) * network.height,
});

const cardBoxes: Record<ChannelId, Box> = {
    slack: percentBox(1.5, 0, 29.5, 34.4),
    teams: percentBox(100 - 0.7 - 27, 0, 27, 34.4),
    github: percentBox(1.5, 40.3, 28.8, 35.8),
    email: percentBox(100 - 0.7 - 25.7, 40.3, 25.7, 35.8),
    discord: {
        ...percentBox(0, 72, 24.4, 27.5),
        left: hubCenterX - (24.4 / 100) * network.width * 0.5,
    },
};

type Travel = {
    channel: ChannelId;
    start: number;
    end: number;
    inbound: boolean;
    label?: string;
};

const featured: ChannelId = featuredScene.channel;
const montageChannels = channels
    .map((channel) => channel.id)
    .filter((id) => id !== featured);

const montageStart = (index: number) => at.montage + index * at.montageSpacing;

const travels: Travel[] = [
    {
        channel: featured,
        start: at.requestTravel[0],
        end: at.requestTravel[1],
        inbound: true,
        label: "Request",
    },
    {
        channel: featured,
        start: at.responseTravel[0],
        end: at.responseTravel[1],
        inbound: false,
        label: "Response",
    },
    ...montageChannels.flatMap((channel, index): Travel[] => [
        {
            channel,
            start: montageStart(index),
            end: montageStart(index) + 16,
            inbound: true,
        },
        {
            channel,
            start: montageStart(index) + 18,
            end: montageStart(index) + 30,
            inbound: false,
        },
    ]),
];

// Fades the other channels back while the featured conversation plays.
const spotlightAt = (frame: number): number =>
    progress(frame, at.request - 10, at.request + 8) *
    (1 - progress(frame, at.zoomOut[0] + 12, at.zoomOut[1]));

const activeChannel = (frame: number): ChannelId | null => {
    if (frame < at.request) return null;
    if (frame < at.montage) return featured;
    const index = Math.floor((frame - at.montage) / at.montageSpacing);
    return montageChannels[Math.min(index, montageChannels.length - 1)] ?? null;
};

const Skeleton = ({
    width,
    height = 9,
    color = "#d6dce8",
    opacity = 1,
}: {
    width: string;
    height?: number;
    color?: string;
    opacity?: number;
}) => (
    <span
        style={{
            display: "block",
            width,
            height,
            background: color,
            borderRadius: 10,
            opacity,
        }}
    />
);

const TypingDots = ({ frame, active }: { frame: number; active: boolean }) => (
    <span style={{ display: "flex", gap: 5, marginTop: 3 }}>
        {[0, 1, 2].map((index) => {
            const wave = active
                ? (Math.sin((frame - index * 5) / 4) + 1) / 2
                : 0;
            return (
                <i
                    key={index}
                    style={{
                        display: "block",
                        width: 4,
                        height: 4,
                        borderRadius: "50%",
                        background:
                            mix(0, 1, wave) > 0.5 ? "#66ad59" : "#b0d1b2",
                        transform: `translateY(${-2 * wave}px)`,
                    }}
                />
            );
        })}
    </span>
);

const GoblinMark = ({
    size,
    dark,
    style,
}: {
    size: number;
    dark?: boolean;
    style?: CSSProperties;
}) => (
    <span
        style={{
            display: "inline-flex",
            alignItems: "center",
            justifyContent: "center",
            width: size,
            height: size * 1.03,
            flexShrink: 0,
            borderRadius: dark ? 10 : 0,
            background: dark
                ? "linear-gradient(140deg, #173d32, #205b3d 60%, #163c2d)"
                : "transparent",
            ...style,
        }}
    >
        <Img
            src={staticFile(dark ? "goblin-dark.svg" : "goblin.svg")}
            style={{ width: dark ? "70%" : "78%", height: "auto" }}
        />
    </span>
);

const ChatPreview = ({
    channel,
    frame,
    active,
    compact,
}: {
    channel: Channel;
    frame: number;
    active: boolean;
    compact: boolean;
}) => {
    const avatar = compact ? 34 : 39;
    return (
        <span
            style={{
                display: "flex",
                flexDirection: "column",
                gap: compact ? 9 : 10,
            }}
        >
            <span
                style={{
                    display: "flex",
                    alignItems: "flex-start",
                    gap: compact ? 11 : 13,
                }}
            >
                <span
                    style={{
                        display: "flex",
                        alignItems: "center",
                        justifyContent: "center",
                        flexShrink: 0,
                        width: avatar,
                        height: avatar,
                        background: "linear-gradient(145deg, #e9edf6, #f0f1f7)",
                        borderRadius: "50%",
                        color: "#536078",
                        fontSize: compact ? 14 : 16,
                    }}
                >
                    {channel.initials}
                </span>
                <span
                    style={{
                        display: "flex",
                        flexDirection: "column",
                        justifyContent: "center",
                        gap: compact ? 6 : 7,
                        width: "66%",
                        minHeight: compact ? 39 : 47,
                        padding: compact ? "10px 13px" : "12px 15px",
                        background: "#f0f3ff",
                        borderRadius: "0 12px 12px",
                    }}
                >
                    <Skeleton width="83%" height={compact ? 7 : 9} />
                    <Skeleton
                        width="45%"
                        height={compact ? 7 : 9}
                        opacity={0.7}
                    />
                </span>
            </span>
            <span
                style={{
                    display: "flex",
                    alignItems: "flex-start",
                    gap: 12,
                    marginLeft: compact ? 29 : 30,
                }}
            >
                <GoblinMark
                    size={compact ? 30 : 34}
                    dark={compact}
                    style={{ marginTop: 4, opacity: active ? 0.9 : 0.45 }}
                />
                <span
                    style={{
                        display: "flex",
                        flexDirection: "column",
                        justifyContent: "center",
                        gap: compact ? 6 : 7,
                        width: "85%",
                        minHeight: compact ? 39 : 47,
                        padding: compact ? "10px 13px" : "12px 16px",
                        background: "#f0f7ef",
                        borderRadius: "0 12px 12px",
                    }}
                >
                    <Skeleton
                        width="83%"
                        height={compact ? 7 : 9}
                        color="#d9e7d8"
                    />
                    <TypingDots frame={frame} active={active} />
                </span>
            </span>
        </span>
    );
};

const RepoPreview = () => (
    <span style={{ display: "flex", flexDirection: "column" }}>
        <span
            style={{
                display: "flex",
                alignItems: "center",
                justifyContent: "space-between",
                padding: "5px 0 6px",
            }}
        >
            <span
                style={{
                    width: "74%",
                    display: "flex",
                    flexDirection: "column",
                    gap: 11,
                }}
            >
                <Skeleton width="47%" color="#dfe2e6" />
                <Skeleton width="70%" color="#e9ebef" />
            </span>
            <span
                style={{
                    border: "1px solid #e9edf0",
                    borderRadius: 11,
                    padding: 8,
                    color: "#9ba5b7",
                }}
            >
                <Icon size={17}>{paths.lock}</Icon>
            </span>
        </span>
        <span
            style={{
                display: "flex",
                justifyContent: "space-between",
                gap: 9,
                marginTop: 7,
            }}
        >
            {[
                paths.code,
                paths.issue,
                paths.pullRequest,
                paths.actions,
                paths.projects,
            ].map((icon, index) => (
                <span
                    key={index}
                    style={{
                        display: "grid",
                        placeItems: "center",
                        width: "18%",
                        height: 47,
                        borderRadius: 12,
                        background: index === 0 ? "#f0f1ff" : "#f2f8ef",
                        border:
                            index === 0
                                ? "1px solid #dcdeff"
                                : "1px solid transparent",
                        color: index === 0 ? "#6266ff" : "#16532a",
                    }}
                >
                    <Icon size={22}>{icon}</Icon>
                </span>
            ))}
        </span>
    </span>
);

const MailPreview = ({
    initials,
    unread,
}: {
    initials: string;
    unread: number;
}) => (
    <span style={{ display: "flex", flexDirection: "column", gap: 10 }}>
        {["11:24 AM", "9:08 AM"].map((time, index) => (
            <span
                key={time}
                style={{
                    margin: "0 -8px",
                    padding: "0 8px",
                    borderRadius: 10,
                    background:
                        index === 0
                            ? `rgba(234, 245, 231, ${unread})`
                            : "transparent",
                    display: "flex",
                    alignItems: "center",
                    gap: 12,
                    minHeight: 34,
                    color: "#9aa4b4",
                }}
            >
                <span
                    style={{
                        display: "flex",
                        alignItems: "center",
                        justifyContent: "center",
                        width: 31,
                        height: 31,
                        borderRadius: "50%",
                        background: "#eef0f2",
                        color: "#536078",
                        fontSize: 14,
                        flexShrink: 0,
                    }}
                >
                    {initials}
                </span>
                <Icon size={16}>{paths.mail}</Icon>
                <Skeleton width="36%" color="#d7dce2" />
                <span
                    style={{
                        marginLeft: "auto",
                        fontSize: 11,
                        color: "#8b96a8",
                        whiteSpace: "nowrap",
                    }}
                >
                    {time}
                </span>
            </span>
        ))}
        <span
            style={{
                display: "flex",
                alignItems: "center",
                gap: 13,
                marginLeft: 40,
                background: "#f0f7ef",
                borderRadius: 11,
                padding: 12,
                color: "#467a52",
            }}
        >
            <Icon size={16}>{paths.reply}</Icon>
            <Skeleton width="74%" color="#d9e7d8" />
        </span>
    </span>
);

const ChannelCard = ({
    channel,
    index,
    frame,
    active,
    spotlight,
}: {
    channel: Channel;
    index: number;
    frame: number;
    active: boolean;
    spotlight: number;
}) => {
    const box = cardBoxes[channel.id];
    const appear = pop(frame, at.cards + index * 5);
    const isFeatured = channel.id === featured;
    const emphasis = isFeatured ? spotlight : 0;
    const visibility = isFeatured ? 1 : mix(1, 0.42, spotlight);
    const compact = channel.id === "discord";
    const glow = active ? 1 : 0;
    let body: ReactNode;
    if (channel.id === "github") body = <RepoPreview />;
    else if (channel.id === "email")
        body = (
            <MailPreview initials={channel.initials} unread={active ? 1 : 0} />
        );
    else
        body = (
            <ChatPreview
                channel={channel}
                frame={frame}
                active={active}
                compact={compact}
            />
        );
    return (
        <div
            style={{
                position: "absolute",
                ...box,
                padding: compact ? "14px 19px" : "18px 20px",
                border: `1px solid ${active ? "#9ac692" : "#e1ebdf"}`,
                borderRadius: 17,
                background: "linear-gradient(135deg, #ffffffeb, #fdfdffef)",
                boxShadow: `0 10px 35px rgba(59, 110, 57, ${mix(0.03, 0.14, Math.max(glow, emphasis))})`,
                opacity: Math.min(appear, visibility),
                transform: `translateY(${mix(26, 0, appear)}px) scale(${mix(0.96, 1, appear) * mix(1, 1.035, emphasis)})`,
                zIndex: isFeatured ? 1 : 0,
                overflow: "hidden",
            }}
        >
            <span
                style={{
                    display: "flex",
                    alignItems: "flex-start",
                    gap: 8,
                    height: compact ? "29%" : "32%",
                    minHeight: compact ? 35 : 43,
                    borderBottom: "1px solid #e8ebed",
                    marginBottom: compact ? 10 : 12,
                    paddingBottom: compact ? 9 : 12,
                }}
            >
                <Img
                    src={staticFile(`${channel.id}.svg`)}
                    style={{
                        width: compact ? 47 : 53,
                        height: compact ? 36 : 53,
                        maxHeight: "100%",
                        objectFit: "contain",
                        flexShrink: 0,
                    }}
                />
                <strong
                    style={{
                        alignSelf: "center",
                        color: "#29463c",
                        fontSize: 16,
                        lineHeight: 1.4,
                        fontWeight: 600,
                    }}
                >
                    {channel.name}
                </strong>
                <span
                    style={{
                        marginLeft: "auto",
                        color: "#a7aebe",
                        fontSize: 13,
                        letterSpacing: 2,
                        lineHeight: "25px",
                    }}
                >
                    •••
                </span>
            </span>
            {body}
        </div>
    );
};

const HostingBadge = ({ frame }: { frame: number }) => {
    const appear = pop(frame, at.hosting + 4, 12);
    if (frame < at.hosting) return null;
    return (
        <span
            style={{
                position: "absolute",
                left: markSize * 0.89 - 24,
                top: markHeight * 0.85 - 24,
                display: "grid",
                placeItems: "center",
                width: 48,
                height: 48,
                borderRadius: "50%",
                background: "#ffffff",
                border: "2px solid #3f8f4f",
                color: "#23732b",
                boxShadow: "0 8px 20px #2f6b3a33",
                opacity: Math.min(1, appear * 1.5),
                transform: `scale(${appear})`,
            }}
        >
            <Icon size={27} strokeWidth={1.9}>
                {paths.shield}
            </Icon>
        </span>
    );
};

const Hub = ({ frame }: { frame: number }) => {
    const appear = pop(frame, at.hub, 13);
    const thinking = frame >= at.thinking[0] && frame < at.thinking[1];
    const breathe = thinking
        ? (1 - Math.cos(((frame - at.thinking[0]) / 45) * Math.PI * 2)) / 2
        : 0;
    const hosting = progress(frame, at.hosting, at.hosting + 30);
    return (
        <div
            style={{
                position: "absolute",
                left: hubCenterX,
                top: hubTop,
                display: "flex",
                flexDirection: "column",
                alignItems: "center",
                transform: `translateX(-50%) scale(${mix(0.7, 1, appear) * hubScale})`,
                transformOrigin: "top center",
                opacity: Math.min(1, appear * 1.4),
            }}
        >
            <div style={{ position: "relative" }}>
                <div
                    style={{
                        position: "absolute",
                        inset: mix(-30, -46, hosting),
                        borderRadius: "50%",
                        background: "#67aa6430",
                        filter: "blur(24px)",
                        opacity:
                            mix(1, 0.45, breathe) +
                            (thinking ? 0.6 : 0) +
                            hosting * 0.7,
                        transform: `scale(${mix(1.05, 0.95, breathe)})`,
                    }}
                />
                <GoblinMark size={markSize} />
                <HostingBadge frame={frame} />
            </div>
            <span
                style={{
                    height: hubNameHeight,
                    fontSize: 32,
                    fontWeight: 700,
                    letterSpacing: -1.3,
                    marginTop: 12,
                    color: "#114f3c",
                }}
            >
                goblin
            </span>
        </div>
    );
};

const ArrivalRings = ({ frame }: { frame: number }) => (
    <>
        {travels
            .filter((travel) => travel.inbound)
            .map((travel, index) => {
                const amount = progress(frame, travel.end, travel.end + 22);
                if (amount <= 0 || amount >= 1) return null;
                const size = mix(100, 220, amount);
                return (
                    <div
                        key={index}
                        style={{
                            position: "absolute",
                            left: hubCenterX - size / 2,
                            top: markCenterY - size / 2,
                            width: size,
                            height: size,
                            borderRadius: "50%",
                            border: "2px solid #66ad59",
                            opacity: mix(0.5, 0, amount),
                        }}
                    />
                );
            })}
    </>
);

const PacketLayer = ({ frame }: { frame: number }) => (
    <>
        {travels.map((travel, index) => {
            if (frame < travel.start || frame > travel.end) return null;
            const raw = progress(
                frame,
                travel.start,
                travel.end,
                (input) => input,
            );
            const eased = raw * raw * (3 - 2 * raw);
            const path = lines[travel.channel];
            const distance =
                (travel.inbound ? eased : 1 - eased) * getLength(path);
            const position = getPointAtLength(path, distance);
            if (!position) return null;
            return (
                <div key={index}>
                    <div
                        style={{
                            position: "absolute",
                            left: position.x - 15,
                            top: position.y - 15,
                            width: 30,
                            height: 30,
                            borderRadius: "50%",
                            background: "#75b86d40",
                        }}
                    />
                    <div
                        style={{
                            position: "absolute",
                            left: position.x - 6,
                            top: position.y - 6,
                            width: 12,
                            height: 12,
                            borderRadius: "50%",
                            background: "#479c52",
                            boxShadow: "0 0 12px #479c52aa",
                        }}
                    />
                    {travel.label ? (
                        <div
                            style={{
                                position: "absolute",
                                left: position.x,
                                top: position.y,
                                transform: "translate(-50%, -150%)",
                                border: "1px solid #d4e4d0",
                                borderRadius: 8,
                                background: "#fffffff2",
                                padding: "5px 10px",
                                fontSize: 14,
                                fontWeight: 600,
                                color: "#2f5a43",
                                whiteSpace: "nowrap",
                                opacity: Math.min(1, raw * 6, (1 - raw) * 6),
                            }}
                        >
                            {travel.label}
                        </div>
                    ) : null}
                </div>
            );
        })}
    </>
);

export const Network = () => {
    const frame = useCurrentFrame();
    const active = activeChannel(frame);
    const returnOpacity = progress(frame, at.lines[1] - 10, at.lines[1] + 10);
    const spotlight = spotlightAt(frame);
    return (
        <div
            style={{
                position: "absolute",
                left: network.x,
                top: network.y,
                width: network.width,
                height: network.height,
                fontFamily,
            }}
        >
            <svg
                width={network.width}
                height={network.height}
                style={{ position: "absolute", inset: 0, overflow: "visible" }}
            >
                {returnLines.map((line, index) => (
                    <path
                        key={index}
                        d={line.d}
                        fill="none"
                        stroke={line.color}
                        strokeWidth={1.25}
                        strokeDasharray="3 4"
                        opacity={returnOpacity}
                    />
                ))}
                {channels.map((channel, index) => {
                    const draw = progress(
                        frame,
                        at.lines[0] + index * 4,
                        at.lines[1] + index * 4,
                    );
                    const isActive = channel.id === active;
                    const d = lines[channel.id];
                    return (
                        <path
                            key={channel.id}
                            d={d}
                            fill="none"
                            stroke={
                                isActive ? "#66ad59" : lineColor[channel.id]
                            }
                            strokeWidth={isActive ? 2.2 : 1.3}
                            strokeLinecap="round"
                            opacity={
                                channel.id === featured
                                    ? 1
                                    : mix(1, 0.4, spotlight)
                            }
                            {...evolvePath(draw, d)}
                        />
                    );
                })}
                {channels.map((channel, index) => {
                    const dot = dots[channel.id];
                    const appear = progress(
                        frame,
                        at.lines[1] + index * 4 - 6,
                        at.lines[1] + index * 4 + 4,
                    );
                    return (
                        <circle
                            key={channel.id}
                            cx={dot.x}
                            cy={dot.y}
                            r={5 * appear}
                            fill={dot.color}
                        />
                    );
                })}
            </svg>
            {channels.map((channel, index) => (
                <ChannelCard
                    key={channel.id}
                    channel={channel}
                    index={index}
                    frame={frame}
                    active={channel.id === active}
                    spotlight={spotlight}
                />
            ))}
            <ArrivalRings frame={frame} />
            <Hub frame={frame} />
            <PacketLayer frame={frame} />
        </div>
    );
};
