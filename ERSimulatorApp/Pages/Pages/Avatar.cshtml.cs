using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ERSimulatorApp.Pages;

public class AvatarModel : PageModel
{
    private readonly ILogger<AvatarModel> _logger;

    public AvatarModel(ILogger<AvatarModel> logger)
    {
        _logger = logger;
    }

    public void OnGet()
    {
        _logger.LogInformation("Avatar page accessed");
    }
}
