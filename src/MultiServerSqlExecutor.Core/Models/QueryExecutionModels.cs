using System.Data;

namespace MultiServerSqlExecutor.Core.Models;

public enum QueryExecutionStatus
{
    NotStarted,
    Connected,
    Running,
    Completed,
    Errored
}

public sealed class ServerExecutionStatusUpdate
{
    public required ServerConnection Server { get; init; }
    public required QueryExecutionStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class ServerExecutionResult
{
    public required ServerConnection Server { get; init; }
    public DataTable? Data { get; init; }
    public Exception? Error { get; init; }
    public bool Succeeded => Error is null && Data is not null;
}
