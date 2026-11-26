import type { Page } from "@playwright/test";

export function refreshIntervalControl(page: Page) {
    return page.getByLabel(/^Auto-refresh interval,/);
}

export async function refreshWorkspace(page: Page) {
    await refreshIntervalControl(page).click();
    await page
        .getByRole("button", { name: "Refresh now", exact: true })
        .click();
}
