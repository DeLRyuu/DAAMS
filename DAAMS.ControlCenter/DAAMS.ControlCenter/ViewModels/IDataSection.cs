namespace ControlCenter.ViewModels;

/// <summary>
/// Implemented by every section ViewModel that loads its data from Firestore.
/// MainViewModel calls EnsureLoaded() the first time a section becomes active,
/// so data loads lazily on first visit rather than all six sections querying
/// Firestore at once on startup.
/// </summary>
public interface IDataSection
{
    /// <summary>Starts the first load if one hasn't happened yet. Safe to call repeatedly.</summary>
    void EnsureLoaded();
}
