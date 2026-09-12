# Try the authentication preview

This stage implements ChatGPT device-code login and OpenAI API-key login. It
does not connect to GitHub or run agent tasks. Review authentication first;
repository integration is a separate decision.

## Run locally

Install Node.js 22 or newer, then run from this checkout:

```bash
npm ci
npm start
```

Open **http://localhost:8787**. In another terminal, read the workspace access
code and enter it on the page:

```bash
cat .goblin-auth/owner-token
```

The workspace code protects the preview's account settings. Treat it as a
password. It is not a ChatGPT credential, and it is never placed in a URL or
printed by the application. The browser receives a private session cookie;
after restarting the application, unlock it with the same workspace code.

Goblin installs Codex CLI **0.154.0** through the lockfile and starts
`codex app-server` over stdio. Its `HOME`, working directory, and `CODEX_HOME` are
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

ChatGPT access follows the connected plan/workspace. API-key usage is billed
separately through the OpenAI Platform. Switching methods requires disconnecting
the current account first. Keys rejected with HTTP 401 are not saved. Restricted
keys without permission to list models, or rate-limited requests, are explicitly
marked as unverified when saved; model execution is outside this preview.

## Run in Docker

Build the image and start it with persistent storage:

```bash
docker build -t goblin-auth-preview:0.1.0 .
docker run --rm --name goblin-auth-preview \
  -p 127.0.0.1:8787:8787 \
  -v goblin-auth-data:/data \
  goblin-auth-preview:0.1.0
```

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
sudo k3s kubectl exec -n goblin-preview goblin-auth -- cat /data/auth/owner-token
sudo k3s kubectl port-forward -n goblin-preview svc/goblin-auth-preview 8787:8787
```

If your controller gives the pod a different name, use the name shown by
`sudo k3s kubectl get pods -n goblin-preview -l app=goblin-auth-preview` for `exec`.

Leave the VM's port-forward running. On your own computer, forward the same
port through SSH, replacing the key path and hostname:

```bash
ssh -i /path/to/your-key.pem -L 8787:127.0.0.1:8787 goblinadmin@YOUR_VM_HOSTNAME
```

Open **http://localhost:8787** on your computer. No public application port needs
to be opened. See the [Azure reference](../deploy/azure/reference.md#use-the-saved-key-or-add-ssh-access-later)
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
not expose thread creation, shell execution, or an arbitrary Codex RPC endpoint.
The cluster still needs an appropriate sandbox runtime before a later stage
executes untrusted repository code.

## Configuration and storage

| Setting | Default | Purpose |
| --- | --- | --- |
| `GOBLIN_DATA_DIR` | `.goblin-auth` locally; `/data/auth` in the image | Persistent private application data |
| `GOBLIN_HOST` | `127.0.0.1` locally; `0.0.0.0` in the image | Listening interface |
| `GOBLIN_PORT` | `8787` | Listening port |
| `GOBLIN_PUBLIC_ORIGIN` | `http://localhost:8787` | Exact browser origin, used for Host and Origin checks |

Remote origins must use HTTPS. Loopback HTTP is supported for local access and
SSH/kubectl forwarding. Public ingress and automatic HTTPS setup are outside
this authentication preview.

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
npm test
npm run test:codex
npx playwright install --with-deps chromium
npm run test:browser
```

The unit tests exercise the HTTP and JSON-RPC paths with a fake Codex
process: login notifications, cancellation, expiry, persistence, logout,
credential isolation, access control, and key verification errors. The Codex check
initializes the real pinned Codex binary with a temporary home and verifies
local storage, restart, and logout using a synthetic, unusable key. Browser
tests cover the same UI flow with simulated
authentication, including mobile layout and clearing API-key inputs. These
checks do not use personal credentials or run a model task. A real ChatGPT
sign-in remains a manual acceptance check.
