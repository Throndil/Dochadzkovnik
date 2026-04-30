using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using API.Models;
using Microsoft.IdentityModel.Tokens;

namespace API.Services;

public interface ITokenService
{
    string CreateToken(AppUser user);
}

public class TokenService : ITokenService
{
    private readonly IConfiguration _config;

    public TokenService(IConfiguration config)
    {
        _config = config;
    }

    public string CreateToken(AppUser user)
    {
        var key = _config["Jwt:Key"] ?? throw new InvalidOperationException("JWT key not configured");
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.UserName ?? string.Empty),
            new Claim("displayName", user.DisplayName)
        };

        // Superadmin marker — username-based, configurable via SuperAdminSeed:Username.
        // Used by FeatureFlagsController and the frontend to gate hidden features.
        // No hardcoded fallback: if the config is missing, NO user is considered superadmin.
        var superAdminUsername = _config["SuperAdminSeed:Username"];
        if (!string.IsNullOrWhiteSpace(superAdminUsername)
            && string.Equals(user.UserName, superAdminUsername, StringComparison.OrdinalIgnoreCase))
        {
            claims.Add(new Claim("isSuperAdmin", "true"));
        }

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(12),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
