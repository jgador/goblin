export type DiffFile = {
    path: string;
    previousPath?: string;
    status?: "Added" | "Deleted" | "Renamed" | "Copied";
    added: number;
    removed: number;
    lines: string[];
};

export const hunkRange = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/;

const gitEscapes: Record<string, string> = {
    a: "\x07",
    b: "\b",
    f: "\f",
    n: "\n",
    r: "\r",
    t: "\t",
    v: "\v",
};
const decoder = new TextDecoder();

function gitPath(raw: string) {
    const quoted = /^"((?:\\.|[^"\\])*)"/.exec(raw);
    if (!quoted) return raw;
    return quoted[1].replace(/(?:\\[0-7]{3})+|\\./g, (escape) =>
        escape.length === 2
            ? (gitEscapes[escape[1]] ?? escape[1])
            : decoder.decode(
                  Uint8Array.from(escape.slice(1).split("\\"), (octal) =>
                      Number.parseInt(octal, 8),
                  ),
              ),
    );
}

function headerPath(header: string) {
    // Git leaves spaces unquoted. Find the separator that repeats the same
    // path, including when the path itself contains " b/".
    for (
        let i = header.indexOf(" b/");
        i >= 0;
        i = header.indexOf(" b/", i + 1)
    ) {
        const path = header.slice(2, i);
        if (path === header.slice(i + 3)) return path;
    }
    // Quoted paths can be read directly. Rename and copy metadata supply the
    // destination when the two header paths differ.
    return header.startsWith('"') ? gitPath(header).slice(2) : header.slice(2);
}

export function parseDiff(patch: string): DiffFile[] {
    // format-patch writes a mail header and diffstat before the Git diff.
    // A commit message can itself include the text "diff --git".
    const diffstat = /^---\n(?: [^\n]*\n)+\n(?=diff --git )/m.exec(patch);
    const lines = patch
        .slice(diffstat ? diffstat.index + diffstat[0].length : 0)
        .split("\n");
    if (lines.at(-1) === "") lines.pop();
    const files: DiffFile[] = [];
    let file: DiffFile | undefined;
    let inHunk = false;
    let binary = false;
    for (const line of lines) {
        if (line.startsWith("diff --git ")) {
            file = {
                path: headerPath(line.slice(11)),
                lines: [],
                added: 0,
                removed: 0,
            };
            files.push(file);
            inHunk = false;
            binary = false;
        } else if (file && !binary) {
            if (line.startsWith("@@ ")) inHunk = true;
            if (inHunk) {
                if (line.startsWith("+")) file.added++;
                if (line.startsWith("-")) file.removed++;
                file.lines.push(line);
            } else if (
                line.startsWith("+++ b/") ||
                line.startsWith('+++ "b/')
            ) {
                file.path = gitPath(line.slice(4).split("\t", 1)[0]).slice(2);
            } else if (
                line.startsWith("--- a/") ||
                line.startsWith('--- "a/')
            ) {
                if (file.status === "Deleted")
                    file.path = gitPath(line.slice(4).split("\t", 1)[0]).slice(
                        2,
                    );
            } else if (line.startsWith("new file mode ")) {
                file.status = "Added";
            } else if (line.startsWith("deleted file mode ")) {
                file.status = "Deleted";
            } else if (
                line.startsWith("rename from ") ||
                line.startsWith("copy from ")
            ) {
                file.status = line.startsWith("rename") ? "Renamed" : "Copied";
                file.previousPath = gitPath(
                    line.slice(line.indexOf("from ") + 5),
                );
            } else if (
                line.startsWith("rename to ") ||
                line.startsWith("copy to ")
            ) {
                file.path = gitPath(line.slice(line.indexOf("to ") + 3));
            } else if (
                line === "GIT binary patch" ||
                line.startsWith("Binary files ")
            ) {
                file.lines.push("Binary file changed.");
                binary = true;
            } else if (
                line.startsWith("old mode ") ||
                line.startsWith("new mode ")
            ) {
                file.lines.push(line);
            }
        }
    }
    return files;
}
