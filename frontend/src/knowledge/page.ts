import { icon } from "../work/presentation.js";

// Repository setup memory is scoped to execution grants. There is no public
// knowledge index/search API; do not turn Work history or connected tools into
// simulated knowledge sources, counts, sync times, or search results.
export function renderKnowledge() {
    return `<section class="directory-page knowledge-page" aria-labelledby="knowledge-title" data-scroll="knowledge">
        <header class="directory-heading"><h1 id="knowledge-title" tabindex="-1">Knowledge</h1></header>
        <section class="knowledge-sources" aria-labelledby="knowledge-availability">
            <div class="directory-empty">${icon("knowledge")}<h2 id="knowledge-availability">Knowledge indexing is unavailable in this deployment</h2></div>
        </section>
        <footer class="directory-footer"><button data-action="browse-work">Back to inbox${icon("arrow")}</button></footer>
    </section>`;
}
