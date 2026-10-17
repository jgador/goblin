import { AbsoluteFill, Easing, spring, useCurrentFrame } from "remotion";
import { mix, progress } from "./anim";
import { fps } from "./timeline";

export const logoClip = { durationInFrames: 360, size: 1080 } as const;

// Geometry from assets/goblin.svg. The parts move, but their shapes and colours stay intact.
const mark = {
    head: "M625.11,86.18v-53.95c0-17.8-14.43-32.22-32.22-32.22H55.31C37.51,0,23.09,14.43,23.09,32.22v54.04c-12.9.93-23.09,11.66-23.09,24.8v52.72c0,13.14,10.18,23.87,23.09,24.8v54.04c0,17.8,14.43,32.22,32.22,32.22h537.58c17.8,0,32.22-14.43,32.22-32.22v-53.95c13.75,0,24.89-11.14,24.89-24.89v-52.72c0-13.75-11.14-24.89-24.89-24.89Z",
    headColor: "#10182a",
    width: 650,
    height: 602.03,
    centerX: 324.1,
    headBottom: 274.84,
    blockTop: 327.17,
    blockSize: 274.86,
    blockRadius: 32.02,
    blocks: [
        { x: 23.09, color: "#595a5a" },
        { x: 350.25, color: "#939393" },
    ],
    eyes: [198.98, 449.23],
    eyeY: 101.28,
    eyeWidth: 59.79,
    eyeHeight: 36.09,
    eyeRadius: 16.19,
} as const;

const logoScale = 0.86;
const placement = {
    scale: logoScale,
    x: (logoClip.size - mark.width * logoScale) / 2,
    y: 322,
} as const;

// Frame numbers for each behaviour in the loop, which starts and ends on an empty stage.
const beat = {
    blocksIn: [2, 6],
    headFall: [8, 19],
    eyesOpen: 25,
    idle: [30, 50, 330, 338],
    blinks: [50, 89, 208, 312, 323],
    gazeX: [
        [68, -26],
        [88, 26],
        [110, 0],
        [128, 16],
        [164, 0],
        [178, -22],
        [244, 0],
    ],
    gazeY: [
        [68, 4],
        [88, -2],
        [110, 0],
        [128, -10],
        [164, 0],
        [178, -22],
        [244, 0],
        [332, -18],
    ],
    curious: [128, 164],
    thinking: [184, 192, 236, 246],
    hop: { crouch: 250, launch: 259, land: 279, height: 130 },
    happy: { close: 262, arcs: 265, flatten: 292, open: 295 },
    exit: { crouch: 334, launch: 340, blocksOut: [345, 348] },
} as const;

type Keys = readonly (readonly [number, number])[];
type SpringConfig = { damping: number; mass: number; stiffness: number };

const settle: SpringConfig = { damping: 14, mass: 0.8, stiffness: 120 };
const lively: SpringConfig = { damping: 10, mass: 0.9, stiffness: 110 };
const bouncy: SpringConfig = { damping: 10, mass: 0.6, stiffness: 170 };

const springAt = (frame: number, start: number, config = settle) =>
    spring({ frame: Math.max(0, frame - start), fps, config });

// Springs towards each [frame, value] target in turn, starting from zero.
const springPath = (frame: number, keys: Keys, config = settle): number => {
    let value = 0;
    let previous = 0;
    for (const [start, target] of keys) {
        value += (target - previous) * springAt(frame, start, config);
        previous = target;
    }
    return value;
};

// A damped wobble that starts at 1 on impact.
const impact = (frame: number, start: number, decay = 5, speed = 0.6) =>
    frame < start
        ? 0
        : Math.exp(-(frame - start) / decay) *
          Math.cos((frame - start) * speed);

const blink = (frame: number, start: number) =>
    progress(frame, start, start + 3, Easing.in(Easing.quad)) -
    progress(frame, start + 4, start + 9, Easing.out(Easing.quad));

const pivot = (x: number, y: number, transform: string) =>
    `translate(${x} ${y}) ${transform} translate(${-x} ${-y})`;

const hopLift = (frame: number) => {
    const { launch, land, height } = beat.hop;
    const amount = (frame - launch) / (land - launch);
    return amount <= 0 || amount >= 1 ? 0 : -height * 4 * amount * (1 - amount);
};

const wave = (frame: number, period: number, phase = 0) =>
    Math.sin((2 * Math.PI * frame) / period + phase);

export const GoblinLogo = () => {
    const frame = useCurrentFrame();
    const [idleIn, idleFull, idleFade, idleOut] = beat.idle;
    const idle =
        progress(frame, idleIn, idleFull) *
        (1 - progress(frame, idleFade, idleOut));
    const [thinkIn, thinkFull, thinkFade, thinkOut] = beat.thinking;
    const thinking =
        progress(frame, thinkIn, thinkFull) *
        (1 - progress(frame, thinkFade, thinkOut));
    // Whole-body hop with anticipation, stretch in the air, and squash on landing.
    const { crouch, launch, land } = beat.hop;
    const bodyLift = hopLift(frame);
    const crouching =
        progress(frame, crouch, launch - 2) -
        progress(frame, launch - 2, launch + 1);
    const airborne = (frame - launch) / (land - launch);
    const stretch =
        airborne > 0 && airborne < 1 ? 0.08 * Math.abs(1 - 2 * airborne) : 0;
    const landing = impact(frame, land);
    const bodyScaleY = 1 - 0.1 * crouching + stretch - 0.12 * landing;
    const bodyScaleX = 1 + 0.05 * crouching - stretch / 2 + 0.06 * landing;

    // Head: falls in, bobs, follows the eyes, tilts when curious, and leaves upwards.
    const [fallStart, fallEnd] = beat.headFall;
    const fall = progress(frame, fallStart, fallEnd, Easing.linear);
    const exitLaunch = progress(
        frame,
        beat.exit.launch,
        beat.exit.launch + 10,
        Easing.linear,
    );
    const gazeX =
        springPath(frame, beat.gazeX) +
        8 * wave(frame - thinkIn, 40) * thinking;
    const gazeY = springPath(frame, beat.gazeY);
    const [curiousIn, curiousOut] = beat.curious;
    const tilt = springPath(
        frame,
        [
            [curiousIn, -9],
            [curiousOut, 0],
        ],
        lively,
    );
    const headX = gazeX * 0.3;
    const headY =
        -760 * (1 - fall * fall) -
        820 * exitLaunch * exitLaunch -
        5 * wave(frame, 60) * idle +
        springPath(frame, [
            [curiousIn, -10],
            [curiousOut, 0],
        ]) +
        0.5 * (hopLift(frame - 2) - bodyLift);
    const exitCrouch =
        progress(frame, beat.exit.crouch, beat.exit.launch) -
        progress(frame, beat.exit.launch, beat.exit.launch + 2);
    const headStretch =
        (fall < 1 ? 0.12 * fall : 0) +
        0.18 * progress(frame, beat.exit.launch, beat.exit.launch + 6);
    const headSquash =
        0.16 * impact(frame, fallEnd) +
        0.1 * exitCrouch +
        0.06 * impact(frame, land + 2);
    const headScaleY = 1 + headStretch - headSquash;
    const headScaleX = 1 - headStretch / 2 + headSquash / 2;

    // Blocks: pop in, breathe, take turns lifting while thinking, then pop out.
    const blocks = mark.blocks.map((block, index) => {
        const popIn = springAt(frame, beat.blocksIn[index], bouncy);
        const outStart = beat.exit.blocksOut[index];
        const popOut = progress(
            frame,
            outStart,
            outStart + 9,
            Easing.back(1.2),
        );
        const scale = Math.max(0, popIn * (1 - popOut));
        const lift =
            30 *
            thinking *
            Math.max(0, wave(frame - thinkIn, 26, index * Math.PI)) ** 2;
        const scaleY =
            scale *
            (1 +
                0.015 * wave(frame, 60, Math.PI / 2 + index) * idle -
                0.05 * impact(frame, fallEnd) -
                0.05 * impact(frame, beat.exit.launch));
        return { ...block, scale, scaleY, lift };
    });
    const presence = (blocks[0].scale + blocks[1].scale) / 2;

    // Eyes: open on landing, blink, widen when curious, squint while thinking, smile after the hop.
    const { close, arcs, flatten, open } = beat.happy;
    const opened =
        frame < beat.eyesOpen
            ? 0
            : springAt(frame, beat.eyesOpen, {
                  damping: 9,
                  mass: 0.6,
                  stiffness: 150,
              });
    const blinking = Math.min(
        1,
        beat.blinks.reduce((total, start) => total + blink(frame, start), 0),
    );
    const smiling =
        progress(frame, close, close + 3) - progress(frame, open, open + 5);
    const shut = Math.max(blinking, smiling);
    const size = springPath(frame, [
        [curiousIn, 0.32],
        [curiousOut, 0],
        [thinkIn - 6, -0.3],
        [thinkOut - 2, 0],
    ]);
    const curiousExtra = springPath(frame, [
        [curiousIn, 0.12],
        [curiousOut, 0],
    ]);
    const arc =
        progress(frame, arcs, arcs + 6, Easing.out(Easing.back(2))) *
        (1 - progress(frame, flatten, flatten + 3));

    const shadowLift = 1 + (0.35 * bodyLift) / beat.hop.height;

    return (
        <AbsoluteFill
            style={{
                background:
                    "radial-gradient(ellipse 60% 55% at 50% 48%, #eaf5e7 0%, #f8fbf7 68%), #f8fbf7",
            }}
        >
            <svg viewBox={`0 0 ${logoClip.size} ${logoClip.size}`}>
                <defs>
                    <radialGradient id="goblin-shadow">
                        <stop
                            offset="0"
                            stopColor="#123b34"
                            stopOpacity="0.2"
                        />
                        <stop offset="1" stopColor="#123b34" stopOpacity="0" />
                    </radialGradient>
                </defs>
                <g
                    transform={`translate(${placement.x} ${placement.y}) scale(${placement.scale})`}
                >
                    <ellipse
                        cx={mark.centerX}
                        cy={mark.height + 16}
                        rx={340 * presence * shadowLift * bodyScaleX}
                        ry={34 * presence * shadowLift}
                        fill="url(#goblin-shadow)"
                        opacity={shadowLift}
                    />
                    <g
                        transform={pivot(
                            mark.centerX,
                            mark.height,
                            `translate(0 ${bodyLift}) scale(${bodyScaleX} ${bodyScaleY})`,
                        )}
                    >
                        {blocks.map((block) => {
                            const centerX = block.x + mark.blockSize / 2;
                            return (
                                <g
                                    key={block.x}
                                    transform={pivot(
                                        centerX,
                                        mark.height,
                                        `translate(0 ${-block.lift}) scale(${block.scale} ${block.scaleY})`,
                                    )}
                                >
                                    <rect
                                        x={block.x}
                                        y={mark.blockTop}
                                        width={mark.blockSize}
                                        height={mark.blockSize}
                                        rx={mark.blockRadius}
                                        fill={block.color}
                                    />
                                </g>
                            );
                        })}
                        <g
                            transform={`translate(${headX} ${headY}) ${pivot(
                                mark.centerX,
                                200,
                                `rotate(${tilt + gazeX * 0.05})`,
                            )} ${pivot(
                                mark.centerX,
                                mark.headBottom,
                                `scale(${headScaleX} ${headScaleY})`,
                            )}`}
                        >
                            <path d={mark.head} fill={mark.headColor} />
                            <g transform={`translate(${gazeX} ${gazeY})`}>
                                {mark.eyes.map((eyeX, index) => {
                                    const grow =
                                        1 +
                                        size +
                                        (index === 1 ? curiousExtra : 0);
                                    const scaleY = mix(
                                        0.08,
                                        grow,
                                        opened * (1 - shut),
                                    );
                                    const scaleX =
                                        (1 - 0.12 * (grow - 1)) *
                                        (1 + 0.06 * (1 - opened * (1 - shut)));
                                    return (
                                        <g key={eyeX}>
                                            {smiling < 0.97 ? (
                                                <rect
                                                    x={eyeX - mark.eyeWidth / 2}
                                                    y={
                                                        mark.eyeY -
                                                        mark.eyeHeight / 2
                                                    }
                                                    width={mark.eyeWidth}
                                                    height={mark.eyeHeight}
                                                    rx={mark.eyeRadius}
                                                    fill="#ffffff"
                                                    transform={pivot(
                                                        eyeX,
                                                        mark.eyeY,
                                                        `scale(${scaleX} ${scaleY})`,
                                                    )}
                                                />
                                            ) : null}
                                            {arc > 0 ? (
                                                <path
                                                    d={`M${eyeX - 25} ${mark.eyeY + 10} Q${eyeX} ${mark.eyeY - 26} ${eyeX + 25} ${mark.eyeY + 10}`}
                                                    fill="none"
                                                    stroke="#ffffff"
                                                    strokeWidth={15}
                                                    strokeLinecap="round"
                                                    opacity={Math.min(
                                                        1,
                                                        arc * 4,
                                                    )}
                                                    transform={pivot(
                                                        eyeX,
                                                        mark.eyeY,
                                                        `scale(1 ${Math.max(0.05, arc)})`,
                                                    )}
                                                />
                                            ) : null}
                                        </g>
                                    );
                                })}
                            </g>
                        </g>
                    </g>
                </g>
            </svg>
        </AbsoluteFill>
    );
};
