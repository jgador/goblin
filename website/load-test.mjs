import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { performance } from "node:perf_hooks";
import { setTimeout as delay } from "node:timers/promises";
import { promisify } from "node:util";
import { chromium } from "@playwright/test";

// Run on the same Linux host as the single-node k3s installation. Only the
// website container's cgroup is measured, never the browser or the whole node.
const run = promisify(execFile);
const repo = resolve(import.meta.dirname, "..");
const args = process.argv.slice(2);
if (args.includes("--help")) {
  console.log("Usage: node website/load-test.mjs [--visits 10] [--idle-seconds 60]");
  console.log("One fresh browser visit every 60 seconds; each stays for 20 seconds.");
  process.exit(0);
}
const options = { "--visits": 10, "--idle-seconds": 60 };
for (let i = 0; i < args.length; i += 2) {
  assert.ok(Object.hasOwn(options, args[i]), `Unknown option: ${args[i]}`);
  const value = Number(args[i + 1]);
  assert.ok(Number.isInteger(value) && value > 0, `${args[i]} requires a positive integer`);
  options[args[i]] = value;
}
const visitCount = options["--visits"];
const idleSeconds = options["--idle-seconds"];
assert.ok(idleSeconds >= 5, "Idle baseline must be at least five seconds");
const intervalSeconds = 60;
const dwellSeconds = 20;
const sampleMilliseconds = 250;
const output = resolve(repo, ".artifacts/website/load-test", new Date().toISOString().replaceAll(":", "-"));
await mkdir(output, { recursive: true });
const kubectl = ["kubectl", "--kubeconfig", "/etc/rancher/k3s/k3s.yaml", "-n", "goblin"];
async function command(args) {
  return (await run("k3s", args, { timeout: 15_000, maxBuffer: 4 * 1024 * 1024 })).stdout;
}
async function websitePod() {
  const list = JSON.parse(await command([...kubectl, "get", "pods", "-l", "app.kubernetes.io/name=website", "-o", "json"]));
  assert.equal(list.items.length, 1, "Measurement requires exactly one website pod and no rollout in progress");
  const pod = list.items[0];
  assert.ok(!pod.metadata.deletionTimestamp, "Website pod is terminating");
  assert.equal(pod.spec.containers.length, 1, "Measurement requires the website container without sidecars");
  assert.equal(pod.spec.containers[0].name, "website", "Measurement requires the website container");
  const container = pod.status.containerStatuses.find(item => item.name === "website");
  assert.ok(container?.ready && container.state.running, "Website container must be ready and running");
  return { pod, container };
}
function fields(text) {
  return Object.fromEntries(text.trim().split("\n").map(line => {
    const [key, value] = line.trim().split(/\s+/);
    return [key, Number(value)];
  }));
}
function percentile(values, fraction) {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.max(0, Math.ceil(sorted.length * fraction) - 1)];
}
const { pod, container } = await websitePod();
const id = container.containerID.replace(/^containerd:\/\//, "");
assert.match(id, /^[a-f0-9]+$/);
const inspected = JSON.parse(await command(["crictl", "inspect", id]));
const membership = await readFile(`/proc/${inspected.info.pid}/cgroup`, "utf8");
const cgroupPath = membership.split("\n").find(line => line.startsWith("0::"))?.slice(3);
assert.ok(cgroupPath?.includes(id), "Expected the website container's cgroup v2 path");
const cgroup = resolve("/sys/fs/cgroup", `.${cgroupPath}`);
const service = JSON.parse(await command([...kubectl, "get", "service", "website", "-o", "json"]));
assert.equal(service.spec.selector["app.kubernetes.io/name"], "website", "Traffic must target only website pods");
const origin = new URL(`http://${service.spec.clusterIP}:${service.spec.ports[0].port}`).origin;
const startedAt = new Date().toISOString();
const start = performance.now();
const samples = [];
const visits = [];
const failures = [];
let phase = "idle";
let sampling = true;
let samplingError;
let browser;
async function sample() {
  const names = ["cpu.stat", "memory.current", "memory.stat", "memory.peak", "memory.events"];
  const values = await Promise.all(names.map(name => readFile(`${cgroup}/${name}`, "utf8")));
  const cpu = fields(values[0]);
  const memory = Number(values[1]);
  const stat = fields(values[2]);
  samples.push({
    elapsedSeconds: (performance.now() - start) / 1000,
    phase,
    cpuUsec: cpu.usage_usec,
    throttledUsec: cpu.throttled_usec,
    throttledPeriods: cpu.nr_throttled,
    memoryBytes: memory,
    workingSetBytes: Math.max(0, memory - stat.inactive_file),
    anonymousBytes: stat.anon,
    containerLifetimeMemoryPeakBytes: Number(values[3]),
    memoryEvents: fields(values[4]),
  });
}
const sampler = (async () => {
  while (sampling) {
    await sample();
    await delay(sampleMilliseconds);
  }
})().catch(error => { samplingError = error; });

async function visit(index, scheduledStart) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, serviceWorkers: "block" });
  const page = await context.newPage();
  // CDP disables cache explicitly; a fresh context also isolates cookies/storage.
  const session = await context.newCDPSession(page);
  await session.send("Network.enable");
  await session.send("Network.setCacheDisabled", { cacheDisabled: true });
  const requests = [];
  page.on("requestfinished", request => requests.push((async () => {
    const response = await request.response();
    const sizes = await request.sizes();
    return { path: new URL(request.url()).pathname, status: response.status(), bytes: sizes.responseBodySize + sizes.responseHeadersSize };
  })()));
  page.on("requestfailed", request => failures.push(`Visit ${index}: ${request.url()} ${request.failure()?.errorText}`));
  page.on("pageerror", error => failures.push(`Visit ${index}: ${error.message}`));
  // Reject unexpected external traffic instead of sending it during a test.
  await context.route("**/*", route => {
    if (new URL(route.request().url()).origin === origin) return route.continue();
    failures.push(`Visit ${index}: unexpected external request`);
    return route.abort();
  });
  const visitStart = performance.now();
  assert.ok(visitStart - scheduledStart < 5000, "Visit missed its scheduled start by more than five seconds");
  try {
    const response = await page.goto(origin, { waitUntil: "networkidle", timeout: 15_000 });
    assert.equal(response.status(), 200);
    await page.evaluate(() => document.fonts.ready);
    const pageLoadMs = await page.evaluate(() => performance.getEntriesByType("navigation")[0].loadEventEnd);
    assert.equal(await page.locator(".channel").count(), 5);
    await delay(Math.max(0, dwellSeconds * 1000 - (performance.now() - visitStart)));
    const responses = await Promise.all(requests);
    for (const item of responses) assert.equal(item.status, 200, `Unexpected status for ${item.path}`);
    assert.ok(responses.some(item => item.path.endsWith(".css")), "CSS was not fetched");
    assert.ok(responses.some(item => item.path.endsWith(".js")), "JavaScript was not fetched");
    assert.ok(responses.some(item => item.path.endsWith(".ttf")), "Fonts were not fetched");
    assert.ok(responses.some(item => item.path.endsWith(".svg")), "Images were not fetched");
    const result = { index, startedAtSeconds: (visitStart - start) / 1000, pageLoadMs, requests: responses.length, transferredBytes: responses.reduce((sum, item) => sum + item.bytes, 0), responses };
    visits.push(result);
    console.log(`Visit ${index}/${visitCount}: ${result.requests} HTTP requests, ${(result.transferredBytes / 1024).toFixed(1)} KiB, load ${pageLoadMs.toFixed(1)} ms`);
  } finally {
    await context.close();
  }
}

function summarize(selected) {
  const first = selected[0];
  const last = selected.at(-1);
  const elapsed = last.elapsedSeconds - first.elapsedSeconds;
  const cpuIntervals = selected.slice(1).map((item, i) => (item.cpuUsec - selected[i].cpuUsec) / 1e6 / (item.elapsedSeconds - selected[i].elapsedSeconds));
  // About one-second windows reduce scheduler quantization in individual samples.
  const cpuOneSecond = selected.slice(4).map((item, i) => (item.cpuUsec - selected[i].cpuUsec) / 1e6 / (item.elapsedSeconds - selected[i].elapsedSeconds));
  return {
    elapsedSeconds: elapsed,
    sampleCount: selected.length,
    averageVcpu: (last.cpuUsec - first.cpuUsec) / 1e6 / elapsed,
    peakSampleVcpu: Math.max(...cpuIntervals),
    peakOneSecondVcpu: Math.max(...cpuOneSecond),
    p95OneSecondVcpu: percentile(cpuOneSecond, 0.95),
    averageWorkingSetMiB: selected.reduce((sum, item) => sum + item.workingSetBytes, 0) / selected.length / 2 ** 20,
    peakWorkingSetMiB: Math.max(...selected.map(item => item.workingSetBytes)) / 2 ** 20,
    peakMemoryCurrentMiB: Math.max(...selected.map(item => item.memoryBytes)) / 2 ** 20,
    containerLifetimeMemoryPeakMiB: last.containerLifetimeMemoryPeakBytes / 2 ** 20,
    throttledSeconds: (last.throttledUsec - first.throttledUsec) / 1e6,
    throttledPeriods: last.throttledPeriods - first.throttledPeriods,
    oomKills: last.memoryEvents.oom_kill - first.memoryEvents.oom_kill,
    memoryLimitEvents: last.memoryEvents.max - first.memoryEvents.max,
  };
}

try {
  console.log(`Output: ${output}`);
  console.log(`Measuring ${pod.metadata.name}/website; ${idleSeconds}s idle + ${visitCount} minutes at one new visitor/minute.`);
  browser = await chromium.launch({ headless: true });
  await delay(Math.max(0, idleSeconds * 1000 - (performance.now() - start)));
  phase = "load";
  await sample();
  const loadStart = performance.now();
  for (let i = 0; i < visitCount; i++) {
    const scheduledStart = loadStart + i * intervalSeconds * 1000;
    await delay(Math.max(0, scheduledStart - performance.now()));
    if (samplingError) throw samplingError;
    const current = await websitePod();
    assert.equal(current.container.containerID, container.containerID, "Website container changed during measurement");
    assert.equal(current.container.restartCount, container.restartCount, "Website container restarted during measurement");
    await visit(i + 1, scheduledStart);
  }
  await delay(Math.max(0, loadStart + visitCount * intervalSeconds * 1000 - performance.now()));
  await sample();
  if (samplingError) throw samplingError;
  const current = await websitePod();
  assert.equal(current.container.containerID, container.containerID, "Website container changed during measurement");
  assert.equal(current.container.restartCount, container.restartCount, "Website container restarted during measurement");
  assert.deepEqual(failures, []);
  const report = {
    startedAt, completedAt: new Date().toISOString(),
    pod: pod.metadata.name, container: "website", image: pod.spec.containers[0].image,
    resources: pod.spec.containers[0].resources,
    workload: { visitsPerMinute: 1, visitCount, intervalSeconds, dwellSeconds, idleSeconds, sampleMilliseconds, browserCache: "disabled", target: "local Kubernetes ClusterIP HTTP service" },
    beforeMeasurement: {
      note: "Cumulative container counters before the idle baseline, including startup; not part of the measured traffic window.",
      cpuSeconds: samples[0].cpuUsec / 1e6,
      throttledSeconds: samples[0].throttledUsec / 1e6,
      throttledPeriods: samples[0].throttledPeriods,
      memoryLimitEvents: samples[0].memoryEvents.max,
      oomKills: samples[0].memoryEvents.oom_kill,
    },
    idle: summarize(samples.filter(item => item.phase === "idle")),
    load: summarize(samples.filter(item => item.phase === "load")),
    http: { requests: visits.reduce((sum, item) => sum + item.requests, 0), transferredBytes: visits.reduce((sum, item) => sum + item.transferredBytes, 0), medianPageLoadMs: percentile(visits.map(item => item.pageLoadMs), 0.5), p95PageLoadMs: percentile(visits.map(item => item.pageLoadMs), 0.95), failures: failures.length },
    visits,
  };
  await writeFile(resolve(output, "summary.json"), JSON.stringify(report, null, 2) + "\n");
  console.log(JSON.stringify({ beforeMeasurement: report.beforeMeasurement, idle: report.idle, load: report.load, http: report.http }, null, 2));
} catch (error) {
  await writeFile(resolve(output, "failure.txt"), `${error.stack}\n`);
  throw error;
} finally {
  sampling = false;
  await sampler;
  await writeFile(resolve(output, "samples.json"), JSON.stringify(samples) + "\n");
  await writeFile(resolve(output, "visits.json"), JSON.stringify(visits, null, 2) + "\n");
  await browser?.close();
}
