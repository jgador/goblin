// These checks fail tsc if closed HTTP contracts become unrestricted strings again.
import type { Work, Attempt } from "../../frontend/src/work/contracts.js";
import type { Verification, Notice } from "../../frontend/src/api/contracts.js";
import type {
    WorkAction,
    IdentityKind,
} from "../../frontend/src/api/values.js";

// @ts-expect-error Only owning WorkStatus values are accepted.
const workStatus: Work["status"] = "Working";
// @ts-expect-error Attempt lifecycle is a closed set.
const attemptStatus: Attempt["status"] = "Ready";
// @ts-expect-error Only documented verification results are accepted.
const verification: Verification = "verified";
// @ts-expect-error Notice kinds are owned by the backend.
const notice: Notice["kind"] = "warning";
// @ts-expect-error Commands must be a defined WorkAction.
const action: WorkAction = "execute";
// @ts-expect-error Reservation kinds must be a defined IdentityKind.
const identity: IdentityKind = "Ticket";
void [workStatus, attemptStatus, verification, notice, action, identity];
