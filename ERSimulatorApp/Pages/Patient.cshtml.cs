using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ERSimulatorApp.Pages
{
    public class PatientModel : PageModel
    {
        private readonly ILogger<PatientModel> _logger;

        public PatientModel(ILogger<PatientModel> logger)
        {
            _logger = logger;
        }

        public void OnGet()
        {
            _logger.LogInformation("Patient avatar page accessed");
        }
    }
}

