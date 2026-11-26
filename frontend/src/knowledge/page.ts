import { icon } from "../work/presentation.js";

// Repository setup memory is scoped to execution grants. There is no public
// knowledge index/search API; do not turn Work history or connected tools into
// simulated knowledge sources, counts, sync times, or search results.
export function renderKnowledge() {
    return `<section class="directory-page knowledge-page" aria-labelledby="knowledge-title" data-scroll="knowledge">
        <header class="directory-heading"><h1 id="knowledge-title" tabindex="-1">Knowledge</h1></header>
        <label class="directory-search knowledge-search">${icon("search")}<input id="knowledge-search" type="search" placeholder="Search indexed knowledge…" aria-label="Search indexed knowledge" aria-describedby="knowledge-availability" disabled></label>
        <p id="knowledge-availability" class="directory-hint">Indexing unavailable</p>
        <section class="knowledge-sources" aria-labelledby="knowledge-sources-title"><header><h2 id="knowledge-sources-title">Sources</h2></header>
            <div class="directory-empty">${icon("knowledge")}<h2>No indexed sources</h2><p>Connecting a tool doesn’t index its content.</p><a class="secondary" href="/settings?section=connections" data-action="settings">${icon("integrations")}Manage connections</a></div>
        </section>
        <footer class="directory-footer"><button data-action="browse-work">View work${icon("arrow")}</button></footer>
    </section>`;
}
