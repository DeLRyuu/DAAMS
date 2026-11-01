namespace ControlCenter.ViewModels;

/// <summary>
/// Minimal ViewModel used by sections whose real implementation is scheduled for a
/// later phase (Phases 3–7). Phase 1's job is only to prove navigation + shell work;
/// it deliberately does NOT pretend these sections are feature-complete.
/// </summary>
public class SectionPlaceholderViewModel : ViewModelBase
{
    public string Title { get; }
    public string Description { get; }
    public string PlannedPhase { get; }

    public SectionPlaceholderViewModel(string title, string description, string plannedPhase)
    {
        Title = title;
        Description = description;
        PlannedPhase = plannedPhase;
    }
}
