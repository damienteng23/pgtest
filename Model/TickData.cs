using static Npgsql.Replication.PgOutput.Messages.RelationMessage;

namespace pgtest.Model
{
    public record struct TickData
    (
        string Symbol,
        string Provider,
        double Bid,
        double Ask,
        double? LastPrice,
        long? Volume,
        DateTime TickTimestamp,
        string LastUpdatedBy,
        DateTime LastUpdatedOnUtc
    );
}
