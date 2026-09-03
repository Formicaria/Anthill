/* ─────────────────────────────────────────────────────────────────────────────
   COLONY TOPOLOGY — the normalized Colony Live model. v0.3.8.115.

   ONE MODEL, TWO RENDERERS. This file owns what is TRUE. `colony-renderer.js`
   (WebGL) and the classic canvas both consume the scene it emits, and neither
   may decide anything: a renderer answers "how should this state look", never
   "what is the state".

   IT NEVER FETCHES. app.js already polls /graph, /colony/registry and the
   approvals list and already holds one /events/stream subscription; this file
   is fed from those handlers. A second poll or a second SSE connection is the
   one thing the feature is not allowed to add.

   ── WHAT `.111` GOT WRONG, AND WHY EACH IS NAMED HERE ───────────────────────
   The previous version of this file was honest in its header and not in its
   body. Every item below was live in production:

   1. A hand-written `SECTOR_OF` map of ~22 role ids — a second store of a fact
      the registry already owns (`AntRoleDefinition.Colony`). Anything it did
      not name resolved to null, and the records path read
      `sectorOfAnt(ant) || 'queen'`: every role added after the map was last
      edited, and every plugin-contributed role, was silently filed under the
      QUEEN. Now: sectors arrive from /colony/live/snapshot, and an unknown role
      lands in `unassigned`, visibly.

   2. Routes built by filtering a hard-coded `SECTOR_SEQUENCE`. That is a
      picture of how work is SUPPOSED to flow, drawn as though it were what
      happened. Now: routes come from persisted task edges only.

   3. `pausedForApproval = true` for ANY approval, with the ant parked on the
      last route segment. An approval belongs to one task; attaching it to
      whatever was last drawn is a guess presented as a fact. Now: resolved to
      its exact mission/task/role, or shown as unresolved Queen attention.

   4. `evidenceReturn` whenever any validation task was complete — an inference,
      not a record. Gone; evidence moves only when an event says it did.

   5. Ant positions and speeds invented (`t: 0.2 + ants.length * .25`,
      `sp: .0002`) — a travel animation presented as progress. Now: an ant is
      docked at its real context and moves once per recorded transition.

   6. Every one of the last 120 events turned into a "record" with
      `verif: 'recorded'`. The file DECLARED a `RECORD_EVENTS` regex for exactly
      this and never called it. Now the server answers, per event, on the wire
      (`event_type_creates_record`) so there is one implementation of the rule.

   7. No event de-duplication at all, while the stream is explicitly designed to
      replay. Now: dedup by the stable event id the wire has always carried.
   ───────────────────────────────────────────────────────────────────────────── */
(function () {
  'use strict';

  /* ── Deterministic placement ───────────────────────────────────────────────
     Record positions must be stable: the same snapshot must produce the same
     layout, or a re-render reshuffles the colony under the operator's cursor
     and "the third particle from the left" stops meaning anything.

     FNV-1a over the record id. This positions; it never creates a fact. A hash
     decides WHERE a real record sits, never WHETHER it exists or what it says.
     `Math.random()` appears nowhere in this file, and a guard enforces that. */
  function hash32(str) {
    var h = 2166136261, s = String(str == null ? '' : str);
    for (var i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = (h * 16777619) >>> 0; }
    return h >>> 0;
  }
  /** Three independent unit values in [0,1) from one id — enough for a point on a shell. */
  function placement(id) {
    var a = hash32('x:' + id), b = hash32('y:' + id), c = hash32('z:' + id);
    return { a: a / 4294967296, b: b / 4294967296, c: c / 4294967296 };
  }

  /* ── The transition vocabulary ─────────────────────────────────────────────
     Movement happens ONLY for these, once per unique event id. Every name is a
     real `EventTypes` constant; none is inferred from a sector ordering.

     `task_started` is the dispatch: it names the mission, the task and the ant,
     so the destination is known. The others are recorded transitions between
     workers. A type absent here still reaches the event feed — it simply does
     not move anything, which is the default a truthful view needs. */
  var TRANSITION_EVENTS = {
    task_started: 'dispatch',
    handoff_admitted: 'handoff',
    task_rerouted: 'reroute',
    adaptive_escalated: 'escalation',
    micromound_mission_dispatched: 'physical_dispatch'
  };

  /** Retained client history. Bounded so a long-running tab cannot grow without limit (§10). */
  var MAX_RECORDS = 600;
  var MAX_SEEN = 4000;
  var MAX_TRANSITIONS = 200;

  function create() {
    var listeners = [];
    var st = {
      // From /colony/live/snapshot — the authority for sector membership.
      sectors: [], roleSector: {}, unassignedId: 'unassigned', runtime: null,
      snapshotAt: null, watermark: null, hydrated: false,

      // Buffered while hydrating, so an event that lands between "ask for the
      // snapshot" and "receive it" is applied after it rather than lost (§10).
      buffer: [],

      graph: null, approvals: [], mound: null,

      records: [],                 // persisted record-creating events only
      recordIds: Object.create(null),
      seen: Object.create(null),   // event-id dedup
      seenOrder: [],
      transitions: [],             // one-shot, event-backed
      truncated: false
    };

    function onScene(fn) { listeners.push(fn); }
    function publish() { var s = project(); listeners.forEach(function (fn) { fn(s); }); }

    /** The sector a role id belongs to. Unknown → `unassigned`, NEVER queen, never guessed. */
    function sectorOfRole(roleId) {
      if (!roleId) return st.unassignedId;
      var key = String(roleId).toLowerCase();
      return Object.prototype.hasOwnProperty.call(st.roleSector, key)
        ? st.roleSector[key] : st.unassignedId;
    }

    /* ── Hydration ─────────────────────────────────────────────────────────── */
    function applySnapshot(snap) {
      if (!snap) return;
      st.sectors = Array.isArray(snap.sectors) ? snap.sectors : [];
      st.runtime = snap.runtime || null;
      st.snapshotAt = snap.snapshot_at || null;
      st.unassignedId = snap.unassigned_sector || 'unassigned';
      st.watermark = (snap.watermark && snap.watermark.event_id) || null;

      st.roleSector = Object.create(null);
      st.sectors.forEach(function (sec) {
        (sec.residents || sec.Residents || []).forEach(function (r) {
          var id = r.roleId || r.RoleId;
          if (id) st.roleSector[String(id).toLowerCase()] = sec.sectorId || sec.SectorId;
        });
      });

      st.hydrated = true;
      // Everything that arrived while we were waiting, in order, each exactly once.
      var pending = st.buffer; st.buffer = [];
      pending.forEach(accept);
      publish();
    }

    /* ── The idempotent event reducer ──────────────────────────────────────── */
    function accept(ev) {
      if (!ev) return false;

      // Identity is the persisted event id and nothing else. Never the message
      // text, which is display, repeats, and is not stable across a rename.
      var id = ev.id || ev.event_id;
      if (!id) return false;
      if (st.seen[id]) return false;

      // Before the watermark the snapshot already accounted for it. The stream
      // replays its last 50 rows on every connect, so this is the ordinary path
      // after a reconnect, not an edge case.
      if (st.watermark && String(id) === String(st.watermark)) { remember(id); return false; }

      remember(id);

      // A record particle appears only when the colony STORED something, and
      // the server decided that — see `event_type_creates_record` on the wire.
      if (ev.event_type_creates_record) addRecord(ev, id);

      var kind = TRANSITION_EVENTS[ev.event_type];
      if (kind) addTransition(ev, id, kind);

      return true;
    }

    function remember(id) {
      st.seen[id] = true;
      st.seenOrder.push(id);
      while (st.seenOrder.length > MAX_SEEN) delete st.seen[st.seenOrder.shift()];
    }

    function addRecord(ev, id) {
      if (st.recordIds[id]) return;
      st.recordIds[id] = true;
      st.records.push({
        recordId: id,
        sector: sectorOfRole(ev.ant_name),
        recordType: ev.event_type || '',
        title: ev.message || ev.event_type || '',
        ant: ev.ant_name || '',
        missionId: ev.mission_id || '',
        taskId: ev.task_id || '',
        createdAt: ev.created_at || '',
        place: placement(id)
      });
      while (st.records.length > MAX_RECORDS) {
        var dropped = st.records.shift();
        delete st.recordIds[dropped.recordId];
      }
    }

    /* A transition names where it ARRIVED. It names where it came from only
       when the event's own metadata does — inferring a source from "the sector
       before this one" is the defect this replaced. Without a source the
       renderer shows an arrival, not a journey. */
    function addTransition(ev, id, kind) {
      var meta = ev.metadata || {};
      var toRole = ev.ant_name || meta.to_worker || meta.to_role || '';
      var fromRole = meta.from_worker || meta.from_role || meta.previous_worker || '';

      st.transitions.push({
        id: id,
        kind: kind,
        from: fromRole ? sectorOfRole(fromRole) : null,
        to: sectorOfRole(toRole),
        role: toRole || '',
        missionId: ev.mission_id || '',
        taskId: ev.task_id || '',
        at: ev.created_at || ''
      });
      while (st.transitions.length > MAX_TRANSITIONS) st.transitions.shift();
    }

    /* ── Routes: persisted task edges only ─────────────────────────────────── */
    function tasksOf(graph) {
      if (!graph) return [];
      return Array.isArray(graph.nodes) ? graph.nodes : (Array.isArray(graph.tasks) ? graph.tasks : []);
    }
    function roleOfTask(t) { return t.assigned_worker || t.assigned_ant || t.ant || t.role || ''; }

    /** Sector-to-sector links, derived from real `depends_on`/`parent_task` edges. */
    function missionEdges(graph) {
      var tasks = tasksOf(graph), edges = (graph && graph.edges) || [];
      if (!tasks.length || !edges.length) return [];

      var sectorOfTask = Object.create(null);
      tasks.forEach(function (t) {
        var id = t.task_id || t.id;
        if (id) sectorOfTask[id] = sectorOfRole(roleOfTask(t));
      });

      var seen = Object.create(null), out = [];
      edges.forEach(function (e) {
        var a = sectorOfTask[e.from], b = sectorOfTask[e.to];
        if (!a || !b || a === b) return;   // a link within one chamber is not a route
        var key = a + '>' + b;
        if (seen[key]) return;
        seen[key] = true;
        out.push({ from: a, to: b, kind: 'mission', edgeType: e.type || 'depends_on' });
      });
      return out;
    }

    /* ── Approvals: exact, or explicitly unresolved ────────────────────────── */
    function projectApprovals() {
      return (st.approvals || []).map(function (a) {
        var role = a.assigned_worker || a.assigned_ant || a.requested_by || '';
        var taskId = a.task_id || a.target_id || '';
        var missionId = a.mission_id || '';

        // Resolved means the colony can name WHERE this approval sits. A role we
        // can place plus a task it belongs to is the whole bar; anything less is
        // reported as unresolved rather than attached to the nearest route.
        var known = !!(role && Object.prototype.hasOwnProperty.call(st.roleSector, String(role).toLowerCase()));
        var resolved = !!(known && taskId);

        return {
          approvalId: a.id || a.approval_id || '',
          missionId: missionId,
          taskId: taskId,
          role: role,
          actionType: a.action_type || '',
          title: a.title || '',
          // Unresolved approvals surface at the Queen as ATTENTION — which is
          // true (she is the authority that must answer) — and are flagged so
          // the renderer never draws them as a boundary on a route.
          sector: resolved ? sectorOfRole(role) : 'queen',
          resolved: resolved
        };
      });
    }

    /* ── The mound, as the fleet listing literally states it ───────────────────
       NO DERIVED STATUS. `MicromoundWidgets.StatusOf` decides online/offline/
       quiesced from the last beat, the sync interval and the configured missed-
       beat grace, and it lives on the server beside the options it reads. The
       fleet listing does not currently carry its verdict, so this view shows the
       RECORDED fields — last seen, stopped, quiesced, charter, lease — and lets
       the operator read them. Recomputing the verdict here would be a second
       implementation of one rule (defect class 5) that silently disagrees with
       the server the moment the grace configuration changes.

       A mound node exists only when the fleet listing returned one. There is no
       placeholder mound, and a colony with no devices has no mound chamber. */
    function projectMound() {
      var fleet = st.mound;
      if (!fleet) return null;

      var items = fleet.items || fleet.Items || [];
      if (!Array.isArray(items) || !items.length) {
        // The route answered and the fleet is empty. That is a FACT worth
        // carrying — it is how the view says "no mounds" rather than "unknown".
        return { present: false, globalStop: !!(fleet.global_stop), commandPath: !!(fleet.command_path), mounds: [] };
      }

      return {
        present: true,
        globalStop: !!(fleet.global_stop),
        commandPath: !!(fleet.command_path),
        mounds: items.map(function (m) {
          return {
            moundId: m.mound_id || m.moundId || '',
            name: m.name || m.Name || '',
            // `edge_queen` is the WIRE value. The UI shows "Mound Major"; this
            // carries the identifier so the technical panel can state it exactly.
            tier: m.tier || m.Tier || '',
            enrolled: !!(m.public_key || m.publicKey),
            capabilities: m.capabilities || m.Capabilities || [],
            lastSeen: m.last_seen || m.lastSeen || '',
            lastSeq: (m.last_seq !== undefined ? m.last_seq : m.lastSeq),
            stopped: !!(m.stopped !== undefined ? m.stopped : m.Stopped),
            quiesced: !!(m.quiesced !== undefined ? m.quiesced : m.Quiesced),
            charterId: m.charter_id || m.charterId || '',
            charterExpiresAt: m.charter_expires_at || m.charterExpiresAt || '',
            leaseExpiresAt: m.lease_expires_at || m.leaseExpiresAt || ''
          };
        })
      };
    }

    function project() {
      var graph = st.graph;
      var tasks = tasksOf(graph);

      // Per-sector state, derived from real task status. No clock, no progress.
      var running = Object.create(null);
      tasks.forEach(function (t) {
        if (t.status !== 'running') return;
        var sec = sectorOfRole(roleOfTask(t));
        (running[sec] = running[sec] || []).push({
          taskId: t.task_id || t.id || '', missionId: t.mission_id || '', role: roleOfTask(t)
        });
      });

      var records = Object.create(null);
      st.records.forEach(function (r) { (records[r.sector] = records[r.sector] || []).push(r); });

      var sectors = st.sectors.map(function (sec) {
        var id = sec.sectorId || sec.SectorId;
        var residents = (sec.residents || sec.Residents || []).map(function (r) {
          var roleId = r.roleId || r.RoleId;
          var busy = (running[id] || []).some(function (x) {
            return String(x.role).toLowerCase() === String(roleId).toLowerCase();
          });
          return {
            roleId: roleId,
            name: r.displayName || r.DisplayName || roleId,
            colony: r.colony || r.Colony || '',
            enabled: r.enabled !== undefined ? r.enabled : r.Enabled,
            executable: r.executable !== undefined ? r.executable : r.Executable,
            // The only three states an ant may show. `working` requires a real
            // running task assigned to THIS role — never a guess, never a timer.
            status: busy ? 'working' : ((r.enabled !== undefined ? r.enabled : r.Enabled) ? 'idle' : 'disabled')
          };
        });
        return {
          id: id,
          label: sec.label || sec.Label || id,
          colonies: sec.colonies || sec.Colonies || [],
          residents: residents,
          runningTasks: running[id] || [],
          records: records[id] || [],
          recordCount: (records[id] || []).length
        };
      });

      return {
        sectors: sectors,
        edges: missionEdges(graph),
        transitions: st.transitions.slice(),
        approvals: projectApprovals(),
        mound: projectMound(),
        meta: {
          hydrated: st.hydrated,
          snapshotAt: st.snapshotAt,
          watermark: st.watermark,
          // True when the client has dropped older records to stay bounded, so
          // the view can say "partial" instead of implying the colony is small.
          partialHistory: st.truncated || st.records.length >= MAX_RECORDS,
          runtime: st.runtime
        }
      };
    }

    /* ── §14 COLONY GROWTH PLAYBACK — reconstruction, not re-enactment ─────────
       WHAT A HISTORICAL FRAME CAN HONESTLY CONTAIN, and what it must not.

       Two things in this model carry their own timestamps and are therefore
       reconstructable: persisted RECORDS (`created_at`, from the events table)
       and recorded TRANSITIONS (the event's own `created_at`). A frame at time T
       shows those, filtered to T, and nothing else is invented to fill it.

       Three things deliberately go EMPTY in a historical frame, because the
       model holds only their present value and a past frame showing a present
       value is the exact lie playback exists to avoid:

         · running tasks — `/graph` reports status NOW; there is no per-task
           status history in this model, so an ant is never shown "working" in
           the past. It shows idle-or-disabled, from the registry.
         · approvals    — the queue is a current queue. An approval raised five
           minutes ago and answered since did not exist "pending at T" as far as
           anything here can prove.
         · the mound    — the fleet listing is a snapshot of now.

       And one thing is shown but LABELLED: the chambers themselves. Sector
       membership comes from the live registry and has no history, so a frame
       from three hours ago is drawn in today's chambers. `sectorsAreCurrent`
       says so, and the HUD prints it — a caveat the operator can read beats a
       reconstruction that quietly pretends the colony never changed shape.

       Finally the coverage caveat: many colony events reach the SSE bus without
       ever being persisted (every Micromound event among them). Those cannot be
       replayed on reconnect and cannot appear here at all. `coverage` states
       that plainly rather than letting a sparse timeline read as a quiet colony. */
    function historyBounds() {
      var lo = null, hi = null;
      st.records.forEach(function (r) {
        if (!r.createdAt) return;
        if (lo === null || r.createdAt < lo) lo = r.createdAt;
        if (hi === null || r.createdAt > hi) hi = r.createdAt;
      });
      st.transitions.forEach(function (t) {
        if (!t.at) return;
        if (lo === null || t.at < lo) lo = t.at;
        if (hi === null || t.at > hi) hi = t.at;
      });
      return {
        from: lo, to: hi,
        records: st.records.length,
        transitions: st.transitions.length,
        // No timestamps at all means there is nothing to scrub. The HUD must
        // offer no timeline rather than an empty one that looks broken.
        available: lo !== null && hi !== null && lo !== hi
      };
    }

    /** A scene as it stood at `iso`, in the same shape `project()` emits. */
    function historyAt(iso) {
      var at = String(iso || '');
      var s = project();

      var records = st.records.filter(function (r) { return r.createdAt && r.createdAt <= at; });
      var bySector = Object.create(null);
      records.forEach(function (r) { (bySector[r.sector] = bySector[r.sector] || []).push(r); });

      s.sectors = s.sectors.map(function (sec) {
        return {
          id: sec.id,
          label: sec.label,
          colonies: sec.colonies,
          residents: sec.residents.map(function (r) {
            return {
              roleId: r.roleId, name: r.name, colony: r.colony,
              enabled: r.enabled, executable: r.executable,
              // Never `working` in the past — see the note above.
              status: r.enabled ? 'idle' : 'disabled'
            };
          }),
          runningTasks: [],
          records: bySector[sec.id] || [],
          recordCount: (bySector[sec.id] || []).length
        };
      });

      s.transitions = st.transitions.filter(function (t) { return t.at && t.at <= at; });
      s.approvals = [];
      s.mound = null;
      s.meta = Object.assign({}, s.meta, {
        history: {
          at: at,
          live: false,
          sectorsAreCurrent: true,
          recordsShown: records.length,
          recordsHeld: st.records.length,
          coverage: 'persisted records and recorded transitions only; events that '
                  + 'reach the stream without being written are not reconstructable'
        }
      });
      return s;
    }

    return {
      onScene: onScene,
      applySnapshot: function (s) { applySnapshot(s); },
      ingestGraph: function (g) { st.graph = g; publish(); },
      ingestApprovals: function (a) { st.approvals = a || []; publish(); },
      ingestMound: function (m) { st.mound = m; publish(); },
      ingestRecords: function (page) {
        // The bounded records read. Same dedup path as the stream, so a record
        // fetched here and then replayed on the stream lands exactly once.
        if (!page) return;
        if (page.scan_truncated) st.truncated = true;
        (page.items || []).forEach(function (it) {
          var id = it.record_id;
          if (!id || st.recordIds[id]) return;
          st.recordIds[id] = true;
          st.records.push({
            recordId: id, sector: it.sector || st.unassignedId, recordType: it.record_type || '',
            title: it.title || '', ant: it.ant || '', missionId: it.mission_id || '',
            taskId: it.task_id || '', createdAt: it.created_at || '', place: placement(id)
          });
        });
        publish();
      },
      /** Returns true when the event changed the model — for tests and for the renderer's dirty flag. */
      ingestEvent: function (ev) {
        if (!st.hydrated) { st.buffer.push(ev); return false; }
        var changed = accept(ev);
        if (changed) publish();
        return changed;
      },
      /** A reconnect replays; nothing else changes. The reducer already ignores what it has seen. */
      onReconnect: function () { publish(); },
      sectorOfRole: sectorOfRole,
      project: project,
      /** §14. `historyAt` is READ-ONLY: it derives a frame and mutates nothing. */
      historyBounds: historyBounds,
      historyAt: historyAt
    };
  }

  window.ColonyTopology = { create: create, hash32: hash32, placement: placement };
})();
