using System.Text.Json;
using GitHubToDo.Core;
using Windows.Security.Credentials;

namespace GitHubToDo.Services;

public sealed class WindowsCredentialStore : ICredentialStore
{
    private const string Resource = "GitHubToDo.GitHub.com.OAuth";
    private const string UserName = "active-account";
    private readonly PasswordVault _vault = new();

    public OAuthToken? Load()
    {
        PasswordCredential credential;
        try { credential = _vault.Retrieve(Resource, UserName); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490)) { return null; }
        credential.RetrievePassword();
        return JsonSerializer.Deserialize(credential.Password, CoreJsonContext.Default.OAuthToken);
    }

    public void Save(OAuthToken token) =>
        _vault.Add(new PasswordCredential(Resource, UserName, JsonSerializer.Serialize(token, CoreJsonContext.Default.OAuthToken)));

    public void Clear()
    {
        try { _vault.Remove(_vault.Retrieve(Resource, UserName)); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070490)) { }
    }
}
