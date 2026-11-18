using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FlowerShop.Server.Data;
using Microsoft.IdentityModel.Tokens;

namespace FlowerShop.Server;

public static class PasswordHasher
{
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 2) return false;
        var salt = Convert.FromBase64String(parts[0]);
        var expected = Convert.FromBase64String(parts[1]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public sealed class TokenService(IConfiguration cfg)
{
    public static TokenValidationParameters Validation(IConfiguration c) => new()
    {
        ValidIssuer = c["Jwt:Issuer"], ValidAudience = c["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(c["Jwt:Key"]!)),
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
        RoleClaimType = "role", NameClaimType = "name", ClockSkew = TimeSpan.FromMinutes(1)
    };

    public string Issue(User u)
    {
        var claims = new[]
        {
            new Claim("sub", u.Id.ToString()), new Claim("email", u.Email),
            new Claim("name", u.FullName), new Claim("role", u.Role)
        };
        var creds = new SigningCredentials(Validation(cfg).IssuerSigningKey, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(cfg["Jwt:Issuer"], cfg["Jwt:Audience"], claims,
            expires: DateTime.UtcNow.AddHours(double.Parse(cfg["Jwt:Hours"] ?? "8")), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
