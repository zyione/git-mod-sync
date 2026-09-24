using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ModSync.Utils;

namespace ModSync.Services;

/// <summary>
/// Handles secure GitHub authentication for push operations.
/// Plaintext tokens are NEVER stored in config.json or on disk;
/// credentials are saved securely in Windows Credential Manager.
/// </summary>
public class AuthenticationService
{
    private const string CredentialKey = "Main";
    private readonly LoggingService _logger;
    private static readonly HttpClient HttpClient = new();

    static AuthenticationService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ModSync-Minecraft", "1.0"));
    }

    public AuthenticationService(LoggingService logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the currently stored GitHub token from Windows Credential Manager.
    /// </summary>
    public string? GetStoredToken()
    {
        var (_, token) = CredentialUtils.LoadCredential(CredentialKey);
        return token;
    }

    /// <summary>
    /// Checks if a valid GitHub credential exists and verifies it against the GitHub API.
    /// </summary>
    public async Task<(bool IsValid, string? Username, string? Message)> CheckAuthStatusAsync()
    {
        var (username, token) = CredentialUtils.LoadCredential(CredentialKey);
        if (string.IsNullOrEmpty(token))
        {
            return (false, null, "Not logged in. (Required only for pushing mod updates).");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await HttpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                string ghUser = doc.RootElement.GetProperty("login").GetString() ?? username ?? "Unknown";

                _logger.Info($"Authenticated session confirmed for user: {ghUser}");
                return (true, ghUser, $"Logged in as: {ghUser}");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.Warning("Stored GitHub token is expired or invalid.");
                return (false, null, "Stored login token has expired or is invalid.");
            }

            return (false, null, $"GitHub check returned: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to verify GitHub token", ex);
            return (true, username ?? "Offline Admin", "Offline (saved credentials present).");
        }
    }

    /// <summary>
    /// Logs in using a Personal Access Token (PAT), verifies it, and securely saves it in Windows Credential Manager.
    /// </summary>
    public async Task<(bool Success, string? Username, string? Error)> LoginWithTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (false, null, "Token cannot be empty.");

        string trimmedToken = token.Trim();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", trimmedToken);

            using var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    return (false, null, "Invalid GitHub token. Please verify your token and try again.");
                }
                return (false, null, $"GitHub returned error {response.StatusCode}.");
            }

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            string ghUser = doc.RootElement.GetProperty("login").GetString() ?? "Admin";

            // Save in Windows Credential Manager
            bool saved = CredentialUtils.SaveCredential(CredentialKey, ghUser, trimmedToken);
            if (!saved)
            {
                return (false, null, "Failed to save credential to Windows Credential Manager.");
            }

            _logger.Info($"GitHub login successful for user: {ghUser}");
            return (true, ghUser, null);
        }
        catch (Exception ex)
        {
            _logger.Error("Exception during GitHub login", ex);
            return (false, null, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears the stored credentials from Windows Credential Manager.
    /// </summary>
    public bool Logout()
    {
        _logger.Info("Logging out: deleting credentials from Windows Credential Manager.");
        return CredentialUtils.DeleteCredential(CredentialKey);
    }
}
