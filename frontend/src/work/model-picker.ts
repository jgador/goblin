import { icon, escapeHtml as e } from "./presentation.js";
import { effortStops, effortLabel } from "./model-selection.js";
import type { ModelCatalog, ModelSelection } from "./model-selection.js";

export function renderModelPicker({
    choice,
    catalog,
    error,
    loading,
    connectionId,
    runtime,
    pickerOpen,
    listOpen,
}: {
    choice: Readonly<ModelSelection>;
    catalog?: ModelCatalog;
    error?: string;
    loading: boolean;
    connectionId: string;
    runtime?: string;
    pickerOpen: boolean;
    listOpen: boolean;
}) {
    const unsupported = runtime && runtime !== "codex";
    const models = (catalog?.models ?? []).slice(0, choice.expanded ? 10 : 3);
    const defaultName = models.find((m) => m.isDefault)?.displayName;
    const selectedModel =
        models.find((m) => m.model === choice.model) ??
        (choice.model ? undefined : models.find((m) => m.isDefault));
    const waitingForSelection =
        !!choice.model && !models.some((m) => m.model === choice.model);
    const modelName =
        selectedModel?.displayName ??
        (choice.model || (unsupported ? "Runtime default" : "Codex default"));
    const currentEffort =
        choice.effort || selectedModel?.defaultReasoningEffort || "";
    const currentEffortLabel = currentEffort
        ? effortLabel(currentEffort)
        : "Default";
    const activeIndex = Math.max(
        0,
        effortStops.findIndex((stop) => stop.value === currentEffort),
    );
    const supportedEfforts = new Set(
        selectedModel?.supportedReasoningEfforts ?? [],
    );
    const statusText = unsupported
        ? "This agent does not offer Codex model choices."
        : !connectionId
          ? "Connect Codex to choose a model."
          : (error ??
            (catalog?.refreshing
                ? "Checking for current models…"
                : catalog?.unavailable
                  ? "Model list unavailable. Codex default is available."
                  : catalog?.stale
                    ? "Showing cached models. The list may be out of date."
                    : !catalog
                      ? "Loading available models…"
                      : ""));
    const hint = "These options apply when you start or retry this work.";
    const status = `${statusText ? `<p class="model-status" role="status">${e(statusText)}</p>` : ""}${choice.notice ? `<p class="model-status" role="status">${e(choice.notice)}</p>` : ""}`;
    const modelRows = `<button type="button" class="model-option" data-action="select-model" data-value="" aria-pressed="${!choice.model}"><span>Codex default${defaultName ? `<small>${e(defaultName)}</small>` : ""}</span>${!choice.model ? icon("check") : ""}</button>${waitingForSelection ? `<span class="model-option model-option-pending">${e(choice.model)} · Checking availability</span>` : ""}${models
        .map(
            (model) =>
                `<button type="button" class="model-option" data-action="select-model" data-value="${e(model.model)}" aria-pressed="${choice.model === model.model}"><span>${e(model.displayName)}${model.isNew ? " <small>New</small>" : ""}</span>${choice.model === model.model ? icon("check") : ""}</button>`,
        )
        .join("")}`;
    const menu = `<div class="model-menu"><button type="button" class="model-menu-back" data-action="model-menu-back">${icon("back")}Models</button><div class="model-options" aria-label="Available models">${modelRows}</div>${catalog?.hasMore && !choice.expanded ? '<button type="button" class="text-button model-more" data-action="show-more-models">Show more models (up to 10)</button>' : ""}<button type="button" class="text-button model-refresh" data-action="refresh-models" ${!connectionId || unsupported || loading ? "disabled" : ""}>${icon("refresh")}Refresh models</button>${status}</div>`;
    const slider = `<div class="model-effort-control"><label for="work-effort">Reasoning effort</label><div class="model-effort-track"><div class="model-effort-visual" data-level="${activeIndex}" aria-hidden="true"><div class="model-effort-rail"><span class="model-effort-fill"></span></div><div class="model-effort-stops">${effortStops.map((stop) => `<span class="${supportedEfforts.has(stop.value) ? "" : "is-unavailable"}"></span>`).join("")}</div><span class="model-effort-thumb"></span></div><input id="work-effort" type="range" min="0" max="3" step="1" value="${activeIndex}" aria-label="Reasoning effort" aria-valuetext="${e(currentEffortLabel)}" ${supportedEfforts.size && !unsupported ? "" : "disabled"}></div><div class="model-effort-labels">${effortStops.map((stop) => `<button type="button" data-action="set-effort" data-value="${stop.value}" aria-pressed="${currentEffort === stop.value}" ${supportedEfforts.has(stop.value) && !unsupported ? "" : "disabled"}>${stop.label}</button>`).join("")}</div></div>`;
    const main = `<button type="button" class="model-row" data-action="open-model-menu" ${connectionId && !unsupported ? "" : "disabled"}><span class="model-row-name">${e(modelName)}</span><span class="model-effort">${e(currentEffortLabel)}</span>${icon("chevron")}</button>${slider}${status}`;
    return `<div class="model-picker"><span class="model-picker-summary"><span class="model-picker-trigger-name">${e(modelName)}</span> · <span class="model-effort">${e(currentEffortLabel)}</span></span><button type="button" class="model-picker-trigger" data-action="toggle-model-picker" aria-label="Options: ${e(modelName)}, reasoning effort: ${e(currentEffortLabel)}" aria-haspopup="dialog" aria-expanded="${pickerOpen}" aria-controls="model-popover" aria-describedby="model-choice-hint" title="${e(hint)}">Options${icon("chevron")}</button><span id="model-choice-hint" class="sr-only">${e(hint)}</span>${choice.notice ? `<span class="model-picker-notice" role="status">${e(choice.notice)}</span>` : ""}${pickerOpen ? `<div id="model-popover" class="model-popover" role="dialog" aria-label="Model and reasoning effort">${listOpen ? menu : main}</div>` : ""}</div>`;
}
