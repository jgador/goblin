const icons: Record<string, string> = {
    sidebar:
        '<rect x="3" y="4" width="18" height="16" rx="3"/><path d="M9 4v16"/>',
    search: '<circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 5 5"/>',
    lock: '<rect x="5" y="10" width="14" height="11" rx="2"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/>',
    close: '<path d="m6 6 12 12M6 18 18 6"/>',
    settings:
        '<path d="m9 3-.6 2.1-2 .9-2-.6-2 3.5 1.5 1.6v2.3L2.4 14l2 3.5 2-.5 2 1L9 20h4l.6-2.1 2-.9 2 .6 2-3.5-1.5-1.6v-2.3L19.6 8l-2-3.5-2 .5-2-1L13 3Z"/><circle cx="11" cy="11.5" r="3"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    chat: '<path d="M21 11.5a8.5 8.5 0 0 1-8.5 8.5H4l-3 2V11.5A8.5 8.5 0 0 1 9.5 3H13a8 8 0 0 1 8 8.5Z"/><path d="M7 9h9M7 13h6"/>',
    work: '<rect x="4" y="5" width="16" height="16" rx="3"/><path d="M9 5V3h6v2M8 11l1 1 2-2M13 11h3M8 16h8"/>',
    arrow: '<path d="M5 12h14m-5-5 5 5-5 5"/>',
    up: '<path d="M12 19V5m-6 6 6-6 6 6"/>',
    check: '<path d="m5 12 4 4L19 6"/>',
    circleCheck: '<circle cx="12" cy="12" r="9"/><path d="m8 12 3 3 5-6"/>',
    wait: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5m0 4v.1"/>',
    clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    activity: '<path d="M3 12h4l3-7 4 14 3-7h4"/>',
    file: '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z"/><path d="M14 2v6h6M8 13h8M8 17h6"/>',
    refresh:
        '<path d="M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8"/><path d="M21 3v5h-5"/>',
    chevron: '<path d="m9 5 7 7-7 7"/>',
    back: '<path d="m14 5-7 7 7 7"/>',
    link: '<path d="m10 13 4-4M8 16l-1 1a4 4 0 0 1-6-6l5-5a4 4 0 0 1 6 0m4 2 1-1a4 4 0 0 1 6 6l-5 5a4 4 0 0 1-6 0" transform="translate(1 -1) scale(.9)"/>',
    pause: '<path d="M8 5v14M16 5v14"/>',
    spark: '<path d="m12 3 2.5 6.5L21 12l-6.5 2.5L12 21l-2.5-6.5L3 12l6.5-2.5Z"/>',
    branch: '<circle cx="6" cy="5" r="2"/><circle cx="18" cy="5" r="2"/><circle cx="6" cy="19" r="2"/><path d="M6 7v10M18 7v3a4 4 0 0 1-4 4H6"/>',
    book: '<path d="M12 5v16M12 5C9 2 4 3 2 4v15c3-1 7-1 10 2 3-3 7-3 10-2V4c-2-1-7-2-10 1Z"/>',
    inbox: '<path d="M4 3h16l2 12v6H2v-6Z"/><path d="M2 15h6l2 3h4l2-3h6"/>',
};
export function icon(name: string, cls = "") {
    return (
        '<svg class="' +
        cls +
        '" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
        (icons[name] || icons.work) +
        "</svg>"
    );
}
export function escapeHtml(s: unknown) {
    return String(s).replace(
        /[&<>"']/g,
        (c) =>
            (
                ({
                    "&": "&amp;",
                    "<": "&lt;",
                    ">": "&gt;",
                    '"': "&quot;",
                    "'": "&#39;",
                }) as Record<string, string>
            )[c],
    );
}
