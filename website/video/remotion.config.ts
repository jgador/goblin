import { Config } from "@remotion/cli/config";

// Reuse the website's fonts and artwork instead of keeping copies.
Config.setPublicDir("../assets");
Config.setEntryPoint("./src/index.ts");
Config.setVideoImageFormat("jpeg");
Config.setJpegQuality(95);
Config.setOverwriteOutput(true);

// Remotion reads tsconfig.json through the TypeScript JS API, which TypeScript 7 does not
// expose, so give its esbuild loader the automatic JSX runtime directly.
Config.overrideWebpackConfig((config) => ({
    ...config,
    module: {
        ...config.module,
        rules: config.module?.rules?.map((rule) => {
            if (!rule || typeof rule !== "object" || !Array.isArray(rule.use))
                return rule;
            return {
                ...rule,
                use: rule.use.map((entry) =>
                    entry &&
                    typeof entry === "object" &&
                    typeof entry.loader === "string" &&
                    entry.loader.includes("esbuild-loader") &&
                    typeof entry.options === "object"
                        ? {
                              ...entry,
                              options: { ...entry.options, jsx: "automatic" },
                          }
                        : entry,
                ),
            };
        }),
    },
}));
