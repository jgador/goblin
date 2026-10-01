// Resetting a workspace invalidates every read started under its old session.
export class ReadScope {
    private controller = new AbortController();

    get signal() {
        return this.controller.signal;
    }

    reset() {
        this.controller.abort();
        this.controller = new AbortController();
    }
}
