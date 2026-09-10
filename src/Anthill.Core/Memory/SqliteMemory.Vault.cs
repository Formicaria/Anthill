namespace Anthill.Core.Memory;

/// <summary>One record in the colony's memory, whatever kind of thing it is.</summary>
public sealed record VaultRecord
{
    /// <summary>`kind:rowid` — stable, and the only handle every layer above this uses.</summary>
    public required string Id { get; init; }

    public required string Kind { get; init; }

    /// <summary>The chamber this record LIVES in. One home each — see <see cref="VaultChambers"/>.</summary>
    public required string Chamber { get; init; }

    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? WhenUtc { get; init; }

    /// <summary>How it went, where the colony recorded a verdict. Null where the kind has none —
    /// never a cheerful default, because the brightness of a dot is read as an answer.</summary>
    public string? Outcome { get; init; }

    public string? ProjectId { get; init; }
    public string? MissionId { get; init; }

    /// <summary>
    /// Which second-level folder this record fell into under the CURRENT grouping. v0.3.9.
    ///
    /// Sent with the record rather than re-derived in the console, and that is the whole reason it
    /// exists: the chamber seats a dot in the cluster its group names, the tree files it in the
    /// folder its group names, and if those two were computed separately they would eventually
    /// disagree about where the same record belongs — the one thing a vault must never do.
    /// </summary>
    public string? Group { get; init; }

    /// <summary>The console page that shows the whole thing, when one exists.</summary>
    public string? Href { get; init; }
}

/// <summary>A page of the vault, with the counts the tree needs to draw its folders.</summary>
public sealed record VaultPage(
    IReadOnlyList<VaultRecord> Records,
    int Total,
    IReadOnlyList<VaultGroupCount> Kinds,
    IReadOnlyList<VaultGroupCount> Groups);

public sealed record VaultGroupCount(string Key, string Label, int Count);

/// <summary>
/// One edge in the local graph. <paramref name="Derived"/> separates the four relations the colony
/// RECORDED from the one this layer infers — see <see cref="SqliteMemory.VaultLinks"/>.
/// </summary>
public sealed record VaultLink(string Id, string Kind, string Title, string Relation, bool Derived);

/// <summary>
/// WHICH CHAMBER A RECORD LIVES IN, SPELLED ONCE. v0.3.9.
///
/// The operator's rule is one home each: a record belongs to the chamber that produced it, and the
/// colony's anatomy becomes the index. This mapping is therefore load-bearing for the 3D view AND
/// for the sidebar's counts, which is exactly the shape that must not exist twice — a console that
/// re-derived it would eventually put a dot in a chamber the server did not count it in.
/// </summary>
public static class VaultChambers
{
    public const string Queen = "queen";
    public const string Intel = "intel";
    public const string Forge = "forge";
    public const string Valid = "valid";
    public const string Memory = "memory";
    public const string Output = "output";

    /// <summary>
    /// The role→chamber map, which is the roster's own shape rather than a preference: the
    /// research roles gather, the making roles make, the checking roles check, the answering roles
    /// answer. A role added to the roster without a line here lands in Memory, which is the honest
    /// fallback — it is remembered, and nothing claims to know where it was made.
    /// </summary>
    public static string ForRole(string? role) => (role ?? "").Trim().ToLowerInvariant() switch
    {
        "queen" or "planner" or "constraint" or "director" => Queen,
        "researcher" or "web" or "file" or "archivist" => Intel,
        "coder" or "ui_cartographer" => Forge,
        "tester" or "verifier" or "soldier" or "medic" => Valid,
        "builder" or "scribe" => Output,
        _ => Memory,
    };

    /// <summary>The SQL fragment for the same map, so a task's chamber is decided by ONE rule
    /// whether it is asked in C# or in a projection. Kept beside `ForRole` for the same reason the
    /// citation vocabulary is kept beside its parser.</summary>
    public const string RoleCase = @"CASE LOWER(COALESCE(assigned_ant,''))
            WHEN 'queen' THEN 'queen' WHEN 'planner' THEN 'queen' WHEN 'constraint' THEN 'queen' WHEN 'director' THEN 'queen'
            WHEN 'researcher' THEN 'intel' WHEN 'web' THEN 'intel' WHEN 'file' THEN 'intel' WHEN 'archivist' THEN 'intel'
            WHEN 'coder' THEN 'forge' WHEN 'ui_cartographer' THEN 'forge'
            WHEN 'tester' THEN 'valid' WHEN 'verifier' THEN 'valid' WHEN 'soldier' THEN 'valid' WHEN 'medic' THEN 'valid'
            WHEN 'builder' THEN 'output' WHEN 'scribe' THEN 'output'
            ELSE 'memory' END";
}

/// <summary>
/// THE VAULT: every kind of thing the colony remembers, projected into one shape. v0.3.9.
///
/// WHY ONE PROJECTION AND NOT TWELVE ENDPOINTS. The sidebar asks questions that cross kinds —
/// "everything about deployment", "everything from Tuesday that failed" — and a UI that asked
/// twelve endpoints and merged the answers would be sorting, filtering and paging in the browser
/// over material the database is built to sort, filter and page. It would also drift: twelve
/// queries eventually disagree about what "recent" means.
///
/// MEASURED BEFORE IT WAS PROMISED. The operator's colony holds ~15,000 records worth a dot across
/// these tables (8.4k events, 543 artifacts, 505 tasks, 159 trails, 97 missions, the rest smaller).
/// A UNION over that is a scan of a small database, not a reporting problem — which is why this is
/// one statement rather than a materialized index that would then need keeping true.
///
/// READ-ONLY, and structurally so: every method here is a SELECT.
/// </summary>
public sealed partial class SqliteMemory
{
    /// <summary>
    /// The kinds, in the order the tree shows them: what the colony learned first, the machinery
    /// last. The tree's own ordering, decided once here rather than in the console.
    /// </summary>
    public static readonly IReadOnlyList<string> VaultKinds = new[]
    {
        "mission", "artifact", "trail", "skill", "conversation", "turn",
        "result", "evidence", "task", "event",
    };

    public static string VaultKindLabel(string kind) => kind switch
    {
        "mission" => "Missions",
        "artifact" => "Artifacts",
        "trail" => "Pheromone trails",
        "skill" => "Skills",
        "conversation" => "Conversations",
        "turn" => "Conversation turns",
        "result" => "Task results",
        "evidence" => "Evidence",
        "task" => "Tasks",
        "event" => "Events",
        "knowledge" => "Knowledge consulted",
        _ => kind,
    };

    /// <summary>
    /// The projection. Every row is `(id, kind, chamber, title, subtitle, when, outcome, project, mission)`.
    ///
    /// TITLES ARE TRIMMED IN SQL rather than in the reader, because the tree renders thousands of
    /// them and a goal can be a page long: the cost of carrying the untrimmed text is paid on every
    /// row, and nothing above this needs it — the card fetches the full record when one is opened.
    /// </summary>
    private const string VaultProjection = @"
      WITH vault AS (
        SELECT 'mission:' || id AS id, 'mission' AS kind, 'queen' AS chamber,
               SUBSTR(COALESCE(goal,''), 1, 200) AS title,
               COALESCE(status,'') AS subtitle, created_at AS when_utc,
               COALESCE(NULLIF(outcome_code,''), status) AS outcome,
               COALESCE(project_id,'') AS project_id, id AS mission_id
        FROM missions
        UNION ALL
        SELECT 'task:' || t.id, 'task', " + VaultChambers.RoleCase + @",
               SUBSTR(COALESCE(t.title,''), 1, 200),
               COALESCE(t.assigned_ant,'') || ' · ' || COALESCE(t.task_type,''), t.created_at,
               COALESCE(NULLIF(t.outcome_code,''), t.status),
               COALESCE((SELECT project_id FROM missions m WHERE m.id = t.mission_id),''), t.mission_id
        FROM tasks t
        UNION ALL
        SELECT 'artifact:' || a.id, 'artifact', 'output',
               COALESCE(a.schema,'') , COALESCE(a.producer_role,''), a.created_at, NULL,
               COALESCE((SELECT project_id FROM missions m WHERE m.id = a.mission_id),''), a.mission_id
        FROM artifacts a
        UNION ALL
        SELECT 'evidence:' || e.id, 'evidence', 'valid',
               COALESCE(e.kind,''),
               CASE WHEN e.passed = 1 THEN 'passed' ELSE 'not passed' END, e.created_at,
               CASE WHEN e.passed = 1 THEN 'passed' ELSE 'failed' END,
               COALESCE((SELECT project_id FROM missions m WHERE m.id = e.mission_id),''), e.mission_id
        FROM evidence e
        UNION ALL
        SELECT 'result:' || r.task_id, 'result', 'valid',
               SUBSTR(COALESCE(r.summary,''), 1, 200), COALESCE(r.ant_name,''), r.recorded_at,
               CASE WHEN r.success = 1 THEN 'succeeded' ELSE COALESCE(NULLIF(r.failure_class,''),'failed') END,
               COALESCE((SELECT project_id FROM missions m WHERE m.id = r.mission_id),''), r.mission_id
        FROM task_results r
        UNION ALL
        SELECT 'event:' || v.id, 'event', 'memory',
               COALESCE(v.event_type,''), SUBSTR(COALESCE(v.message,''), 1, 200), v.created_at, NULL,
               COALESCE((SELECT project_id FROM missions m WHERE m.id = v.mission_id),''), v.mission_id
        FROM events v
        UNION ALL
        SELECT 'trail:' || p.id, 'trail', 'memory',
               COALESCE(p.trail_key,''), COALESCE(p.trail_type,''), p.last_updated,
               CASE WHEN p.success_count > p.failure_count THEN 'succeeded'
                    WHEN p.failure_count > p.success_count THEN 'failed' ELSE 'partial' END,
               '', NULL
        FROM pheromone_trails p WHERE p.legacy = 0
        UNION ALL
        SELECT 'skill:' || s.id, 'skill', 'memory',
               COALESCE(s.id,''), SUBSTR(COALESCE(s.purpose,''), 1, 200), s.saved_at,
               COALESCE(s.status,''), '', NULL
        FROM skills s
        UNION ALL
        SELECT 'conversation:' || c.id, 'conversation', 'intel',
               COALESCE(NULLIF(c.title,''), c.id), COALESCE(c.role,''), c.created_at, NULL,
               COALESCE(c.project_id,''), NULL
        FROM conversations c
        UNION ALL
        SELECT 'turn:' || u.id, 'turn', 'intel',
               SUBSTR(COALESCE(u.content,''), 1, 200), COALESCE(u.role,''), u.created_at, NULL,
               COALESCE((SELECT project_id FROM conversations c2 WHERE c2.id = u.conversation_id),''),
               u.mission_id
        FROM conversation_turns u
      )";

    /// <summary>
    /// A page of the vault, plus the counts every folder in the tree needs.
    ///
    /// THE COUNTS ARE COUNTED, NOT INFERRED FROM THE PAGE. A tree that labelled a folder with what
    /// happened to be on the first page would be wrong by construction the moment anything is
    /// filtered — and it is the number an operator uses to decide whether to look.
    /// </summary>
    public VaultPage VaultQuery(string? kind = null, string? query = null, string? projectId = null,
        string? outcome = null, string? chamber = null, string? from = null, string? to = null,
        string grouping = "facet", int limit = 200, int offset = 0)
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();

        void Filter(string clause, string name, object? value)
        {
            where.Add(clause);
            args.Add((name, value));
        }

        if (!string.IsNullOrWhiteSpace(kind)) Filter("kind = @kind", "@kind", kind!.Trim());
        if (!string.IsNullOrWhiteSpace(chamber)) Filter("chamber = @chamber", "@chamber", chamber!.Trim());
        if (!string.IsNullOrWhiteSpace(projectId)) Filter("project_id = @proj", "@proj", projectId!.Trim());
        if (!string.IsNullOrWhiteSpace(outcome)) Filter("COALESCE(outcome,'') = @out", "@out", outcome!.Trim());
        if (!string.IsNullOrWhiteSpace(from)) Filter("COALESCE(when_utc,'') >= @from", "@from", from!.Trim());
        if (!string.IsNullOrWhiteSpace(to)) Filter("COALESCE(when_utc,'') <= @to", "@to", to!.Trim());

        // FULL TEXT MEANS TITLE AND SUBTITLE HERE, and the honesty of that is worth stating: those
        // two carry the mission's goal, the task's title, the event's message and the turn's text,
        // which is where the words an operator half-remembers actually are. Artifact PAYLOADS are
        // not searched — they are JSON, they are the largest column in the database, and a LIKE over
        // them would match field names as readily as content.
        if (!string.IsNullOrWhiteSpace(query))
        {
            where.Add("(title LIKE @q OR COALESCE(subtitle,'') LIKE @q)");
            args.Add(("@q", "%" + query!.Trim() + "%"));
        }

        var filter = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";

        var rows = Query(VaultProjection + $" SELECT vault.*, {VaultGroupExpression(grouping)} AS grp FROM vault" + filter
                       + " ORDER BY COALESCE(when_utc,'') DESC LIMIT @lim OFFSET @off",
            args.Concat(new (string, object?)[]
            {
                ("@lim", Math.Clamp(limit, 1, 2000)),
                ("@off", Math.Max(0, offset)),
            }).ToArray());

        var total = Query(VaultProjection + " SELECT COUNT(*) AS n FROM vault" + filter, args.ToArray())
            .Select(r => RowValues.Int(r, "n")).FirstOrDefault();

        var kinds = Query(VaultProjection + " SELECT kind, COUNT(*) AS n FROM vault" + filter
                        + " GROUP BY kind", args.ToArray())
            .Select(r => new VaultGroupCount(RowValues.Text(r, "kind"),
                VaultKindLabel(RowValues.Text(r, "kind")), RowValues.Int(r, "n")))
            .OrderBy(g => VaultKinds.Contains(g.Key) ? VaultKinds.ToList().IndexOf(g.Key) : 99)
            .ToList();

        return new VaultPage(rows.Select(VaultRecordFrom).ToList(), total, kinds,
            VaultGroups(grouping, filter, args));
    }

    /// <summary>
    /// The tree's SECOND level, and it is switchable because the operator asked for it to be: the
    /// colony's own facets, a derived topic, or the project. One control, three answers to "what is
    /// this folder full of".
    ///
    /// THE FACET IS THE COLONY'S OWN and cannot be wrong — it is a value the colony recorded. The
    /// TOPIC is derived from words and can be, which is why the two are never mixed in one list.
    /// </summary>
    private IReadOnlyList<VaultGroupCount> VaultGroups(string grouping, string filter,
        List<(string, object?)> args)
    {
        return Query(VaultProjection + $" SELECT {VaultGroupExpression(grouping)} AS g, COUNT(*) AS n FROM vault" + filter
                   + " GROUP BY g ORDER BY n DESC LIMIT 40", args.ToArray())
            .Select(r => new VaultGroupCount(RowValues.Text(r, "g"), RowValues.Text(r, "g"), RowValues.Int(r, "n")))
            .Where(g => g.Key.Length > 0)
            .ToList();
    }

    /// <summary>
    /// The second level, as ONE expression. v0.3.9 — read by the folder counts and by each record's
    /// own `group`, so the tree and the chamber cannot fold the same record two different ways.
    /// </summary>
    private static string VaultGroupExpression(string? grouping) =>
        (grouping ?? "").Trim().ToLowerInvariant() switch
        {
            "project" => "CASE WHEN COALESCE(project_id,'') = '' THEN '(no project)' ELSE project_id END",
            "chamber" => "chamber",
            // The first word of the title. A crude topic and honestly a crude one — it is the
            // grouping the operator can SEE is derived, sitting beside two that are the colony's own
            // facts, and it costs no index and no extraction pass to offer.
            "topic" => "LOWER(TRIM(SUBSTR(title, 1, INSTR(title || ' ', ' ') - 1)))",
            _ => "CASE WHEN COALESCE(outcome,'') = '' THEN '(no verdict)' ELSE outcome END",
        };

    /// <summary>
    /// EVERY record, as lean as it goes, for the 3D layer. v0.3.9.
    ///
    /// The operator's answer to "which records are dots" was "everything, no cap", and this is the
    /// call that has to make that true. It carries five fields rather than nine — a dot needs an id,
    /// its kind, its chamber, its verdict and its age, and nothing else until it is clicked.
    ///
    /// It carries a TRIMMED TITLE and its group as well, and those two are what make a dot
    /// clickable without a second round trip: a record picked in the chamber can name itself
    /// immediately, and the clusters the chamber seats it in are the folders the tree filed it
    /// under rather than a second opinion about grouping.
    ///
    /// Bounded anyway, at 50,000, and the bound is a floor under the frame rate rather than a
    /// disagreement with the operator: their colony holds ~15,000, so nothing is being hidden today,
    /// and a colony that reaches the cap gets the newest 50,000 and a count that says so.
    /// </summary>
    public IReadOnlyList<VaultRecord> VaultDots(string grouping = "facet", int limit = 50000) =>
        Query(VaultProjection + $@" SELECT id, kind, chamber, outcome, when_utc,
                                           SUBSTR(title, 1, 80) AS title,
                                           {VaultGroupExpression(grouping)} AS grp,
                                           NULL AS subtitle, project_id, mission_id
                                    FROM vault ORDER BY COALESCE(when_utc,'') DESC LIMIT @lim",
                ("@lim", Math.Clamp(limit, 1, 200000)))
            .Select(VaultRecordFrom).ToList();

    /// <summary>
    /// THE LOCAL GRAPH: what this record is joined to, one hop out.
    ///
    /// FOUR OF THE FIVE RELATIONS ARE FACTS THE COLONY WROTE DOWN — lineage (a mission's tasks, a
    /// task's artifacts), consumption (who read this artifact), evidence and results (what checked
    /// this task), reinforcement (which route a trail is about). The fifth, "same subject", is
    /// DERIVED from words and is flagged as such on every edge it produces, because a line an
    /// operator reads as provenance when it is actually a shared noun is the kind of wrong that
    /// teaches them to distrust the true ones.
    /// </summary>
    public IReadOnlyList<VaultLink> VaultLinks(string id, int limit = 60)
    {
        var (kind, key) = VaultSplit(id);
        if (kind.Length == 0 || key.Length == 0) return Array.Empty<VaultLink>();

        var links = new List<VaultLink>();
        void Add(string sql, string relation, bool derived, params (string, object?)[] parameters)
        {
            foreach (var row in Query(sql, parameters))
                links.Add(new VaultLink(RowValues.Text(row, "id"), RowValues.Text(row, "kind"),
                    RowValues.Text(row, "title"), relation, derived));
        }

        switch (kind)
        {
            case "mission":
                Add("SELECT 'task:' || id AS id, 'task' AS kind, SUBSTR(COALESCE(title,''),1,120) AS title "
                  + "FROM tasks WHERE mission_id = @m ORDER BY created_at LIMIT @lim",
                    "step", false, ("@m", key), ("@lim", limit));
                Add("SELECT 'artifact:' || id AS id, 'artifact' AS kind, COALESCE(schema,'') AS title "
                  + "FROM artifacts WHERE mission_id = @m LIMIT @lim",
                    "produced", false, ("@m", key), ("@lim", limit));
                Add("SELECT 'evidence:' || id AS id, 'evidence' AS kind, COALESCE(kind,'') AS title "
                  + "FROM evidence WHERE mission_id = @m LIMIT @lim",
                    "checked by", false, ("@m", key), ("@lim", limit));
                break;

            case "task":
                Add("SELECT 'mission:' || mission_id AS id, 'mission' AS kind, "
                  + "(SELECT SUBSTR(COALESCE(goal,''),1,120) FROM missions m WHERE m.id = t.mission_id) AS title "
                  + "FROM tasks t WHERE t.id = @t AND mission_id IS NOT NULL",
                    "part of", false, ("@t", key));
                Add("SELECT 'artifact:' || id AS id, 'artifact' AS kind, COALESCE(schema,'') AS title "
                  + "FROM artifacts WHERE task_id = @t LIMIT @lim",
                    "produced", false, ("@t", key), ("@lim", limit));
                Add("SELECT 'result:' || task_id AS id, 'result' AS kind, SUBSTR(COALESCE(summary,''),1,120) AS title "
                  + "FROM task_results WHERE task_id = @t",
                    "result", false, ("@t", key));
                break;

            case "artifact":
                Add("SELECT 'task:' || task_id AS id, 'task' AS kind, "
                  + "(SELECT SUBSTR(COALESCE(title,''),1,120) FROM tasks t WHERE t.id = a.task_id) AS title "
                  + "FROM artifacts a WHERE a.id = @a AND task_id IS NOT NULL",
                    "written by", false, ("@a", key));
                Add("SELECT 'task:' || consumer_task_id AS id, 'task' AS kind, "
                  + "(SELECT SUBSTR(COALESCE(title,''),1,120) FROM tasks t WHERE t.id = c.consumer_task_id) AS title "
                  + "FROM artifact_consumptions c WHERE c.artifact_id = @a AND consumer_task_id IS NOT NULL LIMIT @lim",
                    "read by", false, ("@a", key), ("@lim", limit));
                break;

            case "trail":
                // REINFORCEMENT, and the join is the trail's own key rather than a stored mission
                // list: `ant:builder` is a statement about a ROUTE, and the tasks that took that
                // route are what taught it. `UpdateMissionPheromones` records the movement and not
                // the mover, so this is the honest reconstruction — and it is a fact about the key,
                // not a guess about the text.
                Add("SELECT 'task:' || t.id AS id, 'task' AS kind, SUBSTR(COALESCE(t.title,''),1,120) AS title "
                  + "FROM tasks t, pheromone_trails p WHERE p.id = @p "
                  + "  AND ((p.trail_key LIKE 'ant:%' AND LOWER(t.assigned_ant) = SUBSTR(p.trail_key, 5)) "
                  + "    OR (p.trail_key LIKE 'worker:%' AND LOWER(t.assigned_worker) = SUBSTR(p.trail_key, 8)) "
                  + "    OR (p.trail_key LIKE 'task_type:%' AND LOWER(t.task_type) = SUBSTR(p.trail_key, 11))) "
                  + "ORDER BY t.created_at DESC LIMIT @lim",
                    "reinforced by", false, ("@p", key), ("@lim", limit));

                /* v0.3.9.2 — AND THE KEYS THAT NAME NO COLUMN. `capability:approval_gate` and
                   `source_domain:example.com` are trails about things no task row carries, so the
                   join above finds nothing for them and `.9` left them with no edges at all. Their
                   suffix IS the subject, though, and the derived pass below can say so — labelled
                   as inference, which is exactly what it is. */
                break;

            case "conversation":
                Add("SELECT 'turn:' || id AS id, 'turn' AS kind, SUBSTR(COALESCE(content,''),1,120) AS title "
                  + "FROM conversation_turns WHERE conversation_id = @c ORDER BY ordinal LIMIT @lim",
                    "said", false, ("@c", key), ("@lim", limit));
                break;

            case "event":
            case "result":
            case "evidence":
            case "turn":
                Add("SELECT 'mission:' || mission_id AS id, 'mission' AS kind, "
                  + "(SELECT SUBSTR(COALESCE(goal,''),1,120) FROM missions m WHERE m.id = x.mission_id) AS title "
                  + $"FROM {VaultTable(kind)} x WHERE {VaultKeyColumn(kind)} = @k AND mission_id IS NOT NULL",
                    "part of", false, ("@k", key));
                break;
        }

        // v0.3.9.2 — AND THE FIFTH RELATION, WHICH IS THE ONLY ONE THAT CAN BE WRONG.
        //
        // `.9`'s release notes described five relations, "four of those five facts the colony
        // recorded, the fifth — same subject — inferred from words, drawn DASHED". Four were
        // implemented. The dashed styling shipped with nothing to draw through it: a claim in a
        // changelog with no code under it, which is the defect this release line keeps finding,
        // this time in the notes rather than in the tree.
        //
        // WHAT MAKES IT HONEST IS THAT IT ADMITS WHAT IT IS. It matches on the record's most
        // distinctive word — the longest token over four characters that is not scaffolding — and
        // every edge it returns carries `Derived: true`, so the console dashes it and the operator
        // can tell a shared noun from a provenance link. It also runs LAST and fills only what the
        // recorded relations left of the budget: an inference must never crowd out a fact.
        var room = limit - links.Count;
        var subjects = VaultSubjects(id);

        // NO SUBJECT MEANS NO EDGES, and the early return is the fix for the worse alternative. The
        // first cut returned a sentinel string for a record with nothing distinctive and matched it
        // with LIKE — which joined that record to EVERYTHING, the exact failure an inferred graph
        // has to avoid to be worth drawing. A record whose title is all scaffolding has nothing to
        // say about its subject, and saying nothing is the honest answer.
        if (room > 0 && subjects.Count > 0)
        {
            var clauses = subjects.Select((_, i) => $"title LIKE @t{i}").ToList();
            var parameters = subjects
                .Select((word, i) => ((string, object?))($"@t{i}", "%" + word + "%"))
                .Append(("@self", (object?)id))
                .Append(("@lim", (object?)room))
                .ToArray();

            foreach (var row in Query(
                VaultProjection + $@" SELECT id, kind, title FROM vault
                                      WHERE id <> @self AND ({string.Join(" OR ", clauses)})
                                      ORDER BY COALESCE(when_utc,'') DESC LIMIT @lim", parameters))
            {
                var linkId = RowValues.Text(row, "id");
                if (linkId.Length == 0 || links.Any(l => l.Id == linkId)) continue;
                links.Add(new VaultLink(linkId, RowValues.Text(row, "kind"), RowValues.Text(row, "title"),
                    "same subject", true));
            }
        }

        return links.Take(limit).ToList();
    }

    /// <summary>
    /// THE WORDS THIS RECORD IS ABOUT, as far as words can say. v0.3.9.2.
    ///
    /// Deliberately the crudest thing that is still useful: every alternative — an extractor, an
    /// embedding, a model — makes the colony's memory graph depend on something that can be
    /// unavailable, slow, or differently opinionated between two runs. This is deterministic, costs
    /// nothing, and is labelled as inference everywhere it is shown.
    ///
    /// UP TO THREE WORDS, NOT THE LONGEST ONE. The first cut took the longest token, which is not
    /// the same as the most distinctive: "rotate the wireguard certificates" yielded
    /// `certificates`, and the mission about configuring wireguard — plainly the same subject —
    /// matched nothing. Any of a few candidates matching is the weaker rule and the more useful one.
    ///
    /// EMPTY IS A REAL ANSWER. A title that is all colony scaffolding has nothing to say about its
    /// subject, and the caller draws no edges rather than falling back to something that matches
    /// everything.
    /// </summary>
    private IReadOnlyList<string> VaultSubjects(string id)
    {
        var title = Query(VaultProjection + " SELECT title FROM vault WHERE id = @id", ("@id", id))
            .Select(r => RowValues.Text(r, "title")).FirstOrDefault() ?? "";

        // A TRAIL'S TITLE IS ITS KEY, and the half after the colon is the subject: `ant:builder`
        // is about the builder, `source_domain:example.com` about that domain. Taking the longest
        // word of the whole key would pick the KIND — `source_domain` — and join every trail of
        // that kind to every other.
        var colon = title.IndexOf(':');
        if (colon > 0 && colon < title.Length - 1) title = title[(colon + 1)..];

        return System.Text.RegularExpressions.Regex.Matches(title, @"[A-Za-z][A-Za-z0-9_.-]{4,}")
            .Select(m => m.Value)
            .Where(w => !VaultStopWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // Longest first only as an ORDER, so the most specific candidates are the ones kept when
            // a title has more than three — not as the single answer, which was the first cut's bug.
            .OrderByDescending(w => w.Length)
            .Take(3)
            .ToList();
    }

    /// <summary>
    /// Words that appear in this colony's own scaffolding rather than in what a record is ABOUT.
    /// Every one of them is a term the colony writes into its OWN titles — a subject match on
    /// "mission" would join half the vault to the other half.
    /// </summary>
    private static readonly HashSet<string> VaultStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "mission", "task", "tasks", "colony", "anthill", "record", "records", "result", "results",
        "answer", "request", "report", "verify", "check", "checked", "compile", "assemble",
        "the", "this", "that", "with", "from", "into", "what", "which", "when", "where", "there",
        "their", "them", "then", "than", "have", "has", "was", "were", "been", "being", "does",
        "created", "started", "finished", "complete", "completed", "failed", "running",
        "other", "another", "about", "these", "those", "would", "could", "should", "system",
        "events", "event", "update", "updated", "change", "changes", "changed",
    };

    /// <summary>
    /// KNOWLEDGE THIS COLONY ACTUALLY TOUCHED. v0.3.9 — the operator's answer to how much of a
    /// 6,700-statement knowledge base becomes memory: only the part a mission went and read.
    ///
    /// SERVED SEPARATELY, NOT IN THE UNION, and the reason is paging rather than tidiness. These
    /// records are DERIVED — they are read out of `source_set` payloads by the same parser the
    /// citation gate uses — so SQLite cannot count, sort or page them alongside the rest, and a list
    /// that mixed a paged query with a derived tail would return the wrong page the moment either
    /// side grew. The tree shows this branch as its own folder with its own count, which is what it
    /// honestly is: what the colony has consulted, not what the knowledge base holds.
    ///
    /// One record per distinct statement, carrying the mission that consulted it most recently.
    /// </summary>
    public IReadOnlyList<VaultRecord> VaultKnowledge(int limit = 500)
    {
        var seen = new Dictionary<string, VaultRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in Query(
            @"SELECT id, mission_id, payload, created_at FROM artifacts
              WHERE schema = 'source_set' AND payload LIKE '%knowledge:%'
              ORDER BY created_at DESC LIMIT 400"))
        {
            var payload = RowValues.Text(row, "payload");
            var mission = RowValues.TextOrNull(row, "mission_id");
            var when = RowValues.TextOrNull(row, "created_at");

            foreach (var source in Anthill.SDK.Artifacts.SourceSetPayload.Read(payload))
            {
                if (!Anthill.SDK.Knowledge.KnowledgeCitations.IsKnowledge(source.Url)) continue;
                if (seen.ContainsKey(source.Url)) continue;

                seen[source.Url] = new VaultRecord
                {
                    Id = "knowledge:" + source.Url,
                    Kind = "knowledge",
                    // INTEL, because retrieval is what that half of the colony does. It is not
                    // Memory: this is what the ORGANIZATION knows, consulted by the colony, and the
                    // distinction is the whole reason FORAGER is a separate product.
                    Chamber = VaultChambers.Intel,
                    Title = source.Title.Length > 0 ? source.Title : source.Url,
                    Subtitle = source.Url,
                    WhenUtc = when,
                    Outcome = null,
                    ProjectId = null,
                    MissionId = mission,
                    Href = null,
                };
                if (seen.Count >= limit) return seen.Values.ToList();
            }
        }

        return seen.Values.ToList();
    }

    private static string VaultTable(string kind) => kind switch
    {
        "event" => "events", "result" => "task_results", "evidence" => "evidence",
        "turn" => "conversation_turns", _ => "events",
    };

    private static string VaultKeyColumn(string kind) => kind == "result" ? "task_id" : "id";

    /// <summary>`kind:key`, split on the FIRST colon — ids carry colons of their own.</summary>
    public static (string Kind, string Key) VaultSplit(string? id)
    {
        var at = (id ?? "").IndexOf(':');
        return at <= 0 ? ("", "") : (id![..at], id[(at + 1)..]);
    }

    /// <summary>The one reader of a vault row. See the typed-row doctrine.</summary>
    private static VaultRecord VaultRecordFrom(Dictionary<string, object?> row)
    {
        var kind = RowValues.Text(row, "kind");
        var id = RowValues.Text(row, "id");
        var mission = RowValues.TextOrNull(row, "mission_id");
        return new VaultRecord
        {
            Id = id,
            Kind = kind,
            Chamber = RowValues.Text(row, "chamber"),
            Title = RowValues.Text(row, "title"),
            Subtitle = RowValues.TextOrNull(row, "subtitle"),
            WhenUtc = RowValues.TextOrNull(row, "when_utc"),
            Outcome = RowValues.TextOrNull(row, "outcome"),
            ProjectId = RowValues.TextOrNull(row, "project_id"),
            MissionId = mission,
            Group = RowValues.TextOrNull(row, "grp"),
            Href = kind switch
            {
                "mission" => "/projects",
                "conversation" or "turn" => "/chat",
                _ => mission is { Length: > 0 } ? "/projects" : null,
            },
        };
    }
}
