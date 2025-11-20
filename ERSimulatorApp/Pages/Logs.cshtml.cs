using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ERSimulatorApp.Pages;

public class LogsModel : PageModel
{
    private readonly ILogger<LogsModel> _logger;

    public LogsModel(ILogger<LogsModel> logger)
    {
        _logger = logger;
    }

    public void OnGet()
    {
        _logger.LogDebug("Recent activity page rendered at {Timestamp}", DateTimeOffset.UtcNow);
    }
}








