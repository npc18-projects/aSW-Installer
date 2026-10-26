using System.Security.Cryptography;
using System.Text;

namespace AStoryWealth.Core;

/// <summary>
/// Stores the Nexus API key (from SSO) encrypted with DPAPI, scoped to the current
/// Windows user. Plaintext key never touches disk. Once Nexus app authorization
/// replaces personal keys, SSO hands the key here instead of an env var.
/// </summary>
public static class SecretStore
{
    private static string PathFor(string name) =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AStoryWealth", $"{name}.secret");

    public static void Save(string name, string secret)
    {
        var dir = System.IO.Path.GetDirectoryName(PathFor(name))!;
        Directory.CreateDirectory(dir);
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(name), enc);
    }

    /// <summary>Null if not stored or unreadable (e.g. restored from another user profile).</summary>
    public static string? Load(string name)
    {
        try
        {
            if (!File.Exists(PathFor(name))) return null;
            var dec = ProtectedData.Unprotect(File.ReadAllBytes(PathFor(name)), null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Delete(string name)
    {
        if (File.Exists(PathFor(name))) File.Delete(PathFor(name));
    }
}
