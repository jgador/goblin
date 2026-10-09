import { icon } from "../work/presentation.js";

// Repository setup memory is scoped to execution grants. There is no public
// knowledge index/search API; do not turn Work history or connected tools into
// simulated knowledge sources, counts, sync times, or search results.
export function renderKnowledge() {
    return `<section class="directory-page knowledge-page" aria-labelledby="knowledge-title" data-scroll="knowledge">
        <header class="directory-heading"><div><h1 id="knowledge-title" tabindex="-1">Knowledge</h1><p>Persistent context Goblin can remember and retrieve.</p></div></header>
        <label class="directory-search knowledge-search">${icon("search")}<input id="knowledge-search" type="search" placeholder="Search indexed knowledge…" aria-label="Search indexed knowledge" aria-describedby="knowledge-availability" disabled></label>
        <p id="knowledge-availability" class="directory-hint">Knowledge search and source indexing are not available in this version of Goblin.</p>
        <section class="knowledge-sources" aria-labelledby="knowledge-sources-title"><header><h2 id="knowledge-sources-title">Sources</h2><span class="integration-status neutral">Indexing unavailable</span></header>
            <div class="directory-empty">${icon("knowledge")}<h2>No indexed sources</h2><p>Connecting a tool doesn’t index its content. There are no searchable sources, sync times, or record counts to show yet.</p><a class="secondary" href="/integrations" data-action="integrations">${icon("integrations")}View integrations</a></div>
        </section>
        <p class="directory-footnote">Your conversations, decisions, and results remain saved with each Work item. <button data-action="browse-work">View work${icon("arrow")}</button></p>
    </section>`;
}
