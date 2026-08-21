using System.Text.Json.Serialization;

namespace enki.Services;

/// <summary>Response body of <c>POST /auth/v1/token?grant_type=password</c> and
/// <c>grant_type=refresh_token</c> — Supabase Auth (GoTrue) REST API, shared by both grant types.</summary>
internal sealed class AuthTokenDto
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("user")]
    public AuthUserDto? User { get; set; }
}

internal sealed class AuthUserDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }
}

/// <summary>Error body shape used by both GoTrue (<c>error_description</c>/<c>msg</c>) and Postgrest
/// (<c>message</c>) — read leniently since we only use it for a human-readable failure reason.</summary>
internal sealed class SupabaseErrorDto
{
    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    public string? ReadableMessage => ErrorDescription ?? Msg ?? Message;
}
