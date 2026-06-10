// Copy this file to AppSecrets.cs and fill in your EVE developer app credentials.
// AppSecrets.cs is gitignored — never commit the real values.
// Register your app at: https://developers.eveonline.com
// Redirect URI must be set to: http://localhost:7648/callback

namespace FCAT;

internal static class AppSecrets
{
    public const string ClientId = "YOUR_CLIENT_ID_HERE";
    public const string ClientSecret = "YOUR_CLIENT_SECRET_HERE";
}
