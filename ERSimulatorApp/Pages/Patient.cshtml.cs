using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ERSimulatorApp.Pages
{
    public class PatientModel : PageModel
    {
        private readonly ILogger<PatientModel> _logger;
        private readonly IConfiguration _configuration;

        public PatientModel(ILogger<PatientModel> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public IActionResult OnGet()
        {
            if (!_configuration.GetValue<bool>("PatientSupport:Enabled", false))
            {
                _logger.LogInformation("Patient avatar page disabled; redirecting to Index");
                return RedirectToPage("/Index");
            }
            _logger.LogInformation("Patient avatar page accessed");
            return Page();
        }
    }
}

