import type { CSSProperties, ReactNode } from "react";
import { Img, staticFile, useCurrentFrame } from "remotion";
import { enter, mix, pop, progress } from "../anim";
import { featuredScene, notice } from "../content";
import { fontFamily } from "../fonts";
import { at } from "../timeline";
import { Icon, paths } from "./Icons";

export const panel = { x: 1140, y: 72, width: 436, height: 804 } as const;

export const threadScroll = 110;

const GoblinAvatar = () => (
    <span
        style={{
            display: "grid",
            placeItems: "center",
            width: 38,
            height: 38,
            flexShrink: 0,
        }}
    >
        <Img
            src={staticFile("goblin.svg")}
            style={{ width: "78%", height: "auto" }}
        />
    </span>
);

const Meta = ({ name, time }: { name: string; time: string }) => (
    <div
        style={{
            display: "flex",
            alignItems: "center",
            gap: 10,
            minHeight: 23,
            marginBottom: 3,
        }}
    >
        <strong style={{ fontWeight: 600, fontSize: 14 }}>{name}</strong>
        <span style={{ fontSize: 11, color: "#8b95a6", whiteSpace: "nowrap" }}>
            {time}
        </span>
    </div>
);

const Message = ({
    avatar,
    children,
    style,
}: {
    avatar: ReactNode;
    children: ReactNode;
    style?: CSSProperties;
}) => (
    <div
        style={{
            display: "flex",
            alignItems: "flex-start",
            gap: 14,
            marginBottom: 27,
            ...style,
        }}
    >
        {avatar}
        <div style={{ flex: 1, minWidth: 0 }}>{children}</div>
    </div>
);

const CheckRow = ({
    label,
    done,
    frame,
    waiting,
}: {
    label: string;
    done: number | null;
    frame: number;
    waiting: boolean;
}) => {
    const amount = done === null ? 0 : pop(frame, done, 12);
    const ring = waiting
        ? (1 - Math.cos(((frame - at.review) / 36) * Math.PI * 2)) / 2
        : 0;
    return (
        <div
            style={{
                display: "flex",
                alignItems: "center",
                gap: 11,
                fontSize: 11,
                lineHeight: 1.2,
            }}
        >
            <span
                style={{
                    position: "relative",
                    display: "grid",
                    placeItems: "center",
                    width: 17,
                    height: 17,
                    borderRadius: "50%",
                    flexShrink: 0,
                    background: amount > 0.02 ? "#149640" : "#ffffff",
                    border:
                        amount > 0.02
                            ? "1.5px solid #149640"
                            : "1.5px solid #428e54",
                    boxShadow: waiting
                        ? `0 0 0 ${mix(0, 5, ring)}px rgba(66, 142, 84, ${mix(0.35, 0, ring)})`
                        : "none",
                    color: "white",
                }}
            >
                <span
                    style={{
                        transform: `scale(${amount})`,
                        display: "grid",
                        placeItems: "center",
                    }}
                >
                    <Icon size={12} strokeWidth={2.3}>
                        {paths.check}
                    </Icon>
                </span>
            </span>
            <span style={{ color: amount > 0.5 ? "#4d6d5d" : "#668374" }}>
                {label}
            </span>
        </div>
    );
};

const Deliverable = ({
    icon,
    title,
    description,
    detail,
    accent,
    highlight,
    border,
}: {
    icon: ReactNode;
    title: string;
    description?: string;
    detail: string;
    accent: boolean;
    highlight: number;
    border: boolean;
}) => (
    <div
        style={{
            display: "flex",
            alignItems: "flex-start",
            gap: 16,
            padding: "16px 10px",
            margin: "0 -10px",
            borderTop: border ? "1px solid #eef0f1" : "none",
            borderRadius: 10,
            background: `rgba(234, 245, 231, ${highlight})`,
            boxShadow: `inset 0 0 0 ${mix(0, 1.5, highlight)}px rgba(102, 173, 89, ${highlight})`,
        }}
    >
        {icon}
        <span style={{ flex: 1, minWidth: 0 }}>
            <strong
                style={{
                    display: "flex",
                    alignItems: "center",
                    gap: 8,
                    fontSize: accent ? 14 : 12,
                    fontWeight: 500,
                    color: accent ? "#2672fd" : "#697e78",
                }}
            >
                {title}
                {accent ? <Icon size={15}>{paths.external}</Icon> : null}
            </strong>
            {description ? (
                <span
                    style={{
                        display: "block",
                        fontSize: 10,
                        color: "#85919f",
                        marginTop: 7,
                        lineHeight: 1.5,
                    }}
                >
                    {description}
                </span>
            ) : null}
            <span
                style={{
                    display: "block",
                    fontSize: 10,
                    color: "#85919f",
                    marginTop: 7,
                    lineHeight: 1.5,
                }}
            >
                {detail}
            </span>
        </span>
    </div>
);

const Composer = () => (
    <div
        style={{
            display: "grid",
            gap: 3,
            flexShrink: 0,
            minHeight: 92,
            margin: "8px 23px 16px",
            padding: "8px 12px",
            border: "1px solid #dce5e0",
            borderRadius: 14,
            background: "#ffffff",
            boxShadow: "0 7px 22px #3b6e3909",
        }}
    >
        <span
            style={{
                height: 32,
                padding: "5px 2px",
                fontSize: 14,
                color: "#9aa4b6",
            }}
        >
            Message Goblin...
        </span>
        <div
            style={{
                display: "flex",
                alignItems: "center",
                justifyContent: "space-between",
            }}
        >
            <div
                style={{
                    display: "flex",
                    alignItems: "center",
                    gap: 5,
                    color: "#7b8293",
                }}
            >
                <span
                    style={{
                        display: "grid",
                        placeItems: "center",
                        width: 35,
                        height: 35,
                    }}
                >
                    <Icon size={21}>{paths.attach}</Icon>
                </span>
                <span
                    style={{
                        display: "grid",
                        placeItems: "center",
                        width: 35,
                        height: 35,
                        fontSize: 23,
                        paddingBottom: 6,
                    }}
                >
                    @
                </span>
                <span
                    style={{
                        display: "grid",
                        placeItems: "center",
                        width: 35,
                        height: 35,
                    }}
                >
                    <Icon size={21}>{paths.emoji}</Icon>
                </span>
            </div>
            <span
                style={{
                    display: "grid",
                    placeItems: "center",
                    width: 40,
                    height: 40,
                    borderRadius: "50%",
                    background: "linear-gradient(145deg, #468852, #246442)",
                    color: "white",
                    boxShadow: "0 2px 6px #315f3720",
                }}
            >
                <Icon size={19}>{paths.send}</Icon>
            </span>
        </div>
    </div>
);

export const ConversationPanel = () => {
    const frame = useCurrentFrame();
    const appear = progress(frame, at.panel[0], at.panel[1]);
    const scroll = progress(frame, at.scroll[0], at.scroll[1]) * threadScroll;
    const typingVisible = frame >= at.thinking[0] + 4 && frame < at.reply;
    const pulse =
        (1 + Math.cos(((frame - at.thinking[0]) / 42) * Math.PI * 2)) / 2;
    const highlight =
        progress(frame, at.highlight, at.highlight + 12) *
        (1 - progress(frame, at.zoomOut[0] + 20, at.zoomOut[1]));
    const checkFrames = at.checks.map((value): number | null => value);
    return (
        <aside
            style={{
                position: "absolute",
                left: panel.x,
                top: panel.y,
                width: panel.width,
                height: panel.height,
                display: "flex",
                flexDirection: "column",
                overflow: "hidden",
                border: "1px solid #e2ece2",
                borderRadius: 19,
                background: "#ffffffd6",
                fontFamily,
                color: "#123b34",
                opacity: appear,
                transform: `translateX(${mix(60, 0, appear)}px)`,
            }}
        >
            <p
                style={{
                    margin: 0,
                    padding: "13px 23px",
                    borderBottom: "1px solid #e2ece2",
                    background: "#f3f8f0",
                    fontSize: 11,
                    lineHeight: 1.5,
                    color: "#657083",
                }}
            >
                <strong
                    style={{
                        display: "block",
                        fontSize: 12,
                        fontWeight: 600,
                        color: "#435e50",
                        marginBottom: 3,
                    }}
                >
                    {notice.title}
                </strong>
                <span>{notice.detail}</span>
            </p>
            <div
                style={{
                    display: "flex",
                    justifyContent: "space-between",
                    gap: 10,
                    minHeight: 61,
                    padding: "0 23px",
                    borderBottom: "1px solid #edf0ec",
                }}
            >
                {["Conversation", "Activity", "Work", "Files"].map(
                    (tab, index) => (
                        <span
                            key={tab}
                            style={{
                                position: "relative",
                                display: "flex",
                                alignItems: "center",
                                padding: "5px 3px 0",
                                fontSize: 13,
                                color: index === 0 ? "#193e32" : "#758092",
                                fontWeight: index === 0 ? 600 : 500,
                            }}
                        >
                            {tab}
                            {index === 0 ? (
                                <span
                                    style={{
                                        position: "absolute",
                                        left: 0,
                                        right: 0,
                                        bottom: 0,
                                        height: 3,
                                        background: "#23733d",
                                        borderRadius: "4px 4px 0 0",
                                    }}
                                />
                            ) : null}
                        </span>
                    ),
                )}
            </div>
            <div
                style={{
                    display: "flex",
                    alignItems: "center",
                    gap: 13,
                    minHeight: 68,
                    margin: "0 23px",
                    borderBottom: "1px solid #edf0ec",
                }}
            >
                <Img
                    src={staticFile(`${featuredScene.channel}.svg`)}
                    style={{ width: 29, height: 29, objectFit: "contain" }}
                />
                <h2 style={{ margin: 0, fontSize: 14, whiteSpace: "nowrap" }}>
                    {featuredScene.name}
                </h2>
                <p
                    style={{
                        margin: 0,
                        minWidth: 0,
                        overflow: "hidden",
                        textOverflow: "ellipsis",
                        whiteSpace: "nowrap",
                        fontSize: 12,
                        color: "#788396",
                    }}
                >
                    {featuredScene.location}
                </p>
                <span
                    style={{ marginLeft: "auto", color: "#49616c", padding: 4 }}
                >
                    <Icon size={16}>{paths.external}</Icon>
                </span>
            </div>
            <div
                style={{
                    position: "relative",
                    flex: 1,
                    minHeight: 0,
                    overflow: "hidden",
                }}
            >
                <div
                    style={{
                        padding: "15px 23px 10px",
                        transform: `translateY(${-scroll}px)`,
                    }}
                >
                    <Message
                        style={enter(frame, at.request, 16, 14)}
                        avatar={
                            <span
                                style={{
                                    display: "grid",
                                    placeItems: "center",
                                    width: 38,
                                    height: 38,
                                    flexShrink: 0,
                                    boxSizing: "border-box",
                                    border: "1px solid #e3e9e4",
                                    borderRadius: "50%",
                                    background: "#fff",
                                }}
                            >
                                <Img
                                    src={staticFile(
                                        `${featuredScene.channel}.svg`,
                                    )}
                                    style={{
                                        width: "58%",
                                        height: "58%",
                                        objectFit: "contain",
                                    }}
                                />
                            </span>
                        }
                    >
                        <Meta name={featuredScene.name} time="10:24 AM" />
                        <p
                            style={{
                                margin: 0,
                                color: "#657083",
                                fontSize: 14,
                                lineHeight: 1.35,
                            }}
                        >
                            {featuredScene.request}
                        </p>
                    </Message>
                    {typingVisible ? (
                        <div
                            style={{
                                display: "flex",
                                alignItems: "center",
                                gap: 14,
                                minHeight: 58,
                                marginBottom: 24,
                                ...enter(frame, at.thinking[0] + 4, 10, 10),
                            }}
                        >
                            <GoblinAvatar />
                            <span
                                style={{
                                    width: 10,
                                    height: 10,
                                    borderRadius: "50%",
                                    background: "#278d43",
                                    opacity: mix(0.55, 1, pulse),
                                    transform: `scale(${mix(0.75, 1, pulse)})`,
                                }}
                            />
                        </div>
                    ) : null}
                    {frame >= at.reply ? (
                        <Message
                            style={enter(frame, at.reply, 16, 14)}
                            avatar={<GoblinAvatar />}
                        >
                            <Meta name="Goblin" time="10:24 AM" />
                            <div
                                style={{
                                    display: "flex",
                                    flexDirection: "column",
                                    gap: 10,
                                    marginTop: 5,
                                    padding: "14px 15px",
                                    borderRadius: 13,
                                    background:
                                        "linear-gradient(120deg, #f0f6ef, #f2f7f2)",
                                }}
                            >
                                {featuredScene.steps.map((step, index) => (
                                    <CheckRow
                                        key={step}
                                        label={step}
                                        done={checkFrames[index] ?? null}
                                        frame={frame}
                                        waiting={
                                            index ===
                                                featuredScene.steps.length -
                                                    1 && frame >= at.review
                                        }
                                    />
                                ))}
                            </div>
                            <p
                                style={{
                                    margin: "10px 3px 0",
                                    fontSize: 11,
                                    lineHeight: 1.5,
                                    color: "#7e8b89",
                                    opacity: progress(
                                        frame,
                                        at.summary,
                                        at.summary + 14,
                                    ),
                                }}
                            >
                                {featuredScene.reply}
                            </p>
                        </Message>
                    ) : null}
                    {frame >= at.artifact ? (
                        <Message
                            style={{
                                marginTop: 8,
                                ...enter(frame, at.artifact, 20, 16),
                            }}
                            avatar={<GoblinAvatar />}
                        >
                            <Meta name="Goblin" time="10:26 AM" />
                            <div
                                style={{
                                    border: "1px solid #e7ebec",
                                    borderRadius: 12,
                                    padding: "0 15px",
                                    marginTop: 7,
                                }}
                            >
                                <div
                                    style={{
                                        display: "flex",
                                        justifyContent: "space-between",
                                        alignItems: "baseline",
                                        padding: "13px 0",
                                        borderBottom: "1px solid #e2ece2",
                                    }}
                                >
                                    <strong
                                        style={{
                                            fontSize: 12,
                                            fontWeight: 600,
                                        }}
                                    >
                                        Ready for review
                                    </strong>
                                    <span
                                        style={{
                                            fontSize: 10,
                                            color: "#657083",
                                        }}
                                    >
                                        Illustrative result
                                    </span>
                                </div>
                                <Deliverable
                                    icon={
                                        <Img
                                            src={staticFile("github.svg")}
                                            style={{
                                                width: 34,
                                                height: 34,
                                                marginTop: 2,
                                            }}
                                        />
                                    }
                                    title={featuredScene.artifactTitle}
                                    description={
                                        featuredScene.artifactDescription
                                    }
                                    detail="Ready for your review"
                                    accent
                                    highlight={highlight}
                                    border={false}
                                />
                                <Deliverable
                                    icon={
                                        <span
                                            style={{
                                                display: "grid",
                                                placeItems: "center",
                                                width: 34,
                                                height: 36,
                                                borderRadius: 8,
                                                border: "1px solid #eef0ef",
                                                color: "#254f42",
                                                flexShrink: 0,
                                            }}
                                        >
                                            <Icon size={26}>{paths.file}</Icon>
                                        </span>
                                    }
                                    title={featuredScene.documentTitle}
                                    detail="Findings and decisions for review"
                                    accent={false}
                                    highlight={0}
                                    border
                                />
                            </div>
                        </Message>
                    ) : null}
                </div>
            </div>
            <Composer />
        </aside>
    );
};
