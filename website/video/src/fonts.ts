import { loadFont } from "@remotion/fonts";
import { staticFile } from "remotion";

export const fontFamily = "Inter";

// The website ships Inter medium for weights 400–500 and semibold for 600–800.
export const fontsLoaded = Promise.all([
    loadFont({
        family: fontFamily,
        url: staticFile("inter-18pt-medium.ttf"),
        weight: "400 500",
        format: "truetype",
    }),
    loadFont({
        family: fontFamily,
        url: staticFile("inter-18pt-semibold.ttf"),
        weight: "600 800",
        format: "truetype",
    }),
]);
