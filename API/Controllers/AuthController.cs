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
    private readonly UserManager<AppUser> _userManager;
    private readonly ITokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _config;

    public AuthController(UserManager<AppUser> userManager, ITokenService tokenService,
        IEmailService emailService, IConfiguration config)
    {
        _userManager = userManager;
        _tokenService = tokenService;
        _emailService = emailService;
        _config = config;
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponseDto>> Login(LoginDto dto)
    {
        var user = await _userManager.FindByNameAsync(dto.Username);
        if (user == null || !await _userManager.CheckPasswordAsync(user, dto.Password))
            return Unauthorized("Invalid username or password");

        return new AuthResponseDto
        {
            Token = _tokenService.CreateToken(user),
            DisplayName = user.DisplayName
        };
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
        return Ok(new { user.Email });
    }
}
