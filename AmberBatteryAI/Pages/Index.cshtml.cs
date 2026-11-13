using AmberBatteryAI.Models;
using AmberBatteryAI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AmberBatteryAI.Pages;

public class IndexModel(OptimizerState state) : PageModel
{
    public StatusSnapshot Status { get; private set; } = default!;

    public void OnGet()
    {
        Status = state.GetSnapshot();
    }

    public IActionResult OnPostRunNow()
    {
        state.TriggerImmediateRun();
        return RedirectToPage();
    }
}
