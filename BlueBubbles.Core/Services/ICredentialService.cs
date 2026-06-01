namespace BlueBubbles.Core.Services;

public interface ICredentialService
{
    void SavePassword(string password);
    string? GetPassword();
    void DeletePassword();
}
