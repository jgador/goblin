// Opt-in HTTPS check against a disposable loopback Goblin instance.
// Credentials and tokens are consumed locally and never printed.
import { readFile } from "node:fs/promises";
import { request as httpsRequest } from "node:https";
import { createHash, randomBytes } from "node:crypto";

type Configuration = {
    resource: string;
    caFile: string;
    ownerPassword: string;
};
class VerificationFailure extends Error {}

try {
    const configuration = JSON.parse(
        await readFile(process.argv[2] ?? "", "utf8"),
    ) as Configuration;
    const resource = new URL(configuration.resource);
    if (
        resource.protocol !== "https:" ||
        resource.hostname !== "localhost" ||
        resource.pathname !== "/mcp" ||
        resource.username ||
        resource.password ||
        resource.search ||
        resource.hash
    )
        throw new VerificationFailure(
            "Use a disposable HTTPS localhost Goblin instance.",
        );
    const origin = resource.origin;
    const ca = await readFile(configuration.caFile);
    const callback = "https://chatgpt.com/connector_platform_oauth_redirect";
    let cookie = "";
    let checks = 0;
    function check(condition: unknown, message: string): asserts condition {
        if (!condition) throw new VerificationFailure(message);
        checks++;
    }
    async function request(
        path: string,
        body?: string,
        headers: Record<string, string> = {},
        owner = false,
    ) {
        return await new Promise<{
            status: number;
            headers: import("node:http").IncomingHttpHeaders;
            text: string;
        }>((resolve, reject) => {
            const req = httpsRequest(
                new URL(path, origin),
                {
                    ca,
                    method: body === undefined ? "GET" : "POST",
                    headers: {
                        ...(body !== undefined
                            ? {
                                  "Content-Type": "application/json",
                                  "Content-Length": String(
                                      Buffer.byteLength(body),
                                  ),
                                  Origin: origin,
                              }
                            : {}),
                        ...(owner ? { Cookie: cookie } : {}),
                        ...headers,
                    },
                    timeout: 15_000,
                },
                (response) => {
                    let text = "";
                    response.setEncoding("utf8");
                    response.on("data", (chunk) => {
                        text += chunk;
                        if (text.length > 65536)
                            req.destroy(
                                new VerificationFailure(
                                    "Response exceeded its limit.",
                                ),
                            );
                    });
                    response.on("end", () =>
                        resolve({
                            status: response.statusCode ?? 0,
                            headers: response.headers,
                            text,
                        }),
                    );
                },
            );
            req.on("timeout", () =>
                req.destroy(
                    new VerificationFailure("HTTPS request timed out."),
                ),
            );
            req.on("error", reject);
            req.end(body);
        });
    }
    const discovery = await request("/.well-known/openid-configuration");
    check(discovery.status === 200, "OIDC discovery failed.");
    const metadata = JSON.parse(discovery.text);
    check(
        metadata.issuer === origin + "/",
        "Issuer differs from Goblin's canonical origin.",
    );
    check(
        metadata.code_challenge_methods_supported.includes("S256") &&
            !metadata.code_challenge_methods_supported.includes("plain"),
        "PKCE policy is incorrect.",
    );
    const login = await request(
        "/api/session",
        JSON.stringify({ password: configuration.ownerPassword }),
    );
    check(login.status === 200, "Owner login failed.");
    cookie = login.headers["set-cookie"]?.[0]?.split(";")[0] ?? "";
    check(!!cookie, "Owner session cookie was not returned.");
    const registered = await request(
        "/api/integrations/chatgpt/client",
        JSON.stringify({ redirectUri: callback, rotateSecret: true }),
        {},
        true,
    );
    check(registered.status === 200, "Disposable client registration failed.");
    const secret = JSON.parse(registered.text).clientSecret as string;
    check(!!secret, "Client secret was not returned on creation/rotation.");
    const verifier = randomBytes(32).toString("base64url");
    const parameters = new URLSearchParams({
        client_id: "goblin-chatgpt",
        response_type: "code",
        redirect_uri: callback,
        resource: resource.href,
        scope: "openid offline_access goblin.work.read",
        state: "loopback-verification",
        code_challenge: createHash("sha256")
            .update(verifier)
            .digest("base64url"),
        code_challenge_method: "S256",
    });
    const authorize = await request("/oauth/authorize?" + parameters);
    check(
        authorize.status === 200 && authorize.text.includes("Unlock Goblin"),
        "Authorization did not require owner sign-in.",
    );
    const consent = await request(
        "/oauth/authorize?" + parameters,
        undefined,
        {},
        true,
    );
    check(
        consent.status === 200 && consent.text.includes("Allow connection"),
        "Consent page was not shown.",
    );
    parameters.set("decision", "allow");
    const accepted = await request(
        "/oauth/authorize",
        parameters.toString(),
        { "Content-Type": "application/x-www-form-urlencoded" },
        true,
    );
    check(
        accepted.status === 302 && !!accepted.headers.location,
        "Authorization was not redirected.",
    );
    const redirect = new URL(accepted.headers.location!);
    check(
        redirect.origin === "https://chatgpt.com" &&
            redirect.searchParams.get("iss") === metadata.issuer &&
            redirect.searchParams.get("state") === "loopback-verification",
        "Authorization callback or issuer is incorrect.",
    );
    const tokenBody = new URLSearchParams({
        grant_type: "authorization_code",
        code: redirect.searchParams.get("code")!,
        code_verifier: verifier,
        redirect_uri: callback,
        resource: resource.href,
    });
    const credentials = {
        "Content-Type": "application/x-www-form-urlencoded",
        Authorization:
            "Basic " +
            Buffer.from("goblin-chatgpt:" + secret).toString("base64"),
    };
    const token = await request(
        "/oauth/token",
        tokenBody.toString(),
        credentials,
    );
    check(token.status === 200, "Authorization-code exchange failed.");
    const issued = JSON.parse(token.text);
    const header = JSON.parse(
        Buffer.from(issued.access_token.split(".")[0], "base64url").toString(),
    );
    const claims = JSON.parse(
        Buffer.from(issued.access_token.split(".")[1], "base64url").toString(),
    );
    check(
        header.alg === "RS256" && header.typ === "at+jwt",
        "Access-token format is incorrect.",
    );
    check(
        claims.iss === metadata.issuer &&
            (claims.aud === resource.href ||
                claims.aud.includes(resource.href)) &&
            !!claims.sub &&
            claims.scope.includes("goblin.work.read"),
        "Access-token identity, audience or scope is incorrect.",
    );
    const keys = await request("/oauth/keys");
    check(
        keys.status === 200 &&
            JSON.parse(keys.text).keys.some(
                (key: { kid: string; d?: string }) =>
                    key.kid === header.kid && !key.d,
            ),
        "Public signing key was not published.",
    );
    const mcpHeaders = {
        Authorization: "Bearer " + issued.access_token,
        Accept: "application/json, text/event-stream",
    };
    const initialize = await request(
        "/mcp",
        JSON.stringify({
            jsonrpc: "2.0",
            id: 1,
            method: "initialize",
            params: {
                protocolVersion: "2025-11-25",
                capabilities: {},
                clientInfo: { name: "loopback-verifier", version: "1" },
            },
        }),
        mcpHeaders,
    );
    check(initialize.status === 200, "Issued token could not initialize MCP.");
    const tools = await request(
        "/mcp",
        JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/list" }),
        mcpHeaders,
    );
    check(
        tools.status === 200 &&
            [
                "goblin_list_work",
                "goblin_get_work_status",
                "goblin_get_work_attention",
                "goblin_get_work_result",
            ].every((name) => tools.text.includes(name)),
        "MCP tool discovery failed.",
    );
    const refreshed = await request(
        "/oauth/token",
        new URLSearchParams({
            grant_type: "refresh_token",
            refresh_token: issued.refresh_token,
            resource: resource.href,
        }).toString(),
        credentials,
    );
    check(
        refreshed.status === 200 &&
            JSON.parse(refreshed.text).refresh_token !== issued.refresh_token,
        "Refresh-token rotation failed.",
    );
    const revoked = await request(
        "/api/integrations/chatgpt/client/revoke",
        "{}",
        {},
        true,
    );
    check(revoked.status === 200, "Connection revocation failed.");
    const denied = await request("/mcp", "{}", mcpHeaders);
    check(denied.status === 401, "Revoked access token remained usable.");
    console.log(
        `PASS: ${checks} checks against HTTPS, embedded OpenIddict, PostgreSQL, and MCP. ChatGPT UI installation remains a separate manual check.`,
    );
} catch (error) {
    console.error(
        error instanceof VerificationFailure
            ? error.message
            : "Loopback integration verification failed; inspect the private local service logs.",
    );
    process.exitCode = 1;
}
