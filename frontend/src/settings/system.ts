import { escapeHtml as e, icon } from "../work/presentation.js";

type Usage = {
    total: number;
    used: number | null;
    available: number | null;
    percent: number | null;
};
type Machine = {
    observedAt: string;
    name: string;
    environment: string;
    operatingSystem: string;
    uptimeSeconds: number | null;
    cpu: Usage;
    memory: Usage;
    disk: Usage;
    warnings: string[];
    services:
        | {
              name: string;
              namespace: string;
              phase: string;
              ready: boolean;
              restarts: number;
              cpuCores: number | null;
              memoryBytes: number | null;
          }[]
        | null;
};
type Overview = {
    status: "loading" | "unsupported" | "unavailable" | "stale" | "live";
    notice: string | null;
    machine: Machine | null;
    history: { at: string; cpu: number | null; memory: number | null }[];
};
const percent = (value: number | null) =>
    value === null ? "—" : `${Math.round(value)}%`;
const cores = (value: number | null) =>
    value === null
        ? "—"
        : value.toLocaleString(undefined, { maximumFractionDigits: 2 });
function bytes(value: number | null) {
    if (value === null) return "—";
    const unit = value >= 1024 ** 4 ? 4 : value >= 1024 ** 3 ? 3 : 2;
    return `${(value / 1024 ** unit).toLocaleString(undefined, { maximumFractionDigits: 1 })} ${["", "", "MiB", "GiB", "TiB"][unit]}`;
}
function uptime(seconds: number | null) {
    if (seconds === null) return "Uptime unavailable";
    const hours = Math.floor(seconds / 3600),
        days = Math.floor(hours / 24);
    return `Up ${days ? `${days}d ` : ""}${hours % 24}h ${Math.floor(seconds / 60) % 60}m`;
}

export class SystemResources {
    private overview: Overview | null = null;
    private lastFetch = 0;
    private pending?: AbortController;
    private panel?: HTMLElement;
    reset() {
        this.pending?.abort();
        this.pending = undefined;
        this.overview = null;
        this.lastFetch = 0;
        this.panel = undefined;
    }
    async refresh() {
        if (this.pending || Date.now() - this.lastFetch < 15_000) return;
        const controller = new AbortController();
        this.pending = controller;
        this.lastFetch = Date.now();
        try {
            const response = await fetch("/api/system", {
                cache: "no-store",
                signal: AbortSignal.any([
                    controller.signal,
                    AbortSignal.timeout(6000),
                ]),
            });
            if (response.status === 401) {
                this.overview = null;
                window.dispatchEvent(new Event("goblin-workspace-locked"));
                return;
            }
            if (!response.ok) throw new Error();
            const result = (await response.json()) as Overview;
            if (!result.status) throw new Error();
            if (!controller.signal.aborted) this.overview = result;
        } catch {
            if (controller.signal.aborted) return;
            this.overview = {
                status: this.overview?.machine ? "stale" : "unavailable",
                notice: "System updates are unavailable. Last readings may be out of date.",
                machine: this.overview?.machine ?? null,
                history: this.overview?.history ?? [],
            };
        } finally {
            if (this.pending === controller) this.pending = undefined;
            if (!controller.signal.aborted) this.update();
        }
    }
    summary() {
        const state = this.overview,
            machine = state?.machine;
        const live = state?.status === "live";
        const text =
            live && machine
                ? `CPU ${percent(machine.cpu.percent)} · Memory ${percent(machine.memory.percent)}`
                : state?.status === "stale"
                  ? "Readings out of date"
                  : state?.status === "unsupported"
                    ? "View machine resources"
                    : state?.status === "unavailable"
                      ? "Metrics unavailable"
                      : "Reading resources…";
        const warning = live && !!machine?.warnings.length;
        return `${icon("activity")}<span><strong>System<span class="system-dot ${warning ? "warning" : live ? "live" : ""}"></span></strong><small>${e(text)}</small></span>${icon("chevron")}`;
    }
    mount(panel: HTMLElement) {
        this.panel = panel;
        this.update();
        void this.refresh();
    }
    private update() {
        document
            .querySelectorAll<HTMLElement>("[data-system-summary]")
            .forEach((element) => (element.innerHTML = this.summary()));
        if (!this.panel?.isConnected) return;
        const state = this.overview,
            machine = state?.machine;
        this.panel.innerHTML = `<div class="system-heading"><h3>Your system</h3><span class="system-live-label">${state?.status === "live" ? "Updates every 15s" : state?.status === "stale" ? "Updates interrupted" : "Machine overview"}</span></div><p class="settings-description">The machine behind your work. Usage includes Goblin, its agents, and other processes on this VM.</p>${state?.notice ? `<p class="settings-notice" role="status">${e(state.notice)}</p>` : ""}${machine ? this.machine(machine) : state ? "" : '<p class="settings-description" role="status">Reading your machine’s resources…</p>'}`;
    }
    private machine(machine: Machine) {
        const live = this.overview?.status === "live";
        const resource = (title: string, value: Usage, isCpu = false) => {
            const format = isCpu ? cores : bytes;
            return `<article class="system-resource"><h4>${title}</h4><strong>${percent(value.percent)}</strong><progress max="100" value="${value.percent ?? 0}" aria-label="${title} used" ${value.percent === null ? "hidden" : ""}></progress><p>${e(format(value.used))} / ${e(format(value.total))}${isCpu ? " cores" : ""}</p><small>${e(format(value.available))}${isCpu ? " cores idle" : " available"}</small></article>`;
        };
        return `<div class="system-machine"><div><strong>${e(machine.name)}</strong><span>${e(machine.environment)} · ${e(machine.operatingSystem)}</span></div><span>${e(uptime(machine.uptimeSeconds))}</span></div>
            <div class="system-health ${!live || machine.warnings.length ? "warning" : ""}" role="status">${icon(!live || machine.warnings.length ? "wait" : "circleCheck")}<div>${!live ? "Current health is unknown. Showing the last observation." : machine.warnings.length ? machine.warnings.map((warning) => `<p>${e(warning)}</p>`).join("") : "No resource warnings"}</div></div>
            <div class="system-resources">${resource("CPU", machine.cpu, true)}${resource("Memory", machine.memory)}${resource("Disk", machine.disk)}</div>
            <p class="system-footnote">Memory shows the active working-set estimate. Disk availability excludes reserved space.</p>
            ${this.chart()}
            <div class="system-section-heading"><h4>Services & agent sandboxes</h4><span>${machine.services ? `${machine.services.filter((s) => s.ready).length} / ${machine.services.length} ready` : "Unavailable"}</span></div>
            ${machine.services?.length ? `<div class="system-services" role="list" aria-label="Services and agent sandboxes">${machine.services.map((service) => `<div class="system-service" role="listitem"><div><strong>${e(service.name)}</strong><small>${service.namespace === "goblin-executions" ? "Agent sandbox" : e(service.namespace)}${service.restarts ? ` · ${service.restarts} restart${service.restarts === 1 ? "" : "s"}` : ""}</small></div><div><span class="system-service-state ${service.ready ? "" : "warning"}">${service.ready ? "Ready" : e(service.phase === "Running" ? "Not ready" : service.phase || "Unknown")}</span><small>${e(cores(service.cpuCores))} CPU · ${e(bytes(service.memoryBytes))}</small></div></div>`).join("")}</div>` : `<p class="settings-description">${machine.services ? "No active Goblin services on this machine." : "Service health is temporarily unavailable."}</p>`}
            <p class="system-footnote">Last reading: ${e(new Date(machine.observedAt).toLocaleTimeString())}. Completed pods are excluded.</p>
            ${machine.environment === "WSL" ? '<p class="system-footnote">These are your WSL VM’s resources. Windows memory and the physical disk’s remaining space can impose additional limits.</p>' : '<p class="system-footnote">Disk shows the VM’s root filesystem. Separately mounted data disks are not included.</p>'}
            <button class="system-cluster-link" data-open-system-cluster>Open detailed cluster view ${icon("arrow")}</button>`;
    }
    private chart() {
        const history = this.overview?.history ?? [];
        if (history.length < 2)
            return '<div class="system-chart-empty">Collecting recent CPU and memory usage…</div>';
        const end = new Date(history.at(-1)!.at).getTime(),
            start = end - 300_000;
        const path = (key: "cpu" | "memory") => {
            let previous = 0;
            return history
                .map((sample) => {
                    const at = new Date(sample.at).getTime(),
                        value = sample[key];
                    if (value === null) {
                        previous = 0;
                        return "";
                    }
                    const x =
                        32 +
                        Math.max(0, Math.min(1, (at - start) / 300_000)) * 468;
                    const y = 108 - Math.max(0, Math.min(100, value)) * 0.96;
                    const command =
                        !previous || at - previous > 35_000 ? "M" : "L";
                    previous = at;
                    return `${command}${x.toFixed(1)},${y.toFixed(1)}`;
                })
                .join(" ");
        };
        return `<div class="system-section-heading"><h4>Recent usage</h4><span class="system-chart-legend"><i class="cpu"></i>CPU<i class="memory"></i>Memory</span></div><svg class="system-chart" viewBox="0 0 510 134" role="img" aria-label="CPU and memory usage over the last five minutes, from zero to one hundred percent"><path class="grid" d="M32 12H500M32 60H500M32 108H500"/><text x="0" y="16">100</text><text x="6" y="64">50</text><text x="12" y="112">0</text><path class="cpu" d="${path("cpu")}"/><path class="memory" d="${path("memory")}"/><text x="32" y="131">5 min ago</text><text x="476" y="131">${this.overview?.status === "live" ? "Now" : "Last"}</text></svg><p class="system-footnote">Recent history is kept while Goblin is running and clears on restart.</p>`;
    }
}
