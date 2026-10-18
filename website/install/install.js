const version = document.querySelector("#version");
const status = document.querySelector("#status");
const deploy = document.querySelector("#deploy");
const notes = document.querySelector("#notes");
const template = document.querySelector("#template");
const links = document.querySelector("#release-links");
const retry = document.querySelector("#retry");
const picker = document.querySelector("#version-picker");
const value = document.querySelector("#version-value");
const badge = document.querySelector("#version-badge");
const menu = document.querySelector("#version-options");
let releases = [];
let recommended = null;
let activeIndex = -1;
let search = "";
let searchTime = 0;
const pattern =
    /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-preview\.[1-9]\d*)?$/;
// Also resolves correctly when a local preview opens index.html explicitly.
const directory = new URL(".", location.href);
// Provider-specific catalogs and assets keep each installation path independent.
const azureDirectory = new URL("azure/", directory);
function closeMenu() {
    menu.hidden = true;
    version.setAttribute("aria-expanded", "false");
    version.removeAttribute("aria-activedescendant");
    search = "";
}
function highlight(index) {
    activeIndex = index;
    for (const [i, option] of [...menu.children].entries()) {
        option.dataset.active = String(i === index);
    }
    const option = menu.children[index];
    if (option) {
        version.setAttribute("aria-activedescendant", option.id);
        // Scroll the options without moving the page or animating keyboard input.
        if (option.offsetTop < menu.scrollTop)
            menu.scrollTop = option.offsetTop;
        else if (
            option.offsetTop + option.offsetHeight >
            menu.scrollTop + menu.clientHeight
        )
            menu.scrollTop =
                option.offsetTop + option.offsetHeight - menu.clientHeight;
    }
}
function openMenu(index) {
    if (version.disabled) return;
    const bounds = version.getBoundingClientRect();
    const height = Math.min(
        menu.scrollHeight || releases.length * 74 + 10,
        320,
        innerHeight * 0.45,
    );
    picker.dataset.side =
        innerHeight - bounds.bottom < height + 12 &&
        bounds.top > innerHeight - bounds.bottom
            ? "above"
            : "below";
    menu.hidden = false;
    version.setAttribute("aria-expanded", "true");
    const selected = releases.findIndex(
        (entry) => entry.version === version.value,
    );
    highlight(index ?? (selected >= 0 ? selected : 0));
}
function displaySelection(selected, placeholder = "Choose a version") {
    version.value = selected;
    value.textContent = selected || placeholder;
    badge.hidden = !selected || selected !== recommended;
    for (const [i, option] of [...menu.children].entries()) {
        option.setAttribute(
            "aria-selected",
            String(releases[i].version === selected),
        );
    }
}
function selectRelease(index) {
    displaySelection(releases[index].version);
    closeMenu();
    choose();
    version.focus({ preventScroll: true });
}
function renderOptions() {
    menu.replaceChildren();
    for (const [index, entry] of releases.entries()) {
        const option = document.createElement("div");
        option.className = "version-option";
        option.id = `version-option-${index}`;
        option.setAttribute("role", "option");
        option.setAttribute("aria-selected", "false");
        const copy = document.createElement("span");
        copy.className = "version-option-copy";
        const title = document.createElement("span");
        title.className = "version-option-title";
        title.textContent = entry.version;
        if (entry.version === recommended) {
            const tag = document.createElement("span");
            tag.className = "version-badge";
            tag.textContent = "Recommended";
            title.append(tag);
        }
        const description = document.createElement("span");
        description.className = "version-option-description";
        description.textContent = entry.version.includes("-preview.")
            ? "Preview release"
            : "Stable release";
        const check = document.createElement("span");
        check.className = "version-check";
        check.textContent = "✓";
        check.setAttribute("aria-hidden", "true");
        copy.append(title, description);
        option.append(copy, check);
        option.addEventListener("pointermove", (event) => {
            if (event.pointerType === "mouse" && activeIndex !== index)
                highlight(index);
        });
        // Keep combobox focus while a pointer chooses an option.
        option.addEventListener("pointerdown", (event) => {
            event.preventDefault();
        });
        option.addEventListener("click", () => selectRelease(index));
        menu.append(option);
    }
}
version.addEventListener("pointerdown", () => {
    picker.dataset.motion = "pointer";
});
version.addEventListener("click", (event) => {
    if (event.detail === 0) picker.dataset.motion = "instant";
    if (menu.hidden) openMenu();
    else closeMenu();
});
version.addEventListener("keydown", (event) => {
    picker.dataset.motion = "instant";
    if (event.key === "Tab" || event.key === "Escape") {
        if (event.key === "Escape" && !menu.hidden) event.preventDefault();
        closeMenu();
        return;
    }
    if (version.disabled) return;
    if (
        ["ArrowDown", "ArrowUp", "Home", "End", "Enter", " "].includes(
            event.key,
        )
    ) {
        event.preventDefault();
        if (menu.hidden)
            openMenu(
                event.key === "End" || event.key === "ArrowUp"
                    ? releases.length - 1
                    : event.key === "Home"
                      ? 0
                      : undefined,
            );
        else if (event.key === "Enter" || event.key === " ")
            selectRelease(activeIndex);
        else if (event.key === "Home") highlight(0);
        else if (event.key === "End") highlight(releases.length - 1);
        else
            highlight(
                Math.max(
                    0,
                    Math.min(
                        releases.length - 1,
                        activeIndex + (event.key === "ArrowDown" ? 1 : -1),
                    ),
                ),
            );
    } else if (
        event.key.length === 1 &&
        !event.ctrlKey &&
        !event.metaKey &&
        !event.altKey
    ) {
        event.preventDefault();
        const now = performance.now();
        search = now - searchTime > 700 ? event.key : search + event.key;
        searchTime = now;
        const match = releases.findIndex((entry) =>
            entry.version.startsWith(search),
        );
        if (match >= 0) {
            if (menu.hidden) openMenu(match);
            else highlight(match);
        }
    }
});
document.addEventListener("pointerdown", (event) => {
    if (!picker.contains(event.target)) closeMenu();
});
version.addEventListener("blur", closeMenu);
window.addEventListener("resize", () => {
    if (!menu.hidden) {
        picker.dataset.motion = "instant";
        openMenu(activeIndex);
    }
});
function clearSelection() {
    deploy.removeAttribute("href");
    deploy.setAttribute("aria-disabled", "true");
    links.hidden = true;
    notes.removeAttribute("href");
    template.removeAttribute("href");
}
function choose() {
    const selected = version.value;
    clearSelection();
    if (!selected) {
        status.textContent = "Choose a Goblin version to continue.";
        return;
    }
    const base = new URL(`versions/${selected}/`, azureDirectory);
    deploy.href = `https://portal.azure.com/#create/Microsoft.Template/uri/${encodeURIComponent(new URL("azuredeploy.portal.json", base).href)}/createUIDefinitionUri/${encodeURIComponent(new URL("createUiDefinition.json", base).href)}`;
    deploy.removeAttribute("aria-disabled");
    notes.href = `https://github.com/jgador/goblin/releases/tag/goblin-v${selected}`;
    template.href = new URL("azuredeploy.json", base).href;
    links.hidden = false;
    status.textContent = selected.includes("-preview.")
        ? "Preview release. Features and setup may change."
        : "Stable release.";
}
async function load() {
    closeMenu();
    clearSelection();
    version.disabled = true;
    releases = [];
    recommended = null;
    menu.replaceChildren();
    displaySelection("", "Loading releases…");
    status.textContent = "Loading available versions.";
    retry.hidden = true;
    try {
        const response = await fetch(new URL("releases.json", azureDirectory), {
            cache: "no-store",
            signal: AbortSignal.timeout(15000),
        });
        if (!response.ok) throw new Error("Unavailable");
        const catalog = await response.json();
        if (
            catalog?.schemaVersion !== 1 ||
            !Array.isArray(catalog.releases) ||
            !catalog.releases.every(
                (entry) =>
                    typeof entry?.version === "string" &&
                    pattern.test(entry.version),
            ) ||
            new Set(catalog.releases.map((entry) => entry.version)).size !==
                catalog.releases.length ||
            !(
                catalog.recommended === null ||
                catalog.releases.some(
                    (entry) => entry.version === catalog.recommended,
                )
            )
        )
            throw new Error("Invalid catalog");
        releases = catalog.releases;
        recommended = catalog.recommended;
        renderOptions();
        const requested = new URL(location.href).searchParams.get("version");
        const selected = requested ?? catalog.recommended;
        displaySelection(
            catalog.releases.some((entry) => entry.version === selected)
                ? selected
                : "",
        );
        version.disabled = catalog.releases.length === 0;
        choose();
        if (!catalog.releases.length) {
            displaySelection("", "No releases yet");
            status.textContent =
                "The first Goblin release is being prepared. Check back soon.";
        } else if (requested !== null && version.value !== requested) {
            status.textContent =
                "That version is unavailable. Choose a published version.";
        }
    } catch {
        status.textContent =
            "Could not load Goblin releases. Try again in a moment.";
        displaySelection("", "Releases unavailable");
        retry.hidden = false;
    }
}
retry.addEventListener("click", load);
load();
