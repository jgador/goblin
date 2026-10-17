import { Easing, interpolate } from "remotion";
import { at, height, stage, width } from "./timeline";

type Shot = { x: number; y: number; zoom: number };

const shots = {
    headline: { x: 376, y: 172, zoom: 2.1 },
    wide: { x: 800, y: 450, zoom: 1 },
    conversation: { x: 1225, y: 466, zoom: 1.65 },
    review: { x: 1225, y: 545, zoom: 1.65 },
    overview: { x: 800, y: 528, zoom: 0.86 },
    settle: { x: 790, y: 530, zoom: 0.88 },
} satisfies Record<string, Shot>;

const keyframes: [number, Shot][] = [
    [0, shots.headline],
    [at.pullback[0], shots.headline],
    [at.pullback[1], shots.wide],
    [at.zoomIn[0], shots.wide],
    [at.zoomIn[1], shots.conversation],
    [at.scroll[0], shots.conversation],
    [at.scroll[1], shots.review],
    [at.zoomOut[0], shots.review],
    [at.zoomOut[1], shots.overview],
    [at.endCard[1], shots.settle],
];

const ease = Easing.bezier(0.45, 0, 0.2, 1);

export const cameraAt = (frame: number): Shot => {
    const nextIndex = keyframes.findIndex(([start]) => start > frame);
    if (nextIndex === -1) return keyframes[keyframes.length - 1][1];
    if (nextIndex === 0) return keyframes[0][1];
    const [start, from] = keyframes[nextIndex - 1];
    const [end, to] = keyframes[nextIndex];
    const amount = interpolate(frame, [start, end], [0, 1], {
        easing: ease,
        extrapolateLeft: "clamp",
        extrapolateRight: "clamp",
    });
    return {
        x: from.x + (to.x - from.x) * amount,
        y: from.y + (to.y - from.y) * amount,
        zoom: Math.exp(
            Math.log(from.zoom) +
                (Math.log(to.zoom) - Math.log(from.zoom)) * amount,
        ),
    };
};

export const cameraTransform = (frame: number): string => {
    const shot = cameraAt(frame);
    const scale = stage.scale * shot.zoom;
    return `translate(${width / 2 - shot.x * scale}px, ${height / 2 - shot.y * scale}px) scale(${scale})`;
};
