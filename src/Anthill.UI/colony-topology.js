/* ─────────────────────────────────────────────────────────────────────────────
   COLONY TOPOLOGY — the projection layer (design doc §14).
   Owns state: consumes the SAME data the classic canvas already holds (the
   /graph poll, /colony/registry, the /approvals poll, the /events/stream
   subscription) and emits a declarative scene for a renderer (ColonyLive
   today, three.js later, classic 2D as fallback). It never invents activity:
   everything in the scene traces to a backend fact.

   Integration: app.js CALLS the ingest* methods from its existing poll/SSE
   handlers with the data it already has — never a second fetch (the boundary
   rule). There is no fetch in this file.

     const topo = ColonyTopology.create();
     topo.onScene(scene => live.setTopology(scene));
     topo.ingestGraph(graphData);        // from the existing /graph poll
     topo.ingestRegistry(registryData);  // from /colony/registry
     topo.ingestEvent(evt);              // from the /events/stream handler
     topo.ingestApprovals(approvals);    // from the approvals poll

   Wire shapes this file reads (Queen.BuildTaskGraphData / SqliteMemory.LogEvent):
     graph  = { nodes:[{ task_id, assigned_ant, assigned_worker, status, ... }], edges:[...] }
     event  = { event_type, ant_name, message, mission_id, task_id, created_at, level }
   ───────────────────────────────────────────────────────────────────────────── */
(function () {
  'use strict';
  // Caste → sector mapping (design doc §3). Extend here, never in the renderer. Keys are the
  // role ids the registry actually reports (AntExecutorCatalog), lower-cased.
  var SECTOR_OF = {
    queen: 'queen', director: 'queen', planner: 'queen', constraint: 'queen',
    researcher: 'intel', web: 'intel', scout: 'intel', ui_cartographer: 'intel',
    coder: 'forge', acting_coder: 'forge', builder: 'forge', file: 'forge', worker: 'forge',
    verifier: 'valid', tester: 'valid', soldier: 'valid', medic: 'valid',
    archivist: 'memory', memory: 'memory', scribe: 'memory',
    output: 'output', forager: 'output'
  };
  var SECTOR_SEQUENCE = ['queen', 'intel', 'forge', 'valid'];

  // Event types that mean the backend WROTE a durable record (EventTypes.cs). Only these grow a
  // sector's shell — addRecordPoint() must never fire on chatter (honesty constraint).
  var RECORD_EVENTS = /(_recorded|_stored|_written)$|^memory_candidate$|^pheromone_scored$|^verification_bound_to_evidence$|^mission_evaluated$|^mission_outcome$/;

  function sectorOfAnt(name) { return SECTOR_OF[String(name || '').toLowerCase()] || null; }
  function antOf(t) { return t.ant || t.assigned_worker || t.assigned_ant || t.role || ''; }
  function tasksOf(graph) {
    if (!graph) return [];
    return Array.isArray(graph.tasks) ? graph.tasks : (Array.isArray(graph.nodes) ? graph.nodes : []);
  }
  /** Sector a record-creating event lands in, or null when the event created no record. */
  function recordSectorOf(ev) {
    if (!ev || !RECORD_EVENTS.test(String(ev.event_type || ''))) return null;
    return sectorOfAnt(ev.ant_name || ev.actor || ev.ant) || 'memory';
  }

  function create() {
    var listeners = [], state = { graph: null, registry: null, approvals: [], events: [], mound: null };
    function onScene(fn) { listeners.push(fn); }
    function publish() {
      var scene = project(state);
      listeners.forEach(function (fn) { fn(scene); });
    }
    function project(st) {
      var scene = { route: null, pausedForApproval: false, evidenceReturn: null, ants: [], records: {}, attention: [], mound: st.mound };
      var tasks = tasksOf(st.graph);
      // Route: the sectors that hold RUNNING (or queued) tasks, ordered by the canonical sequence.
      if (tasks.length) {
        var activeSectors = {};
        tasks.forEach(function (t) {
          if (t.status !== 'running' && t.status !== 'ready' && t.status !== 'pending') return;
          var sec = sectorOfAnt(antOf(t));
          if (sec) activeSectors[sec] = true;
        });
        var route = SECTOR_SEQUENCE.filter(function (s) { return s === 'queen' || activeSectors[s]; });
        if (route.length > 1) scene.route = route;
        // Ants: one per running task's caste, riding the segment into its sector.
        var segIdx = {}; (scene.route || []).forEach(function (s, i) { if (i > 0) segIdx[s] = i - 1; });
        var seen = {};
        tasks.forEach(function (t) {
          if (t.status !== 'running') return;
          var ant = antOf(t), sec = sectorOfAnt(ant);
          if (sec == null || segIdx[sec] == null || seen[ant]) return;
          seen[ant] = true;
          scene.ants.push({ seg: segIdx[sec], t: 0.2 + Math.min(.6, (scene.ants.length * .25)), sp: .0002, paused: false, label: ant });
        });
      }
      // Approvals: pause the route at its last segment; surface attention; park the proposing ant.
      if (st.approvals && st.approvals.length) {
        scene.pausedForApproval = true;
        scene.attention.push({ kind: 'approval', count: st.approvals.length });
        scene.ants.push({ seg: Math.max(0, (scene.route || []).length - 2), t: .86, sp: 0, paused: true, label: 'awaiting approval' });
      }
      // Evidence return: completed verification tasks flow valid → memory.
      if (tasks.some(function (t) { return sectorOfAnt(antOf(t)) === 'valid' && /complete/.test(t.status || ''); })) {
        scene.evidenceReturn = ['valid', 'memory'];
        scene.ants.push({ seg: -1, t: .3, sp: .00016, paused: false, gold: true, label: 'evidence' });
      }
      // Records: recent events become readable shell records per sector (truthful facts only —
      // event_type, ant, timestamp, message. The deep context-record index is contract §19).
      st.events.slice(-120).forEach(function (ev) {
        var ant = ev.ant_name || ev.actor || ev.ant || '';
        var sec = sectorOfAnt(ant) || 'queen';
        (scene.records[sec] = scene.records[sec] || []).push({
          title: ev.message || ev.summary || ev.event_type || 'event',
          type: ev.event_type || 'event', ant: ant || '—',
          mission: ev.mission_id != null ? String(ev.mission_id).slice(0, 8) : (ev.job_id != null ? '#' + ev.job_id : '—'),
          time: ev.created_at || ev.timestamp || '—', verif: 'recorded', phero: null, rel: []
        });
      });
      return scene;
    }
    return {
      onScene: onScene,
      ingestGraph: function (g) { state.graph = g; publish(); },
      ingestRegistry: function (r) { state.registry = r; publish(); },
      ingestApprovals: function (a) { state.approvals = a || []; publish(); },
      ingestEvent: function (e) { state.events.push(e); if (state.events.length > 400) state.events.shift(); publish(); },
      ingestMound: function (m) { state.mound = m; publish(); },
      project: function () { return project(state); }
    };
  }
  window.ColonyTopology = { create: create, sectorOfAnt: sectorOfAnt, recordSectorOf: recordSectorOf };
})();
