using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FCAT.Models;

namespace FCAT.Services;

/// <summary>
/// Persists the FC's authorized characters — including their ESI refresh tokens — encrypted with
/// Windows DPAPI (CurrentUser) at %APPDATA%/FCAT/characters.dat. The file is never plaintext and
/// is bound to the logged-in Windows account, so copying it to another machine/user won't decrypt.
/// </summary>
public class CharacterStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FCAT", "characters.dat");

    public List<StoredCharacter> Characters { get; private set; } = [];

    /// <summary>The character marked active (or the first one if none is flagged).</summary>
    public StoredCharacter? Active =>
        Characters.FirstOrDefault(c => c.IsActive) ?? Characters.FirstOrDefault();

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) { Characters = []; return; }
            var enc  = File.ReadAllBytes(FilePath);
            var json = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            Characters = JsonSerializer.Deserialize<List<StoredCharacter>>(json) ?? [];
        }
        catch { Characters = []; }   // corrupt/unreadable → start clean; the FC just re-adds characters
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.SerializeToUtf8Bytes(Characters);
            var enc  = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, enc);
        }
        catch { /* best-effort; a failed save just means re-auth next launch */ }
    }

    /// <summary>Add or replace a character (keeps the existing role label on re-auth).</summary>
    public void Upsert(StoredCharacter ch)
    {
        var existing = Characters.FirstOrDefault(c => c.CharacterId == ch.CharacterId);
        if (existing != null)
        {
            ch.Role     = existing.Role;       // don't clobber an FC-assigned role on re-login
            ch.IsActive = existing.IsActive;
            Characters.Remove(existing);
        }
        Characters.Add(ch);
        Save();
    }

    public void Remove(int characterId)
    {
        Characters.RemoveAll(c => c.CharacterId == characterId);
        Save();
    }

    public void SetActive(int characterId)
    {
        foreach (var c in Characters) c.IsActive = c.CharacterId == characterId;
        Save();
    }

    public void SetRole(int characterId, string role)
    {
        var c = Characters.FirstOrDefault(x => x.CharacterId == characterId);
        if (c != null) { c.Role = role; Save(); }
    }
}
