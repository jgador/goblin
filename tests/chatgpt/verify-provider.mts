// Opt-in integration check against disposable loopback services. Never targets
// an installed customer instance. See docs/chatgpt-integration.md for setup.
import { readFile } from "node:fs/promises";
import { request as httpsRequest } from "node:https";
import { createHash, randomBytes } from "node:crypto";

type Configuration = {
    issuer: string;
    resource: string;
    caFile: string;
    ownerPassword: string;
    providerUsername: string;
    providerPassword: string;
    clientSecret: string;
};

class VerificationFailure extends Error {}

try {
    const configuration = JSON.parse(
        await readFile(process.argv[2] ?? "", "utf8"),
    ) as Configuration;
    for (const value of [configuration.issuer, configuration.resource]) {
        const url = new URL(value);
        if (
            url.protocol !== "https:" ||
            url.hostname !== "localhost" ||
            url.username ||
            url.password ||
            url.search ||
            url.hash
        )
            throw new VerificationFailure(
                "This check requires disposable HTTPS loopback services.",
            );
    }
    const ca = await readFile(configuration.caFile);
    const issuer = configuration.issuer;
    const origin = new URL(configuration.resource).origin;
    const callback = "https://chatgpt.com/connector_platform_oauth_redirect";
    const clientId = "goblin-chatgpt";
    let checks = 0;
    let ownerCookie = "";
    let sequence = 0;

    function requireCheck(
        condition: unknown,
        message: string,
    ): asserts condition {
        if (!condition) throw new VerificationFailure(message);
        checks++;
    }

    async function request(
        url: string,
        body?: string,
        headers: Record<string, string> = {},
    ) {
        const address = new URL(url);
        if (
            address.origin !== origin &&
            address.origin !== new URL(issuer).origin
        )
            throw new VerificationFailure(
                "Refusing to follow an external redirect.",
            );
        return await new Promise<{
            status: number;
            location?: string;
            cookies: string[];
            text: string;
        }>((resolve, reject) => {
            const req = httpsRequest(
                address,
                { ca, method: body === undefined ? "GET" : "POST", headers },
                (response) => {
                    const chunks: Buffer[] = [];
                    let size = 0;
                    response.on("data", (chunk: Buffer) => {
                        size += chunk.length;
                        if (size > 1024 * 1024)
                            req.destroy(
                                new VerificationFailure(
                                    "Provider response exceeded the test limit.",
                                ),
                            );
                        else chunks.push(chunk);
                    });
                    response.on("end", () =>
                        resolve({
                            status: response.statusCode!,
                            location: response.headers.location,
                            cookies: response.headers["set-cookie"] ?? [],
                            text: Buffer.concat(chunks).toString("utf8"),
                        }),
                    );
                },
            );
            req.setTimeout(10000, () =>
                req.destroy(
                    new VerificationFailure("Provider request timed out."),
                ),
            );
            req.on("error", () =>
                reject(
                    new VerificationFailure(
                        "HTTPS request failed; check services and the test CA.",
                    ),
                ),
            );
            req.end(body);
        });
    }

    async function owner(path: string, body?: unknown) {
        const response = await request(
            origin + path,
            body === undefined ? undefined : JSON.stringify(body),
            {
                Origin: origin,
                Cookie: ownerCookie,
                "Content-Type": "application/json",
            },
        );
        requireCheck(response.status === 200, "Owner request failed: " + path);
        return JSON.parse(response.text);
    }

    function authorization(
        resource = configuration.resource,
        pkce = true,
        redirect = callback,
    ) {
        const verifier = randomBytes(32).toString("base64url");
        const state = randomBytes(24).toString("base64url");
        const query = new URLSearchParams({
            client_id: clientId,
            response_type: "code",
            redirect_uri: redirect,
            resource,
            scope: "openid email goblin.work.read",
            state,
        });
        if (pkce) {
            query.set(
                "code_challenge",
                createHash("sha256").update(verifier).digest("base64url"),
            );
            query.set("code_challenge_method", "S256");
        }
        return {
            url: issuer + "/protocol/openid-connect/auth?" + query,
            verifier,
            state,
        };
    }

    async function code(initial = authorization(), expectError = false) {
        const jar = new Map<string, string>();
        let response = await request(initial.url);
        for (let step = 0; step < 10; step++) {
            if (expectError && response.status >= 400)
                return { code: "", verifier: initial.verifier };
            for (const cookie of response.cookies) {
                const value = cookie.split(";")[0]!;
                jar.set(value.slice(0, value.indexOf("=")), value);
            }
            if (response.location?.startsWith(callback + "?")) {
                const redirect = new URL(response.location);
                requireCheck(
                    redirect.searchParams.get("state") === initial.state,
                    "OAuth state mismatch.",
                );
                requireCheck(
                    redirect.searchParams.get("iss") === issuer,
                    "OAuth response issuer mismatch.",
                );
                if (expectError && redirect.searchParams.has("error"))
                    return { code: "", verifier: initial.verifier };
                const value = redirect.searchParams.get("code");
                requireCheck(value, "Authorization code was not returned.");
                return { code: value, verifier: initial.verifier };
            }
            const cookies = [...jar.values()].join("; ");
            if (response.location) {
                response = await request(
                    new URL(response.location, issuer).href,
                    undefined,
                    { Cookie: cookies },
                );
                continue;
            }
            const action = response.text
                .match(/<form\b[^>]*\baction="([^"]+)"/i)?.[1]
                ?.replaceAll("&amp;", "&");
            requireCheck(
                action,
                "Provider did not present a supported login or consent form.",
            );
            const values = new URLSearchParams();
            for (const input of response.text.matchAll(/<input\b[^>]*>/gi)) {
                const name = input[0].match(/\bname="([^"]+)"/i)?.[1];
                const value = input[0].match(/\bvalue="([^"]*)"/i)?.[1];
                if (name && /type="hidden"/i.test(input[0]))
                    values.set(name, value ?? "");
            }
            if (/name="username"/.test(response.text)) {
                values.set("username", configuration.providerUsername);
                values.set("password", configuration.providerPassword);
            } else values.set("accept", "Yes");
            response = await request(
                new URL(action, issuer).href,
                values.toString(),
                {
                    Cookie: cookies,
                    "Content-Type": "application/x-www-form-urlencoded",
                },
            );
        }
        throw new VerificationFailure(
            "Authorization exceeded the redirect limit.",
        );
    }

    async function exchange(
        grant: { code: string; verifier: string },
        resource = configuration.resource,
        secret = configuration.clientSecret,
    ) {
        return await request(
            issuer + "/protocol/openid-connect/token",
            new URLSearchParams({
                grant_type: "authorization_code",
                client_id: clientId,
                redirect_uri: callback,
                code: grant.code,
                code_verifier: grant.verifier,
                resource,
            }).toString(),
            {
                "Content-Type": "application/x-www-form-urlencoded",
                Authorization:
                    "Basic " +
                    Buffer.from(clientId + ":" + secret).toString("base64"),
            },
        );
    }

    async function mcp(accessToken: string, method: string, params: unknown) {
        const response = await request(
            configuration.resource,
            JSON.stringify({ jsonrpc: "2.0", id: ++sequence, method, params }),
            {
                Authorization: "Bearer " + accessToken,
                "Content-Type": "application/json",
                Accept: "application/json, text/event-stream",
                "MCP-Protocol-Version": "2025-11-25",
            },
        );
        requireCheck(
            response.status === 200,
            "Authenticated MCP " +
                method +
                " failed with HTTP " +
                response.status +
                ".",
        );
        const data = response.text.startsWith("{")
            ? response.text
            : response.text
                  .split("\n")
                  .find((line) => line.startsWith("data: "))
                  ?.slice(6);
        requireCheck(data, "MCP returned no response.");
        return JSON.parse(data).result;
    }

    const login = await request(
        origin + "/api/session",
        JSON.stringify({ password: configuration.ownerPassword }),
        {
            Origin: origin,
            "Content-Type": "application/json",
        },
    );
    requireCheck(login.status === 200, "Fixture owner login failed.");
    ownerCookie = login.cookies[0]!.split(";")[0]!;
    const preflight = await owner("/api/integrations/chatgpt/verify", {});
    requireCheck(
        preflight.discovery && preflight.database,
        "Provider/storage preflight failed.",
    );
    const valid = await code();
    const tokens = await exchange(valid);
    requireCheck(tokens.status === 200, "Authorization-code exchange failed.");
    const token = JSON.parse(tokens.text);
    const payload = JSON.parse(
        Buffer.from(token.access_token.split(".")[1], "base64url").toString(),
    );
    const header = JSON.parse(
        Buffer.from(token.access_token.split(".")[0], "base64url").toString(),
    );
    requireCheck(
        header.typ === "at+jwt" && header.alg === "RS256",
        "Incorrect access-token header.",
    );
    requireCheck(
        payload.iss === issuer && payload.aud === configuration.resource,
        "Incorrect access-token resource binding.",
    );
    const userinfo = await request(
        issuer + "/protocol/openid-connect/userinfo",
        undefined,
        { Authorization: "Bearer " + token.access_token },
    );
    requireCheck(
        userinfo.status === 200 &&
            JSON.parse(userinfo.text).email_verified === true,
        "Verified email is missing from UserInfo.",
    );
    requireCheck(
        (await exchange(valid)).status === 400,
        "Authorization code was reusable.",
    );
    const wrongPkce = await code();
    requireCheck(
        (
            await exchange({
                ...wrongPkce,
                verifier: randomBytes(32).toString("base64url"),
            })
        ).status === 400,
        "Incorrect PKCE was accepted.",
    );
    requireCheck(
        (
            await exchange(
                await code(),
                configuration.resource,
                "invalid-fixture-secret",
            )
        ).status === 401,
        "Incorrect client secret was accepted.",
    );
    requireCheck(
        (await exchange(await code(), configuration.resource + "-other"))
            .status === 400,
        "Mismatched token resource was accepted.",
    );
    const unknownResource = await code(
        authorization(configuration.resource + "-other"),
        true,
    );
    requireCheck(
        !unknownResource.code ||
            (await exchange(unknownResource, configuration.resource + "-other"))
                .status === 400,
        "Provider issued a token for an unknown resource.",
    );
    for (const [label, invalid] of [
        ["missing PKCE", authorization(configuration.resource, false)],
        [
            "unregistered callback",
            authorization(
                configuration.resource,
                true,
                "https://other.example.test/callback",
            ),
        ],
    ] as const) {
        requireCheck(
            !(await code(invalid, true)).code,
            "Authorization accepted " + label + ".",
        );
    }
    // A replayed authorization code intentionally invalidates its client session.
    // Start a fresh session before verifying ordinary refresh behavior.
    const fresh = await exchange(await code());
    requireCheck(fresh.status === 200, "Fresh authorization failed.");
    const refresh = await request(
        issuer + "/protocol/openid-connect/token",
        new URLSearchParams({
            grant_type: "refresh_token",
            refresh_token: JSON.parse(fresh.text).refresh_token,
            resource: configuration.resource,
        }).toString(),
        {
            "Content-Type": "application/x-www-form-urlencoded",
            Authorization:
                "Basic " +
                Buffer.from(
                    clientId + ":" + configuration.clientSecret,
                ).toString("base64"),
        },
    );
    requireCheck(refresh.status === 200, "Token refresh failed.");
    const accessToken = JSON.parse(refresh.text).access_token;
    await mcp(accessToken, "initialize", {
        protocolVersion: "2025-11-25",
        capabilities: {},
        clientInfo: { name: "goblin-provider-verification", version: "1" },
    });
    const tools = await mcp(accessToken, "tools/list", {});
    requireCheck(
        tools.tools.length === 4 &&
            tools.tools.every(
                (tool: { annotations: { readOnlyHint: boolean } }) =>
                    tool.annotations.readOnlyHint,
            ),
        "Unexpected tool surface.",
    );
    const ids = await owner("/api/identities", { kinds: ["Work", "Command"] });
    const workId = ids.ids[0];
    await owner("/api/work/commands", {
        workId,
        commandId: ids.ids[1],
        action: "Create",
        text: "Disposable OAuth verification Work",
    });
    const call = async (name: string, args: unknown) =>
        await mcp(accessToken, "tools/call", { name, arguments: args });
    requireCheck(
        (await call("goblin_get_work_status", { workId })).isError,
        "OAuth login granted Work without owner approval.",
    );
    await owner("/api/integrations/chatgpt/grants", {
        workId,
        subject: payload.sub,
        hours: 1,
        shareApprovedResult: false,
    });
    const shared = await call("goblin_get_work_status", { workId });
    requireCheck(
        !shared.isError && shared.structuredContent.workId === workId,
        "Shared Work could not be read.",
    );
    requireCheck(
        (await call("goblin_get_work_result", { workId })).isError,
        "Status grant exposed a result.",
    );
    const state = await owner("/api/integrations/chatgpt");
    const grant = state.grants.find(
        (item: { workId: string }) => item.workId === workId,
    );
    await owner("/api/integrations/chatgpt/grants/revoke", { id: grant.id });
    const revoked = await call("goblin_get_work_status", { workId });
    requireCheck(
        revoked.isError && !revoked._meta?.["mcp/www_authenticate"],
        "Revocation was not enforced as a local permission denial.",
    );
    console.log(
        `PASS: ${checks} checks against real Keycloak, TLS, PostgreSQL, and the Goblin MCP endpoint. ChatGPT UI installation remains a separate manual check.`,
    );
} catch (error) {
    // Avoid raw responses, cookies, tokens, credentials, or upstream stack traces.
    console.error(
        error instanceof VerificationFailure
            ? error.message
            : "Provider verification failed.",
    );
    process.exitCode = 1;
}
