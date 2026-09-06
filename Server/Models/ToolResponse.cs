namespace Portster;

public sealed record ToolResponse<T>(string OperationId, T? Data, ToolError? Error);
