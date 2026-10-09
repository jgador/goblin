using System;
using System.Linq;
using Goblin.Contracts;
using Goblin.Protocol;

namespace Goblin.Integrations.Codex;

internal static class AuthenticationInputPolicy
{
    internal static string Prompt(string? value)
    {
        string prompt = value?.Trim() ?? "";
        if (prompt.Length is < 1 or > 500 || prompt.Any(c => c is <= '\x08' or '\x0b' or '\x0c' or >= '\x0e' and <= '\x1f'))
            throw new IntegrationFailure("invalid_prompt", "Enter a short prompt of 1–500 characters.");
        return prompt;
    }

    internal static string ApiKey(string? value)
    {
        string apiKey = value?.Trim() ?? "";
        if (apiKey.Length is < 20 or > 4096 || apiKey.Any(c => c is < '\x21' or > '\x7e'))
            throw new IntegrationFailure("invalid_api_key", "Enter a complete OpenAI API key without spaces.");
        return apiKey;
    }

    internal static DeviceLogin? DeviceLogin(LoginAccountResponse result)
    {
        if (result is not ChatGPTDeviceCodeLoginAccountResponse device ||
            string.IsNullOrEmpty(device.UserCode) || device.UserCode.Length > 64 ||
            !Uri.TryCreate(device.VerificationUrl, UriKind.Absolute, out Uri? url) ||
            url.Scheme != "https" || url.Host != "auth.openai.com" || !url.IsDefaultPort ||
            url.AbsolutePath != "/codex/device" || url.UserInfo.Length != 0)
            return null;
        return new(device.LoginId, url.AbsoluteUri, device.UserCode);
    }
}
