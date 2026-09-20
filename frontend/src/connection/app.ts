import { mountCodex } from "./codex.js";
await mountCodex(document.body, () => {
    if (new URLSearchParams(location.search).get("returnTo") !== "headlamp")
        return false;
    location.replace("/headlamp/");
    return true;
});
