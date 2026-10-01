export class ApiError extends Error {
    readonly code: string;
    readonly status: number;

    constructor(code: string, message: string, status: number) {
        super(message);
        this.name = "ApiError";
        this.code = code;
        this.status = status;
    }
}

type RequestOptions = {
    body?: unknown;
    signal?: AbortSignal;
    failureMessage?: string;
    onUnauthorized?: () => void;
};

function failure(value: unknown) {
    if (!value || typeof value !== "object" || !("error" in value)) return;
    const error = value.error;
    if (!error || typeof error !== "object") return;
    return {
        code:
            "code" in error && typeof error.code === "string"
                ? error.code
                : undefined,
        message:
            "message" in error && typeof error.message === "string"
                ? error.message
                : undefined,
    };
}

// Goblin JSON only. External services and HTML assets have separate adapters.
export async function requestJson<T>(
    path: string,
    options: RequestOptions = {},
): Promise<T> {
    const { body, failureMessage = "The request could not be confirmed." } =
        options;
    const signal =
        body === undefined
            ? AbortSignal.any([
                  ...(options.signal ? [options.signal] : []),
                  AbortSignal.timeout(6000),
              ])
            : options.signal;
    const response = await fetch(path, {
        method: body === undefined ? "GET" : "POST",
        credentials: "same-origin",
        cache: body === undefined ? "no-store" : undefined,
        headers:
            body === undefined
                ? undefined
                : { "Content-Type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
        signal,
    });
    signal?.throwIfAborted();
    // A proxy's non-JSON 401 must still clear the caller's private view.
    if (response.status === 401) options.onUnauthorized?.();
    let result: unknown;
    try {
        result = await response.json();
    } catch {
        signal?.throwIfAborted();
        throw new ApiError(
            response.ok ? "invalid_response" : "request_failed",
            failureMessage,
            response.status,
        );
    }
    signal?.throwIfAborted();
    if (!response.ok) {
        const error = failure(result);
        throw new ApiError(
            error?.code || "request_failed",
            error?.message || failureMessage,
            response.status,
        );
    }
    // Successful Goblin responses are produced by the backend's public contracts.
    return result as T;
}

export function errorMessage(failure: unknown, fallback: string): string {
    return failure instanceof Error ? failure.message : fallback;
}
