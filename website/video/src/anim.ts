import { Easing, interpolate, spring } from "remotion";
import { fps } from "./timeline";

const smooth = Easing.bezier(0.33, 0, 0.2, 1);

export const progress = (
    frame: number,
    start: number,
    end: number,
    easing: (input: number) => number = smooth,
): number =>
    interpolate(frame, [start, end], [0, 1], {
        easing,
        extrapolateLeft: "clamp",
        extrapolateRight: "clamp",
    });

export const pop = (frame: number, start: number, damping = 16): number =>
    spring({
        frame: frame - start,
        fps,
        config: { damping, mass: 0.7, stiffness: 140 },
    });

export const mix = (from: number, to: number, amount: number): number =>
    from + (to - from) * amount;

// Fades an element in while lifting it from below.
export const enter = (
    frame: number,
    start: number,
    distance = 18,
    duration = 18,
): { opacity: number; transform: string } => {
    const amount = progress(frame, start, start + duration);
    return {
        opacity: amount,
        transform: `translateY(${mix(distance, 0, amount)}px)`,
    };
};

// Scales absolute M, H, V, L, and Q path commands from the origin.
export const scaleAbsolutePath = (
    path: string,
    scaleX: number,
    scaleY: number,
): string =>
    path.replace(
        /([MHVLQ])([^MHVLQ]*)/g,
        (_, command: string, values: string) => {
            const numbers = values
                .trim()
                .split(/[\s,]+/)
                .filter(Boolean)
                .map(Number);
            const scaled = numbers.map((value, index) => {
                if (command === "H") return value * scaleX;
                if (command === "V") return value * scaleY;
                return value * (index % 2 === 0 ? scaleX : scaleY);
            });
            return `${command}${scaled.map((value) => value.toFixed(2)).join(" ")}`;
        },
    );
