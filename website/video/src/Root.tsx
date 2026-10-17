import { Composition } from "remotion";
import { GoblinLogo, logoClip } from "./GoblinLogo";
import { GoblinPromo } from "./GoblinPromo";
import "./fonts";
import { durationInFrames, fps, height, width } from "./timeline";

export const Root = () => (
    <>
        <Composition
            id="GoblinPromo"
            component={GoblinPromo}
            durationInFrames={durationInFrames}
            fps={fps}
            width={width}
            height={height}
        />
        <Composition
            id="GoblinLogo"
            component={GoblinLogo}
            durationInFrames={logoClip.durationInFrames}
            fps={fps}
            width={logoClip.size}
            height={logoClip.size}
        />
    </>
);
