# Try the authentication preview

This stage implements ChatGPT device-code login, OpenAI API-key login, and a
short prompt test using the connected account. Review authentication first;
GitHub and repository integration remain a separate decision.

## Run locally

Install the .NET 10 SDK and Node.js 22 or newer, then run from this checkout:

```bash
npm ci
npm start
```

`npm start` builds the C# backend and TypeScript browser assets, then starts
the ASP.NET Core Minimal API host. After editing sources, restart it to rebuild.
To build separately, run `npm run build`, then launch with
`dotnet src/Goblin.Web/bin/Debug/net10.0/Goblin.Web.dll`.
Browser assets in `dist/` and .NET `bin/` and `obj/` output are ignored by Git.

Open **http://localhost:8787**. In another terminal, read the workspace access
code and enter it on the page:

```bash
cat .goblin-auth/owner-token
```

The workspace code protects the preview's account settings. Treat it as a
password. It is not a ChatGPT credential, and it is never placed in a URL or
printed by the application. The browser receives a private session cookie;
after restarting the application, unlock it with the same workspace code.

Goblin installs Codex CLI **0.154.0** through the lockfile. Its C# client starts
the package's official Rust binary with `app-server` and communicates over stdio
using the checked-in JSON schemas. Its `HOME`, working directory, and `CODEX_HOME` are
private to the preview. Existing machine-level Codex configuration and provider
credentials are not inherited. You can keep using your normal Codex installation.

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
   displayed code, and complete sign-in. Goblin should show your connected account.
2. Stop and restart `npm start`, unlock the preview, and confirm the connection
   remains. No second ChatGPT login should be needed while the credentials remain valid.
3. Select **Lock preview** and unlock it again. This locks browser access without
   disconnecting Codex.
4. Select **Disconnect Codex**. The account should disappear, including after a
   restart. Disconnecting removes this workspace's cached credentials; it does
   not revoke an API key in the OpenAI Platform.
5. Open **Use an OpenAI API key instead** and connect a key. The preview checks
   `GET /v1/models` and then asks Codex to save it. This does not generate a model
   response or establish that inference credits or a particular model are available.
6. Try canceling ChatGPT sign-in and starting again. Refreshing the browser
   during a pending login should restore its current code.
7. While connected, enter a short prompt under **Try a simple prompt**, then
   select **Send test prompt**. The default is “Say hello in one sentence.”
   Goblin should display **Response received**, the model's actual reply, the
   sign-in method, model name, and elapsed time. A successful reply verifies
   that the saved account can make a model request.

ChatGPT access follows the connected plan/workspace. API-key usage is billed
separately through the OpenAI Platform. Switching methods requires disconnecting
the current account first. Keys rejected with HTTP 401 are not saved. Restricted
keys without permission to list models, or rate-limited requests, are explicitly
marked as unverified when saved. A successful prompt test clears that notice.

## Test a prompt with the connected account

The prompt test uses the same Codex app-server process and stored login as the
authentication UI. It sends `thread/start` with `ephemeral: true`, followed by
`turn/start`, and waits for a completed turn with an assistant reply. It uses
Codex's default model and displays the model name returned by the server.
See the [app-server protocol](https://learn.chatgpt.com/docs/app-server#turns).

Each click makes a real model request and counts toward the connected ChatGPT
plan's usage limits or OpenAI API billing. The initial API-key check against
`/v1/models` remains a separate check that does not generate a response.

Prompts are limited to 500 characters, replies to 8,000 characters, and one
prompt test can run at a time. The response wait times out after 90 seconds;
closing or reloading the page also requests cancellation. Each test uses a
fresh temporary conversation; previous test prompts are not conversation
history. The displayed reply clears on reload, locking, or disconnecting.

The runtime disables shell tools, browsing, apps, plugins, image tools,
multi-agent tools, hooks, and memory. The test also uses a read-only sandbox
and rejects approval requests. It does not enable repository operations.
Account changes are serialized with generation so a prompt cannot switch
accounts halfway through. Model failures show safe messages without raw
upstream errors, credentials, or reasoning output.

## Run in Docker

Build the image and start it with persistent storage:

```bash
docker build -t goblin-auth-preview:0.1.0 .
docker run --rm --name goblin-auth-preview \
  -p 127.0.0.1:8787:8787 \
  -v goblin-auth-data:/data \
  goblin-auth-preview:0.1.0
```

The image builds browser assets and publishes .NET in separate stages. The final
image contains the ASP.NET Core runtime, published C# host, browser assets, and
the official Rust Codex binary with its runtime resources. Node.js and the
TypeScript compiler are build dependencies only.

In another terminal, read the workspace access code:

```bash
docker exec goblin-auth-preview cat /data/auth/owner-token
```

Open **http://localhost:8787** and follow the same checks. Recreate the container
with the same named volume to verify persistence.

## Run in the provisioned Agent Sandbox cluster

The existing Azure bootstrap installs the core Agent Sandbox controller. The
[preview manifest](../deploy/auth-preview/sandbox.yaml) uses that controller
directly; extensions, warm pools, ingress, and GitHub access are not required.
Azure setup asks you to choose and confirm a **Goblin password**. The manifest
mounts the password verifier created during deployment. Use that password to
open the preview; there is no access-token retrieval step.

For a VM provisioned with an older template, first redeploy the updated Azure
template and choose a password. The updated manifest requires the
`goblin-owner-password` Secret in `goblin-preview`; see the
[password setup reference](../deploy/azure/reference.md#goblin-password).

Build the image on a Docker-enabled machine for the VM's Linux architecture
(the default Azure VM is amd64), then export it:

```bash
docker build --platform linux/amd64 -t goblin-auth-preview:0.1.0 .
docker save -o goblin-auth-preview.tar goblin-auth-preview:0.1.0
```

Copy the image archive and `deploy/auth-preview/sandbox.yaml` to the VM using
your existing SSH access. On the VM, import and deploy them:

```bash
sudo k3s ctr images import goblin-auth-preview.tar
sudo k3s kubectl apply -f sandbox.yaml
sudo k3s kubectl wait --for=condition=Ready sandbox/goblin-auth \
  -n goblin-preview --timeout=180s
sudo k3s kubectl port-forward -n goblin-preview svc/goblin-auth-preview 8787:8787
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
sudo k3s kubectl delete pod -n goblin-preview -l app=goblin-auth-preview
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

## Configuration and storage

| Setting | Default | Purpose |
| --- | --- | --- |
| `GOBLIN_DATA_DIR` | `.goblin-auth` locally; `/data/auth` in the image | Persistent private application data |
| `GOBLIN_PASSWORD_HASH_FILE` | Unset locally/in Docker; `/etc/goblin/owner-password` in the Sandbox manifest | Read-only file containing the provisioned password verifier; required when configured |
| `GOBLIN_HOST` | `127.0.0.1` locally; `0.0.0.0` in the image | Listening interface |
| `GOBLIN_PORT` | `8787` | Listening port |
| `GOBLIN_PUBLIC_ORIGIN` | `http://localhost:8787` | Exact browser origin, used for Host and Origin checks |
| `GOBLIN_CODEX_COMMAND` | Local pinned native binary, otherwise `codex` on `PATH` | Optional path to the official Codex executable |

Remote origins must use HTTPS. Loopback HTTP is supported for local access and
SSH/kubectl forwarding. Public ingress and automatic HTTPS setup are outside
this authentication preview.

Local and Docker runs without `GOBLIN_PASSWORD_HASH_FILE` continue to generate
and accept the private workspace access code. When the variable is set, Goblin
uses only the configured password verifier and never falls back to an access
code. The password is compared exactly, including spaces. A missing or invalid
verifier prevents startup. Password attempts use the existing rate limit and
private session cookies. Restart Goblin after changing the verifier to load the
new password and invalidate current browser sessions.

Codex stores credentials in `codex/auth.json` under the data directory, with
`cli_auth_credentials_store="file"`. It owns ChatGPT refresh and logout. The file
contains credentials in plaintext; the parent directory is private, and storage
and backups must be protected accordingly. The HTTP server never serves it.
Don't copy it into images, source control, chat messages, or shared volumes.

The backend doesn't log RPC payloads, API keys, device codes, upstream error
bodies, or the workspace access code. It discards raw Codex stderr because it may
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
only type imports from `shared/api.ts`, so it needs no client framework or bundler.

The integration tests exercise the HTTP and JSON-RPC paths with a fake Codex
process: login notifications, cancellation, expiry, persistence, logout,
credential isolation, access control, and key verification errors. The Codex check
initializes the real pinned Codex binary with a temporary home and verifies
local storage, restart, and logout using a synthetic, unusable key. Browser
tests cover the same UI flow with simulated authentication and model replies,
including mobile layout, clearing API-key inputs, and prompt success/failure.
The unit tests also cover prompt validation, event ordering, failed and empty
replies, cancellation, timeouts, and overlapping requests. These
checks do not use personal credentials or run a model task. A real ChatGPT
sign-in remains a manual acceptance check.

See [App Server migration notes](app-server-migration.md) for the C# project layout,
protocol regeneration, and the boundary between Goblin orchestration and Codex Core.
