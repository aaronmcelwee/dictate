using Windows.Security.Credentials;

namespace GrokVoice.Platform;

/// <summary>An API key (xAI by default), kept in Windows Credential Manager for the signed-in user.</summary>
internal sealed class ApiKeyStore(string userName = "xai-api-key", string resource = "Grok Voice")
{
    // Each key gets its own resource: Credential Locker treats a resource as one slot in practice,
    // and saving a second key under "Grok Voice" lost the xAI key.
    private readonly string Resource = resource;
    private readonly string UserName = userName;

    private readonly PasswordVault _vault = new();
    private string? _cached;
    private bool _loaded;

    public string? Get()
    {
        if (_loaded) return _cached;
        try
        {
            var credential = _vault.Retrieve(Resource, UserName);
            credential.RetrievePassword();
            _cached = credential.Password;
        }
        catch (Exception)
        {
            // Retrieve throws when nothing is stored.
            _cached = null;
        }
        // Keys saved before each key had its own resource sit under "Grok Voice": move them over.
        if (_cached is null && Resource != "Grok Voice")
        {
            try
            {
                var old = _vault.Retrieve("Grok Voice", UserName);
                old.RetrievePassword();
                var key = old.Password;
                _vault.Add(new PasswordCredential(Resource, UserName, key));
                _vault.Remove(old);
                _cached = key;
            }
            catch (Exception) { }
        }
        _loaded = true;
        return _cached;
    }

    public void Set(string key)
    {
        Clear();
        _vault.Add(new PasswordCredential(Resource, UserName, key));
        _cached = key;
        _loaded = true;
    }

    public void Clear()
    {
        try
        {
            foreach (var c in _vault.FindAllByResource(Resource))
                if (c.UserName == UserName) _vault.Remove(c);
        }
        catch (Exception)
        {
            // FindAllByResource throws when there is nothing to remove.
        }
        _cached = null;
        _loaded = true;
    }
}
