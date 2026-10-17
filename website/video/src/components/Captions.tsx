import { useCurrentFrame } from "remotion";
import { enter, mix, pop, progress } from "../anim";
import { benefits, endCard, loopSteps } from "../content";
import { fontFamily } from "../fonts";
import { at } from "../timeline";
import { Icon, paths } from "./Icons";

const stepStarts = [at.loopSteps, at.loopSteps + 8, at.loopSteps + 16];
const stepDone = [at.loopSteps + 18, at.review - 10];

const activeStep = (frame: number): number => {
    if (frame < stepDone[0]) return 0;
    if (frame < stepDone[1]) return 1;
    return 2;
};

// Screen-space captions shown while the camera is close to the conversation panel.
export const LoopCaptions = () => {
    const frame = useCurrentFrame();
    const visible =
        progress(frame, at.loopSteps - 10, at.loopSteps + 8) *
        (1 - progress(frame, at.zoomOut[0], at.zoomOut[0] + 22));
    if (visible <= 0) return null;
    const current = activeStep(frame);
    return (
        <div
            style={{
                position: "absolute",
                inset: 0,
                fontFamily,
                opacity: visible,
            }}
        >
            <div
                style={{
                    position: "absolute",
                    inset: 0,
                    background:
                        "linear-gradient(90deg, #f8fbf7 0%, #f8fbf7 35%, #f8fbf7b0 38.5%, #f8fbf700 41.5%)",
                }}
            />
            <div style={{ position: "absolute", left: 118, top: 340 }}>
                <p
                    style={{
                        margin: "0 0 34px",
                        fontSize: 21,
                        fontWeight: 600,
                        letterSpacing: "0.14em",
                        textTransform: "uppercase",
                        color: "#587765",
                        ...enter(frame, at.loopSteps - 6, 12, 16),
                    }}
                >
                    {endCard.eyebrow.replace(/\.$/, "")}
                </p>
                {loopSteps.map((step, index) => {
                    const done = index < 2 && frame >= stepDone[index];
                    const doneAmount =
                        index < 2 ? pop(frame, stepDone[index], 14) : 0;
                    const focus = current === index;
                    return (
                        <div
                            key={step}
                            style={{
                                display: "flex",
                                alignItems: "center",
                                gap: 24,
                                height: 92,
                                ...enter(frame, stepStarts[index], 24, 18),
                            }}
                        >
                            <span
                                style={{
                                    display: "grid",
                                    placeItems: "center",
                                    width: 42,
                                    height: 42,
                                    borderRadius: "50%",
                                    flexShrink: 0,
                                    border: `2.5px solid ${done ? "#149640" : focus ? "#428e54" : "#c6d6c8"}`,
                                    background: done ? "#149640" : "#ffffff",
                                    color: "white",
                                }}
                            >
                                <span
                                    style={{
                                        transform: `scale(${doneAmount})`,
                                        display: "grid",
                                    }}
                                >
                                    <Icon size={25} strokeWidth={2.4}>
                                        {paths.check}
                                    </Icon>
                                </span>
                            </span>
                            <span
                                style={{
                                    fontSize: 62,
                                    fontWeight: 600,
                                    letterSpacing: -2.2,
                                    color: focus
                                        ? index === 2
                                            ? "#087637"
                                            : "#123b34"
                                        : done
                                          ? "#8aa391"
                                          : "#b9c6bc",
                                }}
                            >
                                {step}
                            </span>
                        </div>
                    );
                })}
            </div>
        </div>
    );
};

const Pill = ({
    label,
    title,
    detail,
    range,
}: {
    label?: string;
    title: string;
    detail: string;
    range: readonly [number, number];
}) => {
    const frame = useCurrentFrame();
    const appear = progress(frame, range[0], range[0] + 18);
    const leave = progress(frame, range[1] - 16, range[1]);
    if (appear <= 0 || leave >= 1) return null;
    return (
        <div
            style={{
                position: "absolute",
                left: "50%",
                bottom: 26,
                display: "flex",
                flexDirection: "column",
                alignItems: "center",
                gap: 8,
                minWidth: 680,
                padding: "14px 40px 17px",
                border: "1px solid #dce9dc",
                borderRadius: 26,
                background: "#ffffffee",
                boxShadow: "0 22px 60px #2f5d3a1f",
                fontFamily,
                textAlign: "center",
                opacity: appear * (1 - leave),
                transform: `translate(-50%, ${mix(26, 0, appear) + mix(0, -14, leave)}px)`,
            }}
        >
            {label ? (
                <span
                    style={{
                        display: "flex",
                        alignItems: "center",
                        gap: 8,
                        marginBottom: 2,
                        padding: "5px 16px 5px 12px",
                        border: "2px solid #3f8f4f",
                        borderRadius: 999,
                        background: "#ffffff",
                        color: "#23732b",
                        fontSize: 18,
                        fontWeight: 600,
                        ...enter(frame, range[0] + 8, 8, 14),
                    }}
                >
                    <Icon size={21} strokeWidth={1.9}>
                        {paths.shield}
                    </Icon>
                    {label}
                </span>
            ) : null}
            <strong
                style={{
                    fontSize: 33,
                    fontWeight: 600,
                    letterSpacing: -1,
                    color: "#123b34",
                }}
            >
                {title}
            </strong>
            <span style={{ fontSize: 22, color: "#657083" }}>{detail}</span>
        </div>
    );
};

export const BenefitCaptions = () => (
    <>
        <Pill
            title={benefits.tools.title}
            detail={benefits.tools.detail}
            range={at.toolsCaption}
        />
        <Pill
            label={benefits.hosting.label}
            title={benefits.hosting.title}
            detail={benefits.hosting.detail}
            range={at.hostingCaption}
        />
    </>
);
