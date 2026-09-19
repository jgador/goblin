# Try the authentication preview

This authentication implementation is a preview. It implements ChatGPT device-code
login, OpenAI API-key login, and an automatic connection check using the saved account
through Codex, the current primary and only implemented agent integration.
Review authentication first;
GitHub and repository integration remain a separate decision.

The [architecture plan](architecture-refactoring-plan.md) prepares for other
agents, including Claude and GitHub Copilot. Their authentication flows and
runtime capabilities are not implemented by this preview; OpenAI sign-in only
configures the Codex connection.

## Run locally

Install the .NET 10 SDK, Node.js 24 or newer, and Python 3, then run from this checkout:

```bash
npm ci
npm start
```

`npm start` builds the C# backend and TypeScript browser assets, provisions the
local password if needed, then starts the ASP.NET Core Minimal API host. After
editing sources, restart it to rebuild. To build separately, run `npm run build`,
then launch with `python3 deploy/local/start.py`.
Browser assets in `frontend/dist/`, test tooling in `dist/`, and .NET `bin/` and
`obj/` output are ignored by Git.

On first start, choose and confirm your password in the terminal; input is hidden.
Open **http://localhost:8787**, enter that password, and click **Open workspace**.
Subsequent starts reuse its saved verifier. After locking the workspace or
restarting Goblin, enter the password again. There is no default password or prefill.

Every environment requires `GOBLIN_PASSWORD_HASH_FILE`. The local launcher sets
it to this checkout's `.goblin-secrets/owner-password` unless you explicitly supply
another verifier path. A missing or invalid configured file prevents startup.
Password login creates a private session cookie; restarting the application
invalidates browser sessions.

Goblin installs Codex CLI **0.154.0** through the lockfile. Its C# client starts
the package's official Rust binary with `app-server` and communicates over stdio
using the checked-in JSON schemas. Its `HOME`, working directory, and `CODEX_HOME` are
private to the preview. Existing machine-level Codex configuration and provider
credentials are not inherited. You can keep using your normal Codex installation.

### Password setup and storage

`npm start` and the full local installer share `.goblin-secrets/owner-password`.
They use Azure's hashing helper (PBKDF2-SHA256, 600,000 iterations, random salt).
Only the verifier is written to disk, with file mode `0600` inside a `0700`
directory. Git ignores the folder's contents except for `.gitkeep`, which keeps
the empty folder in the repository. Docker excludes the entire folder.

To prepare the password before starting the application:

```bash
npm run setup:password
npm start
```

Use `npm run setup:password -- --replace` to choose a new password, then restart
Goblin to load it. For unattended first-time setup, supply `GOBLIN_LOCAL_PASSWORD`
through the process environment; it never replaces an existing verifier.
The same verifier can be mounted into Docker. Azure creates its verifier during
provisioning. For a direct .NET launch, set
`GOBLIN_PASSWORD_HASH_FILE=.goblin-secrets/owner-password` explicitly.

## Enable ChatGPT device authentication

Device-code login must be enabled before your first ChatGPT sign-in. Check this
setting even if you already use Codex through another sign-in method.

For a workspace account:

1. Open ChatGPT and go to **Workspace settings**.
2. Open **Permissions & roles**.
3. Select the role that applies to your account, usually the default workspace role.
4. Under **Codex Local**, enable **Codex Local access**.
5. Enable **Device Code Authentication**.

If OpenAI shows **“Please contact your workspace admin to enable device code
authentication”**, ask the workspace admin to enable both permissions for your
role. Goblin cannot change these ChatGPT workspace permissions.

For a personal account, enable device-code login in ChatGPT under
**Settings → Security**.

After enabling the setting, return to OpenAI's sign-in page and try again. If
the code has expired, cancel sign-in in Goblin and start again for a new code.
The preview includes these steps before sign-in and beside the device code.
Device-code login is currently beta; see [OpenAI's headless login instructions](https://learn.chatgpt.com/docs/auth#login-on-headless-devices).

## What to check

1. Select **Continue with ChatGPT**. Open OpenAI's sign-in page, enter the
   displayed code, and complete sign-in. Goblin should show your account and
   **Verifying connection…**, then **Connected** after a successful background check.
2. Stop and restart `npm start`, unlock the preview, and confirm the connection
   remains. No second ChatGPT login should be needed while the credentials remain valid.
3. Select **Lock workspace** and unlock it again. This locks browser access without
   disconnecting Codex.
4. Select **Disconnect Codex**. The account should disappear, including after a
   restart. Disconnecting removes this workspace's cached credentials; it does
   not revoke an API key in the OpenAI Platform.
5. Open **Use an OpenAI API key instead** and connect a key. The preview checks
   `GET /v1/models` and then asks Codex to save it. Goblin then automatically checks
   model access with a small request that counts toward API billing.
6. Try canceling ChatGPT sign-in and starting again. Refreshing the browser
   during a pending login should restore its current code.
7. Select **Check again** to verify model access again. While the check runs,
   Goblin shows **Verifying connection…** and disables duplicate checks. A failed
   check replaces any previous success with an explanation and **Retry check**.
   Expired sign-ins, usage limits, access denial, and timeouts have distinct messages.
   The account email and plan remain visible; the background prompt and reply are hidden.

ChatGPT access follows the connected plan/workspace. API-key usage is billed
separately through the OpenAI Platform. Switching methods requires disconnecting
the current account first. Keys rejected with HTTP 401 are not saved. Restricted
keys without permission to list models, or rate-limited requests, are explicitly
marked as unverified when saved. A successful connection check clears that notice.

## Verify the connected account

The connection check uses the existing `/api/prompt` endpoint with a fixed short
prompt, “Reply with only OK.” It uses the same Codex app-server process and stored
login as the authentication UI. It sends `thread/start` with `ephemeral: true`,
followed by `turn/start`, and waits for a completed turn with a nonempty assistant
reply from Codex's default model. Only then does the UI report **Connected**.
Neither the prompt nor the response is displayed.
See the [app-server protocol](https://learn.chatgpt.com/docs/app-server#turns).

Goblin checks once when a signed-in account is loaded after sign-in, opening or
reloading the page, or unlocking the workspace. It also checks when switching
accounts or selecting **Check again** or **Retry check**. Routine status polling
never repeats a successful or failed check. Results are held only in page memory;
locking or losing the account clears them. A status refresh failure replaces any
previous success with **Connection unavailable** until a new check succeeds.

Each check makes a real model request and counts toward the connected ChatGPT
plan's usage limits or OpenAI API billing. The initial API-key validation against
`/v1/models` remains a separate check that does not generate a response.

Prompts are limited to 500 characters, replies to 8,000 characters, and one
check can run at a time. The response wait times out after 90 seconds;
closing or reloading the page also requests cancellation. Each test uses a
fresh temporary conversation; previous test prompts are not conversation
history. Checks never add a visible conversation to the UI.

The runtime disables shell tools, browsing, apps, plugins, image tools,
multi-agent tools, hooks, and memory. The test also uses a read-only sandbox
and rejects approval requests. It does not enable repository operations.
Account changes are serialized with generation so a prompt cannot switch
accounts halfway through. Model failures show safe messages without raw
upstream errors, credentials, or reasoning output.

## Run in Docker

Create a verifier using [Password setup and storage](#password-setup-and-storage), then build
the image and copy the verifier into a private Docker volume. The setup command
sets file ownership for the application's UID 1000:

```bash
docker build -t goblin-auth:0.1.0 .
docker run --rm -i --user 0:0 --entrypoint sh \
  -v goblin-auth-password:/password goblin-auth:0.1.0 \
  -c 'umask 077; cat > /password/owner-password; chown 1000:1000 /password/owner-password' \
  < .goblin-secrets/owner-password
```

Start Goblin with persistent application storage and the password volume mounted
read-only:

```bash
docker run --rm --name goblin-auth \
  -p 127.0.0.1:8787:8787 \
  -v goblin-auth-data:/data \
  -v goblin-auth-password:/etc/goblin:ro \
  -e GOBLIN_PASSWORD_HASH_FILE=/etc/goblin/owner-password \
  goblin-auth:0.1.0
```

The image builds browser assets and publishes .NET in separate stages. The final
image contains the ASP.NET Core runtime, published C# host, browser assets, and
the official Rust Codex binary with its runtime resources. Node.js and the
TypeScript compiler are build dependencies only.

Open **http://localhost:8787**, enter your password, and follow the same checks.
Recreate the container with the same named volumes to verify persistence.

## Run in the provisioned Agent Sandbox cluster

The Azure template now installs this application automatically and serves it at
the deployment's `goblinUrl` (`http://<Azure-assigned-hostname>`). Open that URL
and enter the **Goblin password** chosen during provisioning. See the
[Azure guide](../deploy/azure/README.md) for installation and upgrades.

The steps below are for a **manual installation using SSH forwarding**. Do not
apply this local-access manifest over the automatic Azure installation: it
restores the localhost origin and blocks Traefik. Azure's rendered configuration
is saved at `/var/lib/goblin/deploy/azure/app` for subsequent changes.

The Azure bootstrap installs the core Agent Sandbox controller. The
[preview manifest](../deploy/auth/sandbox.yaml) uses that controller
directly; extensions, warm pools, ingress, and GitHub access are not required.
Azure setup asks you to choose and confirm a **Goblin password**. The manifest
mounts the password verifier created during deployment. Use that password to
open the preview.

The manifest requires the `goblin-owner-password` Secret in `goblin`; see the
[password setup reference](../deploy/azure/reference.md#goblin-password). For a VM
provisioned with an older template, redeploy the updated template to install both
the password and application automatically instead of following these manual steps.

For an existing installation in `goblin-preview`, follow the
[namespace migration guidance](#migrate-from-earlier-deployment-names) before
applying the new manifest.

Build the image on a Docker-enabled machine for the VM's Linux architecture
(the default Azure VM is amd64), then export it:

```bash
docker build --platform linux/amd64 -t goblin-auth:0.1.0 .
docker save -o goblin-auth.tar goblin-auth:0.1.0
```

Copy the image archive and `deploy/auth/sandbox.yaml` to the VM using
your existing SSH access. On the VM, import and deploy them:

```bash
sudo k3s ctr images import goblin-auth.tar
sudo k3s kubectl apply -f sandbox.yaml
sudo k3s kubectl wait --for=condition=Ready sandbox/goblin-auth \
  -n goblin --timeout=180s
sudo k3s kubectl port-forward -n goblin svc/goblin-auth 8787:8787
```

Leave the VM's port-forward running. On your own computer, forward the same
port through SSH, replacing the key path and hostname:

```bash
ssh -i /path/to/your-key.pem -L 8787:127.0.0.1:8787 goblinadmin@YOUR_VM_HOSTNAME
```

Open **http://localhost:8787** on your computer and enter your **Goblin password**.
No public application port needs to be opened. See the [Azure reference](../deploy/azure/reference.md#use-the-saved-key-or-add-ssh-access-later)
if SSH access was not enabled during deployment.

After connecting an account, replace just the pod to test persistence:

```bash
sudo k3s kubectl delete pod -n goblin -l app=goblin-auth
```

The Sandbox controller recreates it and reattaches the PVC. Restart the
port-forward, unlock the preview, and check the saved connection. The PVC is
declared separately from the Sandbox so replacing the Sandbox can retain it.
Deleting the PVC, namespace, or full manifest also deletes the preview data
according to the storage class's reclaim policy.

This preview intentionally supports one owner and one active Codex process per
data volume. Do not mount the same credential directory into concurrent replicas.
The container runs without root or a Kubernetes service-account token; it does
not expose arbitrary thread creation, shell execution, or Codex RPC methods.
Its prompt endpoint creates only the restricted temporary conversations above.
The cluster still needs an appropriate sandbox runtime before a later stage
executes untrusted repository code.

### Migrate from earlier deployment names

Earlier versions used `deploy/auth-preview/sandbox.yaml`, the `goblin-preview`
namespace, and the `goblin-auth-preview` image and service. The current manifest
is `deploy/auth/sandbox.yaml`, with namespace `goblin` and image and service
`goblin-auth`. Kubernetes cannot rename a namespace. Applying the new manifest
creates separate resources; it does not move the existing password Secret or PVC.

Redeploy the updated Azure template to create `goblin-owner-password` in
`goblin`. Build and import the image under its new name. For a fresh installation,
apply the new manifest and connect your account again.

To preserve a saved connection, back up the existing data volume and stop the
old Sandbox workload before transferring its contents into the new namespace's
`goblin-auth-data` PVC. Keep the new workload stopped until the transfer is
complete, and preserve ownership by UID/GID 1000 and the private file permissions.
Volume migration depends on the cluster's storage class. Keep the old namespace
and PVC until access and persistence in `goblin` have been verified; deleting
them can delete the original data.

## Configuration and storage

| Setting | Default | Purpose |
| --- | --- | --- |
| `GOBLIN_DATA_DIR` | `.goblin-auth` locally; `/data/auth` in the image | Persistent private application data |
| `GOBLIN_PASSWORD_HASH_FILE` | `.goblin-secrets/owner-password` supplied by the local launcher; `/etc/goblin/owner-password` in the Sandbox manifest | Required password verifier file in every environment |
| `GOBLIN_HOST` | `127.0.0.1` locally; `0.0.0.0` in the image | Listening interface |
| `GOBLIN_PORT` | `8787` | Listening port |
| `GOBLIN_PUBLIC_ORIGIN` | `http://localhost:8787` | Exact browser origin, used for Host and Origin checks |
| `GOBLIN_ALLOW_INSECURE_HTTP` | `false`; explicitly `true` in the Azure overlay | Allows a public HTTP origin before HTTPS is configured; traffic is unencrypted |
| `GOBLIN_CODEX_COMMAND` | Local pinned native binary, otherwise `codex` on `PATH` | Optional path to the official Codex executable |

Remote origins require HTTPS by default. The Azure overlay explicitly enables
public HTTP with `GOBLIN_ALLOW_INSECURE_HTTP=true`, retaining exact Host/Origin
checks and password authentication. Loopback HTTP is supported for local access
and SSH/kubectl forwarding without that setting. After configuring TLS on the
ingress, set `GOBLIN_PUBLIC_ORIGIN` to the HTTPS address, remove the HTTP opt-in,
and recreate the application pod. HTTPS origins use Secure session cookies.

The application always reads the configured password verifier, including on
localhost. Password collection happens during provisioning, outside the app.
The password is compared exactly, including spaces. A missing or invalid
verifier prevents startup. Password attempts use the existing rate limit and
private session cookies. Restart Goblin after changing the verifier to load the
new password and invalidate current browser sessions.

Codex stores credentials in `codex/auth.json` under the data directory, with
`cli_auth_credentials_store="file"`. It owns ChatGPT refresh and logout. The file
contains credentials in plaintext; the parent directory is private, and storage
and backups must be protected accordingly. The HTTP server never serves it.
Don't copy it into images, source control, chat messages, or shared volumes.

The backend doesn't log RPC payloads, API keys, device codes, upstream error
bodies, or configured passwords. It discards raw Codex stderr because it may
contain authentication details. Error messages give safe retry guidance.

## Automated checks

```bash
npm run typecheck
npm test
npm run test:codex
npx playwright install --with-deps chromium
npm run test:browser
```

The test commands build first. `npm test` requires Python 3 for the deterministic
schema-generation check, then runs .NET serialization/transport tests and the HTTP
integration tests against the C# host. Type checking also covers tests and scripts.
Playwright loads its TypeScript configuration and browser tests directly. The browser module uses
only type imports from `frontend/src/api/contracts.ts`, so it needs no client framework or bundler.

The integration tests exercise the HTTP and JSON-RPC paths with a fake Codex
process: login notifications, cancellation, expiry, persistence, logout,
credential isolation, access control, and key verification errors. The Codex check
initializes the real pinned Codex binary with a temporary home and verifies
local storage, restart, and logout using a synthetic, unusable key. Browser
tests cover the same UI flow with simulated authentication and model replies,
including mobile layout, clearing API-key inputs, automatic connection checks,
retry and error states, session expiry, account changes, and avoiding repeated
model requests during status polling.
The unit tests also cover prompt validation, event ordering, failed and empty
replies, cancellation, timeouts, and overlapping requests. These
checks do not use personal credentials or run a model task. A real ChatGPT
sign-in remains a manual acceptance check.

See [App Server migration notes](app-server-migration.md) for the C# project layout,
protocol regeneration, and the boundary between Goblin orchestration and Codex Core.
