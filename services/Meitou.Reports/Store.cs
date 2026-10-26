using Npgsql;
using NpgsqlTypes;

namespace Meitou.Reports;

/// <summary>One play report: a roadmap row seen working or broken on a build.</summary>
sealed record Report(
    long Id, DateTimeOffset At, string Row, string Verdict, string Build, string Platform, bool? Modded,
    string? Note, string Source, string Reporter, bool Trusted, bool HasLog);

/// <summary>Counts for one row since a cutoff: distinct reporters per verdict, and the newest report.</summary>
sealed record RowSummary(string Row, int Works, int Broken, bool TrustedWorks, string LastVerdict, string LastBuild, DateTimeOffset LastAt);

/// <summary>The reports table in PostgreSQL. Reporters are only ever stored as salted hashes.</summary>
sealed class Store(NpgsqlDataSource db)
{
    public async Task Migrate()
    {
        await using var cmd = db.CreateCommand("""
            create table if not exists reports (
                id bigserial primary key,
                at timestamptz not null default now(),
                row_id text not null,
                verdict text not null check (verdict in ('works', 'broken')),
                build text not null,
                platform text not null,
                modded boolean,
                note text,
                source text not null,
                reporter text not null,
                trusted boolean not null default false,
                log bytea
            );
            create index if not exists reports_row_at on reports (row_id, at);
            create index if not exists reports_reporter_at on reports (reporter, at);
            """);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> Add(string row, string verdict, string build, string platform, bool? modded, string? note, string source,
        string reporter, bool trusted, byte[]? gzippedLog)
    {
        await using var cmd = db.CreateCommand("""
            insert into reports (row_id, verdict, build, platform, modded, note, source, reporter, trusted, log)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) returning id
            """);
        cmd.Parameters.Add(new() { Value = row });
        cmd.Parameters.Add(new() { Value = verdict });
        cmd.Parameters.Add(new() { Value = build });
        cmd.Parameters.Add(new() { Value = platform });
        cmd.Parameters.Add(new() { Value = (object?)modded ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean });
        cmd.Parameters.Add(new() { Value = (object?)note ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = source });
        cmd.Parameters.Add(new() { Value = reporter });
        cmd.Parameters.Add(new() { Value = trusted });
        cmd.Parameters.Add(new() { Value = (object?)gzippedLog ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bytea });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>How many reports <paramref name="reporter"/> made since <paramref name="since"/> (a per-person limit on top of the per-IP one).</summary>
    public async Task<int> CountBy(string reporter, DateTimeOffset since)
    {
        await using var cmd = db.CreateCommand("select count(*) from reports where reporter = $1 and at >= $2");
        cmd.Parameters.Add(new() { Value = reporter });
        cmd.Parameters.Add(new() { Value = since });
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>The counts per row over the reports <paramref name="w"/> lets count (a Broken one dated by its build's release, else its own time).</summary>
    public async Task<List<RowSummary>> Summary(Window w)
    {
        await using var cmd = db.CreateCommand("""
            with releases as (select * from unnest($2::text[], $3::timestamptz[]) as t(build, published)),
            counted as (
                select r.* from reports r left join releases using (build)
                where r.at >= $1 and (r.verdict = 'works' or $4::timestamptz is null or coalesce(published, r.at) >= $4))
            select row_id,
                   count(distinct reporter) filter (where verdict = 'works'),
                   count(distinct reporter) filter (where verdict = 'broken'),
                   coalesce(bool_or(trusted and verdict = 'works'), false),
                   (array_agg(verdict order by at desc))[1],
                   (array_agg(build order by at desc))[1],
                   max(at)
            from counted group by row_id order by row_id
            """);
        cmd.Parameters.Add(new() { Value = w.Since });
        cmd.Parameters.Add(new() { Value = w.Builds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = w.Published, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new() { Value = (object?)w.NewestFull ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        var list = new List<RowSummary>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new(r.GetString(0), (int)r.GetInt64(1), (int)r.GetInt64(2), r.GetBoolean(3), r.GetString(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6)));
        return list;
    }

    public async Task<List<Report>> List(DateTimeOffset since, string? row, int limit)
    {
        await using var cmd = db.CreateCommand("""
            select id, at, row_id, verdict, build, platform, modded, note, source, reporter, trusted, log is not null
            from reports where at >= $1 and ($2::text is null or row_id = $2) order by at desc limit $3
            """);
        cmd.Parameters.Add(new() { Value = since });
        cmd.Parameters.Add(new() { Value = (object?)row ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = limit });
        var list = new List<Report>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.GetString(9), r.GetBoolean(10), r.GetBoolean(11)));
        return list;
    }

    public async Task<byte[]?> Log(long id)
    {
        await using var cmd = db.CreateCommand("select log from reports where id = $1");
        cmd.Parameters.Add(new() { Value = id });
        return await cmd.ExecuteScalarAsync() as byte[];
    }

    public async Task<bool> Delete(long id)
    {
        await using var cmd = db.CreateCommand("delete from reports where id = $1");
        cmd.Parameters.Add(new() { Value = id });
        return await cmd.ExecuteNonQueryAsync() > 0;
    }
}
