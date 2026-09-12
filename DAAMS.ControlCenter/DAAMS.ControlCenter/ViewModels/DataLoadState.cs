namespace ControlCenter.ViewModels;

/// <summary>
/// Load state for a Firestore-backed section. Every data view binds its
/// loading/error/empty overlay visibility to one of these values via
/// DataLoadStateToVisibilityConverter.
/// </summary>
public enum DataLoadState
{
    Loading,
    Loaded,
    Empty,
    Error
}
