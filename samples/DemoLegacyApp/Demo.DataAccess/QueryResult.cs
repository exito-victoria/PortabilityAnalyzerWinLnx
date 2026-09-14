namespace Demo.DataAccess;

/// <summary>DTO portable puro (sin dependencias de Windows): debe quedar en Demo.DataAccess.Core.</summary>
public sealed record QueryResult(int Rows, string Message);
