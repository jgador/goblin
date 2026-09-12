export class PublicError extends Error {
  readonly code: string;
  readonly status: number;

  constructor(code: string, message: string, status = 400) {
    super(message);
    this.code = code;
    this.status = status;
  }
}

export function runtimeError() {
  return new PublicError(
    "runtime_unavailable",
    "Codex is unavailable. Retry in a moment. If this continues, restart the preview.",
    503,
  );
}
