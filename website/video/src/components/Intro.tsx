import { Img, staticFile, useCurrentFrame } from "remotion";
import { enter, progress } from "../anim";
import { intro } from "../content";
import { fontFamily } from "../fonts";
import { at } from "../timeline";

const Word = ({
    word,
    start,
    frame,
}: {
    word: string;
    start: number;
    frame: number;
}) => (
    <span
        style={{
            display: "inline-block",
            whiteSpace: "pre",
            ...enter(frame, start, 22, 16),
        }}
    >
        {word}
    </span>
);

// Reproduces the website header and hero introduction in stage coordinates.
export const Intro = () => {
    const frame = useCurrentFrame();
    const words = intro.headline.map((line) => line.split(" "));
    let wordIndex = 0;
    const chrome = progress(frame, 0, 12);
    return (
        <div style={{ fontFamily, color: "#123b34" }}>
            <p
                style={{
                    position: "absolute",
                    left: 31,
                    top: 36,
                    margin: 0,
                    color: "#587765",
                    fontSize: 11,
                    fontWeight: 600,
                    letterSpacing: "0.12em",
                    textTransform: "uppercase",
                    opacity: chrome,
                }}
            >
                Goblin
                <span style={{ padding: "0 8px", color: "#a8b9ab" }}>/</span>In
                the loop
            </p>
            <div
                style={{
                    position: "absolute",
                    left: 1576 - 108,
                    top: 24,
                    display: "flex",
                    alignItems: "center",
                    justifyContent: "center",
                    gap: 8,
                    width: 108,
                    height: 40,
                    border: "1px solid #dce9dc",
                    borderRadius: 999,
                    background: "white",
                    color: "#274236",
                    fontSize: 13,
                    fontWeight: 600,
                    opacity: chrome,
                }}
            >
                <Img
                    src={staticFile("github.svg")}
                    style={{ width: 21, height: 21 }}
                />
                GitHub
            </div>
            <header
                style={{ position: "absolute", left: 61, top: 92, width: 900 }}
            >
                <p
                    style={{
                        margin: "0 0 8px",
                        fontSize: 12,
                        fontWeight: 600,
                        color: "#587765",
                        ...enter(frame, at.kicker, 10, 14),
                    }}
                >
                    {intro.kicker}
                </p>
                <h1
                    style={{
                        margin: 0,
                        fontSize: 38.4,
                        fontWeight: 500,
                        letterSpacing: -1.5,
                        lineHeight: 1.25,
                    }}
                >
                    {words.map((line, lineIndex) => (
                        <span
                            key={lineIndex}
                            style={{
                                display: "block",
                                color: lineIndex === 1 ? "#087637" : undefined,
                                fontWeight: lineIndex === 1 ? 700 : 500,
                            }}
                        >
                            {line.map((word, index) => {
                                const start =
                                    at.headline +
                                    wordIndex++ * 3 +
                                    lineIndex * 4;
                                return (
                                    <Word
                                        key={index}
                                        word={
                                            index < line.length - 1
                                                ? `${word} `
                                                : word
                                        }
                                        start={start}
                                        frame={frame}
                                    />
                                );
                            })}
                        </span>
                    ))}
                </h1>
                <p
                    style={{
                        maxWidth: "62ch",
                        margin: "10px 0 0",
                        fontSize: 16.8,
                        lineHeight: 1.5,
                        color: "#657083",
                        letterSpacing: -0.35,
                        ...enter(frame, at.subheadline, 12, 18),
                    }}
                >
                    {intro.subheadline}
                </p>
            </header>
        </div>
    );
};
