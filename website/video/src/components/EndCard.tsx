import { Img, staticFile, useCurrentFrame } from "remotion";
import { enter, progress } from "../anim";
import { endCard, intro } from "../content";
import { fontFamily } from "../fonts";
import { at } from "../timeline";
import { Icon, paths } from "./Icons";

const Link = ({ icon, label }: { icon: "globe" | "github"; label: string }) => (
    <span
        style={{
            display: "flex",
            alignItems: "center",
            gap: 14,
            padding: "16px 30px",
            border: "1px solid #dce9dc",
            borderRadius: 999,
            background: "#ffffff",
            boxShadow: "0 10px 30px #2f5d3a12",
            fontSize: 28,
            fontWeight: 600,
            color: "#274236",
        }}
    >
        {icon === "github" ? (
            <Img
                src={staticFile("github.svg")}
                style={{ width: 32, height: 32 }}
            />
        ) : (
            <span style={{ color: "#23733d" }}>
                <Icon size={30} strokeWidth={2}>
                    {paths.external}
                </Icon>
            </span>
        )}
        {label}
    </span>
);

export const EndCard = () => {
    const frame = useCurrentFrame();
    const appear = progress(frame, at.endCard[0], at.endCard[1]);
    if (appear <= 0) return null;
    const start = at.endCard[0] + 18;
    return (
        <div
            style={{
                position: "absolute",
                inset: 0,
                display: "flex",
                flexDirection: "column",
                alignItems: "center",
                justifyContent: "center",
                fontFamily,
                color: "#123b34",
                background:
                    "radial-gradient(ellipse 70% 60% at 50% 42%, #eaf5e7 0%, #f8fbf7 62%), #f8fbf7",
                opacity: appear,
            }}
        >
            <div
                style={{
                    display: "flex",
                    alignItems: "center",
                    gap: 22,
                    ...enter(frame, start, 18, 18),
                }}
            >
                <Img
                    src={staticFile("goblin.svg")}
                    style={{ width: 84, height: "auto" }}
                />
                <span
                    style={{
                        fontSize: 64,
                        fontWeight: 700,
                        letterSpacing: -2,
                        color: "#114f3c",
                    }}
                >
                    Goblin
                </span>
            </div>
            <p
                style={{
                    margin: "46px 0 18px",
                    fontSize: 28,
                    fontWeight: 600,
                    color: "#587765",
                    ...enter(frame, start + 8, 16, 18),
                }}
            >
                {intro.kicker}
            </p>
            <h1
                style={{
                    margin: 0,
                    fontSize: 92,
                    fontWeight: 500,
                    letterSpacing: -3.6,
                    lineHeight: 1.12,
                    textAlign: "center",
                    ...enter(frame, start + 14, 22, 20),
                }}
            >
                {intro.headline[0]}{" "}
                <span style={{ fontWeight: 700, color: "#087637" }}>
                    {intro.headline[1]}
                </span>
            </h1>
            <div
                style={{
                    display: "flex",
                    gap: 22,
                    marginTop: 64,
                    ...enter(frame, start + 26, 18, 18),
                }}
            >
                <Link icon="globe" label={endCard.website} />
                <Link icon="github" label={endCard.repository} />
            </div>
            <p
                style={{
                    position: "absolute",
                    bottom: 40,
                    margin: 0,
                    fontSize: 19,
                    lineHeight: 1.6,
                    textAlign: "center",
                    color: "#8b95a6",
                    opacity: progress(frame, start + 34, start + 52),
                }}
            >
                {endCard.footnote.map((line) => (
                    <span key={line} style={{ display: "block" }}>
                        {line}
                    </span>
                ))}
            </p>
        </div>
    );
};
