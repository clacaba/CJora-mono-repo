namespace CJora.Transactions.Domain;

public sealed record CustomerReference(Guid CustomerUuid,Guid ProgramUuid,Guid UserUuid,long SourceVersion,string Status);
public enum SnapshotDecision { Apply, Stale, Conflict }
public static class CustomerProjection
{
    public static SnapshotDecision Decide(CustomerReference? current,CustomerReference incoming)
    {
        if(current is null) return SnapshotDecision.Apply;
        if(current.CustomerUuid!=incoming.CustomerUuid || current.ProgramUuid!=incoming.ProgramUuid || current.UserUuid!=incoming.UserUuid)
            return SnapshotDecision.Conflict;
        if(incoming.SourceVersion<current.SourceVersion) return SnapshotDecision.Stale;
        if(incoming.SourceVersion==current.SourceVersion)
            return current.Status==incoming.Status?SnapshotDecision.Stale:SnapshotDecision.Conflict;
        return SnapshotDecision.Apply;
    }
}
