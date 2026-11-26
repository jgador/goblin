document.addEventListener(
    "pointerdown",
    () => document.documentElement.setAttribute("data-pointer-focus", ""),
    { capture: true, passive: true },
);

document.addEventListener(
    "keydown",
    (event) => {
        if (event.key === "Tab")
            document.documentElement.removeAttribute("data-pointer-focus");
    },
    { capture: true },
);
