namespace Goblin.Web;

public sealed record SessionState(bool Authenticated);
public sealed record ApiFailure(ErrorView Error);
public sealed record ErrorView(string Code, string Message);
