using Microsoft.AspNetCore.Identity;
using API.Models;

namespace API.Services;

public interface IPinHasher
{
    string Hash(string pin);
    bool Verify(string hashedPin, string providedPin);
}

public class PinHasher : IPinHasher
{
    private readonly PasswordHasher<Employee> _hasher = new();

    public string Hash(string pin)
    {
        return _hasher.HashPassword(null!, pin);
    }

    public bool Verify(string hashedPin, string providedPin)
    {
        var result = _hasher.VerifyHashedPassword(null!, hashedPin, providedPin);
        return result != PasswordVerificationResult.Failed;
    }
}
