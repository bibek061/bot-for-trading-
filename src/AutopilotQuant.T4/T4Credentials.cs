using AutopilotQuant.T4.Protocol;

namespace AutopilotQuant.T4;

// Server-only values. Public properties and ToString expose readiness, never credentials.
public sealed class T4Credentials(string? apiKey = null, string? firm = null, string? username = null,
    string? password = null, string? appName = null, string? appLicense = null)
{
    public bool ApiKeyPresent => !string.IsNullOrWhiteSpace(apiKey);
    public string Method => ApiKeyPresent ? "api-key"
        : new[] { firm, username, password, appName, appLicense }.Any(value => !string.IsNullOrWhiteSpace(value))
            ? "username-password" : "not-configured";
    public string[] MissingFields => ApiKeyPresent ? [] : new[] {
        ("Firm", firm), ("Username", username), ("Password", password),
        ("AppName", appName), ("AppLicense", appLicense)
    }.Where(setting => string.IsNullOrWhiteSpace(setting.Item2)).Select(setting => setting.Item1).ToArray();
    public bool Configured => ApiKeyPresent || MissingFields.Length == 0;

    internal LoginRequest CreateLoginRequest()
    {
        if (!Configured) throw new ArgumentException("Configure a T4 API key or complete simulator credentials and application license on the server.");
        // Match the official V2 credential provider: an API key takes precedence, with no
        // password fields sent alongside it and no automatic fallback after a rejected login.
        return ApiKeyPresent ? new() { ApiKey = apiKey!, PriceFormat = 1 }
            : new() { Firm = firm!, Username = username!, Password = password!,
                AppName = appName!, AppLicense = appLicense!, PriceFormat = 1 };
    }

    public override string ToString() => $"T4 credentials ({Method}; configured: {Configured})";
}
