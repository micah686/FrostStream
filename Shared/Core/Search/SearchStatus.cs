namespace DataBridge.Search;

public enum SearchState { Building, Ready, Degraded, Failed }
public sealed record SearchStatusSnapshot(SearchState State, string? Error = null);
public sealed record SearchStatusRequest;
public sealed class SearchStatus
{
    private SearchStatusSnapshot _current = new(SearchState.Building);
    public SearchStatusSnapshot Current => Volatile.Read(ref _current);
    private int _hasReadyGeneration;
    public bool HasReadyGeneration => Volatile.Read(ref _hasReadyGeneration) == 1;
    public void Set(SearchState state, string? error = null)
    {
        if (state == SearchState.Ready) Volatile.Write(ref _hasReadyGeneration, 1);
        Volatile.Write(ref _current, new(state, error));
    }
    public void ReportFailure(string error) => Set(HasReadyGeneration ? SearchState.Degraded : SearchState.Failed, error);
}
