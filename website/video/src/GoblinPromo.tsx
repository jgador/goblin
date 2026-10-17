import { AbsoluteFill, useCurrentFrame } from "remotion";
import { cameraTransform } from "./camera";
import { BenefitCaptions, LoopCaptions } from "./components/Captions";
import { ConversationPanel } from "./components/ConversationPanel";
import { EndCard } from "./components/EndCard";
import { Intro } from "./components/Intro";
import { Network } from "./components/Network";
import { fontFamily } from "./fonts";
import { stage } from "./timeline";

export const GoblinPromo = () => {
    const frame = useCurrentFrame();
    return (
        <AbsoluteFill
            style={{
                overflow: "hidden",
                fontFamily,
                background:
                    "radial-gradient(ellipse 60% 55% at 32% 30%, #eef7ea 0%, #f8fbf7 70%), #f8fbf7",
            }}
        >
            <div
                style={{
                    position: "absolute",
                    left: 0,
                    top: 0,
                    width: stage.width,
                    height: stage.height,
                    transformOrigin: "0 0",
                    transform: cameraTransform(frame),
                }}
            >
                <Intro />
                <Network />
                <ConversationPanel />
            </div>
            <LoopCaptions />
            <BenefitCaptions />
            <EndCard />
        </AbsoluteFill>
    );
};
