using API.DTOs;
using API.Models;
using API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    /// <summary>Identity user-claim type carrying the hashed security PIN —
    /// stored in AspNetUserClaims, so no schema migration was needed.</summary>
    private const string AdminPinClaimType = "admin_pin_hash";

    private readonly UserManager<AppUser> _userManager;
    private readonly ITokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _config;
    private readonly IPinHasher _pinHasher;

    public AuthController(UserManager<AppUser> userManager, ITokenService tokenService,
        IEmailService emailService, IConfiguration config, IPinHasher pinHasher)
    {
        _userManager = userManager;
        _tokenService = tokenService;
        _emailService = emailService;
        _config = config;
        _pinHasher = pinHasher;
    }

    /// <summary>
    /// Login, optionally two-step. When the account has a security PIN set
    /// (customer: password alone feels guessable), the first call returns
    /// PinRequired=true WITHOUT a token; the client repeats the call with
    /// Pin filled in. Wrong PINs count into Identity's lockout, so the PIN
    /// can't be brute-forced any more than the password can.
    /// </summary>
    [HttpPost("login")]
    public async Task<ActionResult<AuthResponseDto>> Login(LoginDto dto)
    {
        var user = await _userManager.FindByNameAsync(dto.Username);
        if (user == null || !await _userManager.CheckPasswordAsync(user, dto.Password))
            return Unauthorized("Invalid username or password");

        var pinHash = (await _userManager.GetClaimsAsync(user))
            .FirstOrDefault(c => c.Type == AdminPinClaimType)?.Value;
        if (!string.IsNullOrEmpty(pinHash))
        {
            if (await _userManager.IsLockedOutAsync(user))
                return Unauthorized("Účet je dočasne zamknutý po opakovaných nesprávnych pokusoch. Skúste o pár minút.");
            if (string.IsNullOrEmpty(dto.Pin))
                return new AuthResponseDto { PinRequired = true, DisplayName = user.DisplayName };
            if (!_pinHasher.Verify(pinHash, dto.Pin))
            {
                await _userManager.AccessFailedAsync(user);
                return Unauthorized("Nesprávny PIN.");
            }
            await _userManager.ResetAccessFailedCountAsync(user);
        }

        return new AuthResponseDto
        {
            Token = _tokenService.CreateToken(user),
            DisplayName = user.DisplayName
        };
    }

    /// <summary>
    /// Set or change the security PIN. Changing requires the previous PIN
    /// (customer rule) — being logged in alone isn't enough.
    /// </summary>
    [Authorize]
    [HttpPut("admin-pin")]
    public async Task<IActionResult> UpdateAdminPin(UpdateAdminPinDto dto)
    {
        var user = await _userManager.FindByNameAsync(User.Identity!.Name!);
        if (user == null) return Unauthorized();

        var claims = await _userManager.GetClaimsAsync(user);
        var existing = claims.FirstOrDefault(c => c.Type == AdminPinClaimType);
        if (existing != null)
        {
            if (string.IsNullOrEmpty(dto.CurrentPin) || !_pinHasher.Verify(existing.Value, dto.CurrentPin))
                return BadRequest("Aktuálny PIN je nesprávny.");
            await _userManager.RemoveClaimAsync(user, existing);
        }
        await _userManager.AddClaimAsync(user,
            new System.Security.Claims.Claim(AdminPinClaimType, _pinHasher.Hash(dto.NewPin)));
        return Ok();
    }

    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordDto dto)
    {
        var user = await _userManager.FindByNameAsync(dto.Username);
        // Always return OK to avoid username enumeration
        if (user == null || string.IsNullOrEmpty(user.Email))
            return Ok();

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var encodedToken = Uri.EscapeDataString(token);
        var appUrl = _config["AppUrl"] ?? "http://localhost:4200";
        var resetLink = $"{appUrl}/reset-password?username={Uri.EscapeDataString(user.UserName!)}&token={encodedToken}";

        await _emailService.SendPasswordResetAsync(user.Email, user.DisplayName, resetLink);
        return Ok();
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordDto dto)
    {
        var user = await _userManager.FindByNameAsync(dto.Username);
        if (user == null) return BadRequest("Neplatný odkaz na obnovenie hesla.");

        var result = await _userManager.ResetPasswordAsync(user, dto.Token, dto.NewPassword);
        if (!result.Succeeded)
            return BadRequest("Odkaz na obnovenie hesla je neplatný alebo vypršal.");

        return Ok();
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordDto dto)
    {
        var user = await _userManager.FindByNameAsync(User.Identity!.Name!);
        if (user == null) return Unauthorized();

        var result = await _userManager.ChangePasswordAsync(user, dto.CurrentPassword, dto.NewPassword);
        if (!result.Succeeded)
            return BadRequest("Aktuálne heslo je nesprávne.");

        return Ok();
    }

    /// <summary>
    /// Change the display name shown in the navbar/kiosk (customer request:
    /// "Manažér" → "Vladimír Sroka"). Self-service so it never needs a
    /// developer again.
    /// </summary>
    [Authorize]
    [HttpPut("display-name")]
    public async Task<IActionResult> UpdateDisplayName(UpdateDisplayNameDto dto)
    {
        var user = await _userManager.FindByNameAsync(User.Identity!.Name!);
        if (user == null) return Unauthorized();

        user.DisplayName = dto.DisplayName.Trim();
        await _userManager.UpdateAsync(user);
        return Ok();
    }

    [Authorize]
    [HttpPut("recovery-email")]
    public async Task<IActionResult> UpdateRecoveryEmail(UpdateRecoveryEmailDto dto)
    {
        var user = await _userManager.FindByNameAsync(User.Identity!.Name!);
        if (user == null) return Unauthorized();

        await _userManager.SetEmailAsync(user, dto.Email);
        return Ok();
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<object>> GetMe()
    {
        var user = await _userManager.FindByNameAsync(User.Identity!.Name!);
        if (user == null) return Unauthorized();
        var hasAdminPin = (await _userManager.GetClaimsAsync(user))
            .Any(c => c.Type == AdminPinClaimType);
        return Ok(new { user.Email, user.UserName, hasAdminPin });
    }
}
